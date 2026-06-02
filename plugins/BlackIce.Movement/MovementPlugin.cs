using BepInEx;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;

namespace BlackIce.Movement;

/// <summary>
/// CLIENT-side BepInEx mod adding two movement conveniences for the local player: a SPEED multiplier and a
/// noclip FLY toggle. These are client-side by necessity — your avatar is client-authoritative (your game
/// owns its transform and PUN syncs it to the server; the server only relays), so neither can be a server
/// command. This is the modern equivalent of the classic WoW-vanilla fly trick: instead of asking the server
/// to move you, the client manipulates its own movement directly.
///
/// <para><b>How it works (clean-room — Unity + PUN public APIs only, no game internals):</b>
///   * It locates the LOCAL player avatar via PUN's public <see cref="PhotonView.IsMine"/> (the synced object
///     that carries a <see cref="CharacterController"/>), falling back to the main camera's root.
///   * <b>Speed</b>: each frame it amplifies the controller's own horizontal displacement (delta ×
///     multiplier), so you walk/run faster with normal gravity and collision.
///   * <b>Fly</b>: it disables the <see cref="CharacterController"/> (so the game's gravity/ground checks stop
///     fighting) and drives the transform directly — WASD relative to the camera for horizontal, the
///     ascend/descend keys for vertical (plain axis manipulation). PUN then syncs the flown position out.</para>
///
/// <para><b>Experimental.</b> The avatar-finding heuristic and the controller assumptions are unverified
/// across the game's states (menus, death, vehicles). Toggle off if something looks wrong. Keys and speeds
/// are configurable in <c>BepInEx/config/blackice.movement.cfg</c>.</para>
/// </summary>
[BepInPlugin("blackice.movement", "BlackIce Movement (fly + speed)", "0.1.0")]
public sealed class MovementPlugin : BaseUnityPlugin
{
    private ConfigEntry<KeyCode> _flyKey = null!, _speedKey = null!, _ascendKey = null!, _descendKey = null!;
    private ConfigEntry<float> _speedMultiplier = null!, _flyVerticalSpeed = null!, _flyHorizontalSpeed = null!;

    private bool _flying, _speeding;
    private Transform? _player;
    private CharacterController? _controller;
    private Vector3 _lastPos;
    private bool _hasLast;
    private float _flyTargetY;
    private int _speedDebugTick;

    private void Awake()
    {
        _flyKey = Config.Bind("Keys", "Fly", KeyCode.F, "Toggle noclip fly.");
        _speedKey = Config.Bind("Keys", "Speed", KeyCode.G, "Toggle the speed multiplier.");
        _ascendKey = Config.Bind("Keys", "Ascend", KeyCode.Space, "Rise while flying.");
        _descendKey = Config.Bind("Keys", "Descend", KeyCode.LeftControl, "Drop while flying.");
        _speedMultiplier = Config.Bind("Tuning", "SpeedMultiplier", 3f, "Horizontal speed multiplier when speed is on.");
        _flyHorizontalSpeed = Config.Bind("Tuning", "FlyHorizontalSpeed", 30f, "Units/sec horizontal while flying (WASD).");
        _flyVerticalSpeed = Config.Bind("Tuning", "FlyVerticalSpeed", 20f, "Units/sec vertical while flying (ascend/descend).");

        Logger.LogInfo($"BlackIce Movement armed — fly={_flyKey.Value}, speed={_speedKey.Value} " +
                       $"(ascend={_ascendKey.Value}, descend={_descendKey.Value})");
    }

    private void Update()
    {
        if (Input.GetKeyDown(_flyKey.Value))
        {
            _flying = !_flying;
            if (_flying && _player != null) _flyTargetY = _player.position.y;   // hover from where we are
            if (_controller != null) _controller.enabled = !_flying;            // free the transform while flying
            Logger.LogInfo($"fly {(_flying ? "ON" : "OFF")}");
        }
        if (Input.GetKeyDown(_speedKey.Value))
        {
            _speeding = !_speeding;
            Logger.LogInfo($"speed {(_speeding ? $"ON x{_speedMultiplier.Value:0.#}" : "OFF")}");
        }
    }

    // LateUpdate runs after the game's movement, so our edits are what PUN serializes out this frame.
    private void LateUpdate()
    {
        EnsurePlayer();
        if (_player == null) { _hasLast = false; return; }

        if (_flying) ApplyFly();
        else if (_speeding) ApplySpeed();

        _lastPos = _player.position;
        _hasLast = true;
    }

    /// <summary>Amplify the player's own horizontal movement this frame by rewriting the position — the same
    /// direct-set mechanism the working fly path uses (so nothing snaps it back), unlike CharacterController.Move
    /// which only worked if we'd found the exact movement controller. Y is preserved so gravity/steps stay intact.</summary>
    private void ApplySpeed()
    {
        if (!_hasLast) return;
        Vector3 delta = _player!.position - _lastPos;
        float m = _speedMultiplier.Value;
        _player.position = _lastPos + new Vector3(delta.x * m, delta.y, delta.z * m);
        if (_speedDebugTick++ % 60 == 0)
            Logger.LogInfo($"[speed] per-frame move XZ={new Vector2(delta.x, delta.z).magnitude:0.000} ×{m:0.#} " +
                           $"(controller present={_controller != null}, enabled={_controller != null && _controller.enabled})");
    }

    /// <summary>Noclip fly: drive the transform directly — WASD (camera-relative) for XZ, keys for Y.</summary>
    private void ApplyFly()
    {
        float dt = Time.deltaTime;
        var cam = Camera.main;
        Vector3 move = Vector3.zero;
        if (cam != null)
        {
            float h = Input.GetAxisRaw("Horizontal"), v = Input.GetAxisRaw("Vertical");
            Vector3 fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = cam.transform.right; right.y = 0f; right.Normalize();
            move = (fwd * v + right * h) * (_flyHorizontalSpeed.Value * dt);
        }
        float vertical = (Input.GetKey(_ascendKey.Value) ? 1f : 0f) - (Input.GetKey(_descendKey.Value) ? 1f : 0f);
        _flyTargetY += vertical * _flyVerticalSpeed.Value * dt;

        _player!.position = new Vector3(_player.position.x + move.x, _flyTargetY, _player.position.z + move.z);
    }

    /// <summary>Locate the local player avatar (cached): the owned PhotonView that carries a CharacterController,
    /// else the main camera's root. Re-acquires if the cached one was destroyed (respawn / scene change).</summary>
    private void EnsurePlayer()
    {
        if (_player != null && _player) return;   // Unity null-check: destroyed objects compare == null

        foreach (var view in FindObjectsOfType<PhotonView>())
        {
            if (!view.IsMine) continue;
            var cc = view.GetComponentInChildren<CharacterController>();
            if (cc != null) { _controller = cc; _player = cc.transform; _hasLast = false; Logger.LogInfo("player avatar acquired (PhotonView)"); return; }
        }

        var camRoot = Camera.main != null ? Camera.main.transform.root : null;
        if (camRoot != null)
        {
            _player = camRoot;
            _controller = camRoot.GetComponentInChildren<CharacterController>();
            _hasLast = false;
            Logger.LogInfo("player avatar acquired (camera root)");
        }
    }
}
