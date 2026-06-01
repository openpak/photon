using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Realtime;

namespace BlackIce.BigLobby;

/// <summary>
/// CLIENT-side BepInEx mod that raises the game's effective room-size ceiling so a match can hold more
/// than the stock ~8 players when playing on a BlackIce server configured for a larger realm.
///
/// This mod is OPTIONAL — it is NOT required to join a >8 realm. Room capacity is enforced entirely by the
/// BlackIce server (GameServerHandler.EnterRoom admits joins against the realm's configured MaxPlayers), so
/// an unmodified client joins a realm configured for 32 players fine. The remaining ~8 is a CLIENT-side
/// in-match assumption: the client that CREATES a room calls PUN with the game's small baked-in
/// RoomOptions.MaxPlayers (~8); the server ignores that for the gate, but the creating client's own local
/// PUN/HUD still believes it. This mod raises that requested capacity at the public PUN boundary so the
/// room-creator's local view matches the big realm — a smoothing aid, not a connection requirement.
///
/// What it patches (public PUN surface only — no game internals):
///   * <see cref="Photon.Pun.PhotonNetwork.CreateRoom"/> / CreateRoomAndLobby — clamp up the RoomOptions.MaxPlayers
///     the client requests, so a room the client hosts is created big.
///   * <see cref="RoomOptions"/> construction — raise MaxPlayers when the game leaves it at the small default.
///
/// CAVEATS — read these:
///   * This overrides a CLIENT design assumption; it is NOT a guarantee the game is stable at large sizes.
///     The game's rendering / HUD / netcode were built for small matches and are UNVERIFIED above 8.
///     Treat large matches as experimental (see docs/large-servers.md and the live-verification roadmap).
///   * PUN's RoomInfo carries MaxPlayers as a BYTE, so the hard ceiling this can express is 255.
///   * It does nothing on its own unless the SERVER's realm MaxPlayers is also raised — both sides must agree.
///
/// Off-by-default-ish: it only ever RAISES a cap toward <see cref="MaxPlayers"/> and never lowers one the
/// game set higher, so leaving the default config value is safe.
/// </summary>
[BepInPlugin("blackice.biglobby", "BlackIce Big Lobby", "0.1.0")]
public sealed class BigLobbyPlugin : BaseUnityPlugin
{
    internal static ConfigEntry<int> MaxPlayers = null!;
    internal static BepInEx.Logging.ManualLogSource Log = null!;

    private void Awake()
    {
        Log = Logger;
        MaxPlayers = Config.Bind("Lobby", "MaxPlayers", 32,
            "Room capacity to request when this client creates/joins a match on a BlackIce server. " +
            "PUN caps this at 255 (the wire format is a byte). The server's realm MaxPlayers must be raised " +
            "to match. NOTE: the game is only verified for ~8 players per match — larger sizes are experimental.");

        new Harmony("blackice.biglobby").PatchAll();
        Logger.LogInfo($"BlackIce Big Lobby armed -> requesting rooms up to {Clamp(MaxPlayers.Value)} players");
    }

    /// <summary>The configured ceiling, clamped to the 1..255 range PUN's byte-typed MaxPlayers can hold.
    /// (Hand-rolled because <c>Math.Clamp</c> is unavailable on netstandard2.0 / the BepInEx Mono runtime.)</summary>
    internal static byte Clamp(int value) => (byte)(value < 1 ? 1 : value > byte.MaxValue ? byte.MaxValue : value);
}

/// <summary>
/// Raises the requested room capacity whenever the client builds <see cref="RoomOptions"/>: if the game
/// left MaxPlayers at a small value (or 0), bump it to the configured ceiling. Patching RoomOptions rather
/// than a specific game method keeps this resilient to how the game creates rooms, and only ever enlarges.
/// </summary>
[HarmonyPatch(typeof(RoomOptions), MethodType.Constructor)]
internal static class RoomOptionsPatch
{
    static void Postfix(RoomOptions __instance)
    {
        byte target = BigLobbyPlugin.Clamp(BigLobbyPlugin.MaxPlayers.Value);
        // Only raise: never shrink a cap the game deliberately set higher than our target. 0 = "unlimited"
        // in PUN, but the game's HUD expects a concrete size, so we still set our explicit ceiling there.
        if (__instance.MaxPlayers == 0 || __instance.MaxPlayers < target)
        {
            __instance.MaxPlayers = target;
            BigLobbyPlugin.Log.LogInfo($"RoomOptions.MaxPlayers raised -> {target}");
        }
    }
}
