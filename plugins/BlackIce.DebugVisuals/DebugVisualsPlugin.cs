using System;
using BepInEx;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace BlackIce.DebugVisuals;

/// <summary>
/// CLIENT-side BepInEx mod that draws debug overlays in the game world with immediate-mode <see cref="GL"/>
/// lines: a ground reference GRID, the game's baked NAVMESH (the pathfinding surface, via
/// <see cref="NavMesh.CalculateTriangulation"/>), COLLIDER bounds (the collision the world is built from), and
/// markers + lines to networked ENTITIES (players, bots, loot — every PUN <see cref="PhotonView"/>).
///
/// <para>Clean-room: Unity + PUN PUBLIC APIs only (no game internals). Everything is rendered locally; nothing
/// is sent to the server. Each overlay toggles on its own key (configurable in
/// <c>BepInEx/config/blackice.debugvisuals.cfg</c>). Expensive scans (navmesh triangulation, collider
/// enumeration) are cached and refreshed on a timer / when toggled on.</para>
///
/// <para><b>Experimental.</b> Drawing depends on the <c>Hidden/Internal-Colored</c> shader being present in the
/// build and on the game having a baked navmesh; if an overlay shows nothing, that's why. Toggle off if it
/// costs too much in a dense scene (collider/entity overlays scan the scene).</para>
/// </summary>
[BepInPlugin("blackice.debugvisuals", "BlackIce Debug Visuals", "0.1.0")]
public sealed class DebugVisualsPlugin : BaseUnityPlugin
{
    private ConfigEntry<KeyCode> _gridKey = null!, _navKey = null!, _colliderKey = null!, _entityKey = null!;
    private ConfigEntry<float> _drawDistance = null!, _gridSize = null!, _refreshSeconds = null!;
    private ConfigEntry<bool> _throughWalls = null!;

    private bool _grid, _nav, _colliders, _entities;
    private Material? _mat;
    private NavMeshTriangulation _navMesh;
    private Collider[] _colliderCache = Array.Empty<Collider>();
    private float _nextRefresh;

    private void Awake()
    {
        _gridKey = Config.Bind("Keys", "Grid", KeyCode.F1, "Toggle the ground reference grid.");
        _navKey = Config.Bind("Keys", "Navmesh", KeyCode.F2, "Toggle the baked navmesh (pathfinding) wireframe.");
        _colliderKey = Config.Bind("Keys", "Colliders", KeyCode.F3, "Toggle collider bounds wireframes.");
        _entityKey = Config.Bind("Keys", "Entities", KeyCode.F4, "Toggle networked-entity markers + lines.");
        _drawDistance = Config.Bind("Tuning", "DrawDistance", 80f, "Max distance from the camera to draw colliders/entities.");
        _gridSize = Config.Bind("Tuning", "GridSize", 100f, "Half-extent (units) of the ground grid around the camera.");
        _refreshSeconds = Config.Bind("Tuning", "RefreshSeconds", 1.0f, "How often to re-scan navmesh/colliders.");
        _throughWalls = Config.Bind("Tuning", "ThroughWalls", true, "Draw overlays through geometry (ZTest Always).");

        Logger.LogInfo($"BlackIce Debug Visuals armed — grid={_gridKey.Value}, navmesh={_navKey.Value}, " +
                       $"colliders={_colliderKey.Value}, entities={_entityKey.Value}");
    }

    private void Update()
    {
        if (Input.GetKeyDown(_gridKey.Value)) { _grid = !_grid; Logger.LogInfo($"grid {(_grid ? "ON" : "OFF")}"); }
        if (Input.GetKeyDown(_navKey.Value)) { _nav = !_nav; if (_nav) Refresh(); Logger.LogInfo($"navmesh {(_nav ? "ON" : "OFF")}"); }
        if (Input.GetKeyDown(_colliderKey.Value)) { _colliders = !_colliders; if (_colliders) Refresh(); Logger.LogInfo($"colliders {(_colliders ? "ON" : "OFF")}"); }
        if (Input.GetKeyDown(_entityKey.Value)) { _entities = !_entities; Logger.LogInfo($"entities {(_entities ? "ON" : "OFF")}"); }

        if ((_nav || _colliders) && Time.unscaledTime >= _nextRefresh) Refresh();
    }

    private void Refresh()
    {
        _nextRefresh = Time.unscaledTime + Mathf.Max(0.1f, _refreshSeconds.Value);
        try { if (_nav) _navMesh = NavMesh.CalculateTriangulation(); } catch { /* no baked navmesh */ }
        try { if (_colliders) _colliderCache = FindObjectsOfType<Collider>(); } catch { _colliderCache = Array.Empty<Collider>(); }
    }

    // Immediate-mode draw, invoked once per camera render. Cheap when all overlays are off.
    private void OnRenderObject()
    {
        if (!(_grid || _nav || _colliders || _entities)) return;
        var cam = Camera.main;
        if (cam is null) return;
        EnsureMaterial();
        if (_mat is null) return;

        _mat.SetPass(0);
        GL.PushMatrix();
        GL.MultMatrix(Matrix4x4.identity);
        GL.Begin(GL.LINES);

        Vector3 eye = cam.transform.position;
        float far = _drawDistance.Value * _drawDistance.Value;
        if (_grid) DrawGrid(eye);
        if (_nav) DrawNavmesh();
        if (_colliders) DrawColliders(eye, far);
        if (_entities) DrawEntities(eye, far);

        GL.End();
        GL.PopMatrix();
    }

    private void EnsureMaterial()
    {
        if (_mat is not null) return;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader is null) { Logger.LogWarning("Hidden/Internal-Colored shader not in build — overlays can't draw."); return; }
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
            Line(p[i], p[(i + 1) % 4], c);            // bottom
            Line(p[i + 4], p[((i + 1) % 4) + 4], c);  // top
            Line(p[i], p[i + 4], c);                  // verticals
        }
    }

    private void DrawGrid(Vector3 eye)
    {
        var color = new Color(0.3f, 0.7f, 1f, 0.35f);
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
        var color = new Color(1f, 0.5f, 0f, 0.7f);
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
        var color = new Color(1f, 0f, 1f, 0.9f);
        foreach (var view in FindObjectsOfType<PhotonView>())
        {
            if (view is null) continue;
            Vector3 p = view.transform.position;
            if ((p - eye).sqrMagnitude > farSq) continue;
            // a small 3D cross marker + a line from the camera, so networked objects are easy to spot
            Line(p + Vector3.left, p + Vector3.right, color);
            Line(p + Vector3.forward, p + Vector3.back, color);
            Line(p + Vector3.up * 1.5f, p, color);
            Line(eye, p, new Color(color.r, color.g, color.b, 0.25f));
        }
    }
}
