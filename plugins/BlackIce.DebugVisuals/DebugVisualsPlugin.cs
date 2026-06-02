using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace BlackIce.DebugVisuals;

/// <summary>
/// CLIENT-side BepInEx mod that draws debug overlays in the game world with immediate-mode <see cref="GL"/>
/// lines, toggled by IN-CHAT "/dbg" commands (handled locally — never sent to the server or other players):
///   <c>/dbg grid</c> · <c>/dbg nav</c> · <c>/dbg col</c> · <c>/dbg ent</c> · <c>/dbg all</c> · <c>/dbg off</c>
/// (bare <c>/dbg</c> shows status). Overlays: a ground GRID, the game's baked NAVMESH (the pathfinding
/// surface, <see cref="NavMesh.CalculateTriangulation"/>), COLLIDER bounds (the collision the world is built
/// from), and markers + lines to networked ENTITIES (every PUN <see cref="PhotonView"/> — players/bots/loot).
///
/// <para>Clean-room: Unity + PUN PUBLIC APIs only. The "/dbg" commands are intercepted by a Harmony patch on
/// <see cref="PhotonView.RPC(string, RpcTarget, object[])"/> (the chat send), so they toggle locally and are
/// suppressed before reaching the server. Rendering uses the rendering camera (<see cref="Camera.current"/>)
/// so it works even when no camera is tagged MainCamera.</para>
/// </summary>
[BepInPlugin("blackice.debugvisuals", "BlackIce Debug Visuals", "0.2.0")]
public sealed class DebugVisualsPlugin : BaseUnityPlugin
{
    internal static DebugVisualsPlugin? Instance;

    private ConfigEntry<float> _drawDistance = null!, _gridSize = null!, _refreshSeconds = null!, _heatMaxCost = null!;
    private ConfigEntry<bool> _throughWalls = null!;

    private bool _grid, _nav, _colliders, _entities, _heat;
    private readonly List<Renderer> _tinted = new();
    private MaterialPropertyBlock? _block;
    private Material? _mat;
    private NavMeshTriangulation _navMesh;
    private Collider[] _colliderCache = Array.Empty<Collider>();
    private float _nextRefresh;
    private string _status = "";
    private float _statusUntil;

    private void Awake()
    {
        Instance = this;
        _drawDistance = Config.Bind("Tuning", "DrawDistance", 80f, "Max distance from the camera to draw colliders/entities.");
        _gridSize = Config.Bind("Tuning", "GridSize", 100f, "Half-extent (units) of the ground grid around the camera.");
        _refreshSeconds = Config.Bind("Tuning", "RefreshSeconds", 1.0f, "How often to re-scan navmesh/colliders/heatmap.");
        _throughWalls = Config.Bind("Tuning", "ThroughWalls", true, "Draw overlays through geometry (ZTest Always).");
        _heatMaxCost = Config.Bind("Tuning", "HeatMaxCost", 20000f, "Estimated per-object cost that maps to full RED in the perf heatmap.");

        try { new Harmony("blackice.debugvisuals").PatchAll(typeof(ChatInterceptor)); }
        catch (Exception ex) { Logger.LogWarning($"chat-command interception unavailable ({ex.Message}); overlays still work if toggled by code."); }

        Logger.LogInfo("BlackIce Debug Visuals armed — type /dbg in chat: grid | nav | col | ent | all | off.");
    }

    /// <summary>Handles a "/dbg ..." chat command locally (called from the chat-send Harmony patch).</summary>
    internal void HandleChat(string text)
    {
        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        switch (sub)
        {
            case "grid": _grid = !_grid; break;
            case "nav": case "navmesh": _nav = !_nav; if (_nav) Refresh(); break;
            case "col": case "colliders": _colliders = !_colliders; if (_colliders) Refresh(); break;
            case "ent": case "entities": _entities = !_entities; break;
            case "perf": case "heat": case "heatmap": ToggleHeat(); break;
            case "all": _grid = _nav = _colliders = _entities = true; Refresh(); break;
            case "off": _grid = _nav = _colliders = _entities = false; if (_heat) ToggleHeat(); break;
            default: break;   // bare /dbg → just show status
        }
        _status = $"DBG  grid:{On(_grid)} nav:{On(_nav)} col:{On(_colliders)} ent:{On(_entities)} perf:{On(_heat)}   " +
                  "(/dbg grid|nav|col|ent|perf|all|off)";
        _statusUntil = Time.unscaledTime + 6f;
        Logger.LogInfo(_status);
    }

    private static string On(bool b) => b ? "ON" : "off";

    private void Refresh()
    {
        _nextRefresh = Time.unscaledTime + Mathf.Max(0.1f, _refreshSeconds.Value);
        try { if (_nav) _navMesh = NavMesh.CalculateTriangulation(); } catch { /* no baked navmesh */ }
        try { if (_colliders) _colliderCache = FindObjectsOfType<Collider>(); } catch { _colliderCache = Array.Empty<Collider>(); }
    }

    private void Update()
    {
        if ((_nav || _colliders) && Time.unscaledTime >= _nextRefresh) Refresh();
        if (_heat && Time.unscaledTime >= _nextHeat) ApplyHeatmap();
    }

    // --- Performance heatmap: tint every renderer green→red by an estimated per-object cost ---------------

    private float _nextHeat;

    private void ToggleHeat()
    {
        _heat = !_heat;
        if (_heat) ApplyHeatmap();
        else ClearHeatmap();
    }

    /// <summary>Scans all renderers and tints each (via a non-destructive <see cref="MaterialPropertyBlock"/>)
    /// on a green→yellow→red scale by an ESTIMATED cost: geometry (vertices), material count, script/component
    /// count, and a bump for per-frame-dynamic systems (skinned mesh, animator, particles, non-kinematic
    /// rigidbody, MeshCollider, real-time light). This is a static complexity proxy for hunting hotspots, not a
    /// live CPU profile — but it reliably surfaces the heavy meshes/objects to optimize.</summary>
    private void ApplyHeatmap()
    {
        _nextHeat = Time.unscaledTime + Mathf.Max(0.25f, _refreshSeconds.Value);
        ClearHeatmap();
        _block ??= new MaterialPropertyBlock();
        float max = Mathf.Max(1f, _heatMaxCost.Value);
        foreach (var r in FindObjectsOfType<Renderer>())
        {
            if (r is null) continue;
            float t = Mathf.Clamp01(Mathf.Sqrt(CostOf(r) / max));   // sqrt so mid-cost reads orange, not green
            var c = Heat(t);
            r.GetPropertyBlock(_block);
            _block.SetColor("_Color", c);          // built-in/standard pipeline tint
            _block.SetColor("_BaseColor", c);      // URP/HDRP tint
            _block.SetColor("_EmissionColor", c * (t * 2f));   // make hotspots glow
            r.SetPropertyBlock(_block);
            _tinted.Add(r);
        }
        _status = $"DBG  perf heatmap: tinted {_tinted.Count} renderers (green=cheap → red=hot, max={max:0})";
        _statusUntil = Time.unscaledTime + 6f;
    }

    private void ClearHeatmap()
    {
        var empty = new MaterialPropertyBlock();
        foreach (var r in _tinted) if (r is not null) r.SetPropertyBlock(empty);   // restore original appearance
        _tinted.Clear();
    }

    /// <summary>Cheap per-object cost estimate (no per-frame allocation: uses vertexCount, not the triangle array).</summary>
    private static float CostOf(Renderer r)
    {
        var go = r.gameObject;
        float cost = r.sharedMaterials.Length * 400f;                 // each material/draw-call submesh
        cost += go.GetComponents<MonoBehaviour>().Length * 150f;      // script/Update load proxy

        Mesh? mesh = (r as SkinnedMeshRenderer)?.sharedMesh ?? go.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh is not null) cost += mesh.vertexCount;               // geometry (vertex count ≈ tris, no alloc)

        if (r is SkinnedMeshRenderer) cost += 4000f;                  // skinning is per-frame CPU
        if (go.GetComponent<Animator>() is not null) cost += 2000f;
        if (go.GetComponent<ParticleSystem>() is { } ps) cost += ps.main.maxParticles * 5f;
        if (go.GetComponent<Rigidbody>() is { isKinematic: false }) cost += 1500f;
        if (go.GetComponent<MeshCollider>() is not null) cost += 1500f;
        if (go.GetComponent<Light>() is { } li && li.type != LightType.Directional) cost += 3000f;
        if (r.isVisible) cost += 1000f;                               // on-screen right now = paying render cost
        return cost;
    }

    private static Color Heat(float t) => t < 0.5f
        ? Color.Lerp(Color.green, Color.yellow, t * 2f)
        : Color.Lerp(Color.yellow, Color.red, (t - 0.5f) * 2f);

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_status) || Time.unscaledTime > _statusUntil) return;
        var prev = GUI.color;
        GUI.color = Color.cyan;
        GUI.Label(new Rect(12, 12, 1100, 28), _status);   // brief on-screen feedback (no chat reply for client cmds)
        GUI.color = prev;
    }

    // Immediate-mode draw, invoked once per camera render. Cheap when all overlays are off.
    private void OnRenderObject()
    {
        if (!(_grid || _nav || _colliders || _entities)) return;
        var cam = Camera.current ?? Camera.main;   // Camera.current is the camera being rendered RIGHT NOW
        if (cam is null) return;
        EnsureMaterial();
        if (_mat is null) return;

        _mat.SetPass(0);
        GL.PushMatrix();
        GL.MultMatrix(Matrix4x4.identity);
        GL.Begin(GL.LINES);

        Vector3 eye = cam.transform.position;
        float farSq = _drawDistance.Value * _drawDistance.Value;
        if (_grid) DrawGrid(eye);
        if (_nav) DrawNavmesh();
        if (_colliders) DrawColliders(eye, farSq);
        if (_entities) DrawEntities(eye, farSq);

        GL.End();
        GL.PopMatrix();
    }

    private void EnsureMaterial()
    {
        if (_mat is not null) return;
        // Try the canonical line shader, then fallbacks that are usually kept in a build (sprites/UI/unlit).
        Shader? shader = null;
        foreach (var name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color", "GUI/Text Shader", "Legacy Shaders/Diffuse" })
        {
            shader = Shader.Find(name);
            if (shader is not null) break;
        }
        if (shader is null) { Logger.LogWarning("No usable line shader found in the build — overlays can't draw."); return; }
        _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_Cull", (int)CullMode.Off);
        _mat.SetInt("_ZWrite", 0);
        _mat.SetInt("_ZTest", (int)(_throughWalls.Value ? CompareFunction.Always : CompareFunction.LessEqual));
    }

    private static void Line(Vector3 a, Vector3 b, Color c) { GL.Color(c); GL.Vertex(a); GL.Vertex(b); }

    private static void WireCube(Bounds b, Color c)
    {
        Vector3 m = b.min, x = b.max;
        Vector3[] p =
        {
            new(m.x, m.y, m.z), new(x.x, m.y, m.z), new(x.x, m.y, x.z), new(m.x, m.y, x.z),
            new(m.x, x.y, m.z), new(x.x, x.y, m.z), new(x.x, x.y, x.z), new(m.x, x.y, x.z),
        };
        for (int i = 0; i < 4; i++)
        {
            Line(p[i], p[(i + 1) % 4], c);
            Line(p[i + 4], p[((i + 1) % 4) + 4], c);
            Line(p[i], p[i + 4], c);
        }
    }

    private void DrawGrid(Vector3 eye)
    {
        var color = new Color(0.3f, 0.7f, 1f, 0.5f);
        float half = _gridSize.Value, step = 5f;
        float cx = Mathf.Round(eye.x / step) * step, cz = Mathf.Round(eye.z / step) * step, y = eye.y - 1.8f;
        for (float o = -half; o <= half; o += step)
        {
            Line(new Vector3(cx - half, y, cz + o), new Vector3(cx + half, y, cz + o), color);
            Line(new Vector3(cx + o, y, cz - half), new Vector3(cx + o, y, cz + half), color);
        }
    }

    private void DrawNavmesh()
    {
        var v = _navMesh.vertices; var t = _navMesh.indices;
        if (v is null || t is null) return;
        var color = new Color(0f, 1f, 0.4f, 0.9f);
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            Line(a, b, color); Line(b, c, color); Line(c, a, color);
        }
    }

    private void DrawColliders(Vector3 eye, float farSq)
    {
        var color = new Color(1f, 0.5f, 0f, 0.8f);
        foreach (var col in _colliderCache)
        {
            if (col is null || !col.enabled) continue;
            var b = col.bounds;
            if ((b.center - eye).sqrMagnitude > farSq) continue;
            WireCube(b, color);
        }
    }

    private void DrawEntities(Vector3 eye, float farSq)
    {
        var color = new Color(1f, 0f, 1f, 0.95f);
        foreach (var view in FindObjectsOfType<PhotonView>())
        {
            if (view is null) continue;
            Vector3 p = view.transform.position;
            if ((p - eye).sqrMagnitude > farSq) continue;
            Line(p + Vector3.left, p + Vector3.right, color);
            Line(p + Vector3.forward, p + Vector3.back, color);
            Line(p, p + Vector3.up * 1.5f, color);
            Line(eye, p, new Color(color.r, color.g, color.b, 0.25f));
        }
    }
}

/// <summary>Harmony patch: intercept the chat-send RPC and handle "/dbg ..." locally, suppressing the send so
/// the command never reaches the server or other players. Patches EVERY PUN RPC overload that could carry a
/// chat call — RPC/RpcSecure, RpcTarget- and Player-targeted — since which one the game uses isn't known.
/// (This is a UX convenience, not a security boundary: the server independently ignores an unknown /dbg.)</summary>
[HarmonyPatch]
internal static class ChatInterceptor
{
    // Every PhotonView.RPC / RpcSecure overload shares (string methodName, …, object[] parameters); patch them
    // all so the chat send is caught regardless of which overload the game calls.
    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(PhotonView).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "RPC" && m.Name != "RpcSecure") continue;
            var ps = m.GetParameters();
            if (ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[ps.Length - 1].ParameterType == typeof(object[]))
                yield return m;
        }
    }

    // Bound by name across all overloads (each has `string methodName` and `object[] parameters`).
    private static bool Prefix(string methodName, object[] parameters)
    {
        if (DebugVisualsPlugin.Instance is null || methodName != "ReceiveChatMessage") return true;
        var text = parameters is { Length: > 0 } ? parameters[0] as string : null;
        if (text is null || !text.TrimStart().StartsWith("/dbg", StringComparison.OrdinalIgnoreCase)) return true;
        DebugVisualsPlugin.Instance.HandleChat(text.Trim());
        return false;   // handled locally — do NOT send to the server / other players
    }
}
