using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;

namespace BlackIce.Redirect;

/// <summary>
/// Realmlist-style server redirect. The player sets one config value (ServerAddress) and the
/// client connects to that custom BlackIce server instead of Photon Cloud. The override is
/// applied just before <see cref="PhotonNetwork.ConnectUsingSettings()"/> runs, so it wins
/// regardless of when the game initiates the connection.
/// </summary>
[BepInPlugin("blackice.redirect", "BlackIce Server Redirect", "0.1.0")]
public sealed class RedirectPlugin : BaseUnityPlugin
{
    internal static ConfigEntry<string> ServerAddress = null!;
    internal static ConfigEntry<int> ServerPort = null!;
    internal static BepInEx.Logging.ManualLogSource Log = null!;

    private void Awake()
    {
        Log = Logger;
        ServerAddress = Config.Bind("Server", "ServerAddress", "127.0.0.1",
            "Custom BlackIce server address (the Name Server). Like WoW's realmlist — point this at your server.");
        ServerPort = Config.Bind("Server", "Port", 5058, "Name Server UDP port.");
        new Harmony("blackice.redirect").PatchAll();
        Logger.LogInfo($"BlackIce redirect armed -> {ServerAddress.Value}:{ServerPort.Value}");
    }
}

/// <summary>Overrides the Photon app settings to point at our server right before connect.</summary>
[HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.ConnectUsingSettings), new System.Type[0])]
internal static class ConnectRedirectPatch
{
    static void Prefix()
    {
        var settings = PhotonNetwork.PhotonServerSettings.AppSettings;
        settings.Server = RedirectPlugin.ServerAddress.Value;
        settings.Port = RedirectPlugin.ServerPort.Value;
        settings.UseNameServer = true;   // client walks Name -> Master -> Game against us
        // Keep the game's FixedRegion (already set, e.g. "us"): a non-empty fixed region makes the
        // client authenticate directly on our Name Server instead of asking for a region list.
        if (string.IsNullOrEmpty(settings.FixedRegion)) settings.FixedRegion = "us";

        // Send the real SteamID as the Photon UserId so the server can key a persistent account
        // to it. Without this the server only sees a chosen character name.
        var steamId = ResolveSteamId();
        if (steamId is not null)
        {
            PhotonNetwork.AuthValues = new Photon.Realtime.AuthenticationValues(steamId);
            RedirectPlugin.Log.LogInfo($"Sending SteamID {steamId} as Photon UserId");
        }
        else RedirectPlugin.Log.LogWarning("SteamID unavailable; server will assign a fallback identity.");

        RedirectPlugin.Log.LogInfo($"Redirecting Photon connect -> {settings.Server}:{settings.Port} (region '{settings.FixedRegion}')");
    }

    /// <summary>
    /// Resolves the logged-in user's SteamID64. Primary: the Steam registry key (context-independent).
    /// Fallback: the Steamworks API if it happens to be initialized in this context.
    /// </summary>
    private static string? ResolveSteamId()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("reg",
                @"query HKCU\Software\Valve\Steam\ActiveProcess /v ActiveUser")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            var outp = p!.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"0x([0-9a-fA-F]+)");
            if (m.Success)
            {
                uint accountId = System.Convert.ToUInt32(m.Groups[1].Value, 16);
                if (accountId != 0) return (76561197960265728UL + accountId).ToString();
            }
        }
        catch (System.Exception ex) { RedirectPlugin.Log.LogWarning($"registry SteamID read failed: {ex.Message}"); }

        try { return Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(); }
        catch { return null; }
    }
}
