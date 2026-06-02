using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing.Auth;

namespace BlackIce.Server.Host;

/// <summary>
/// Selects the Steam ticket validator at startup. By default (and in CI / the Steam-free build) this returns
/// the <see cref="NullSteamTicketValidator"/>, so public peers fail closed and the build needs no Steam SDK.
/// When the optional <c>BlackIce.Server.Steam</c> project is referenced and the build defines the
/// <c>STEAM_ENABLED</c> symbol, the real game-server validator (<c>BeginAuthSession</c> for AppID 311800) is
/// used instead — mirroring the optional <c>PhotonOracleDll</c> reference pattern.
/// </summary>
public static class SteamValidatorFactory
{
    /// <summary>Black Ice's Steam AppID — the app the server validates client tickets for.</summary>
    public const uint AppId = 311800;

    public static ISteamTicketValidator Create()
    {
#if STEAM_ENABLED
        try
        {
            var v = new BlackIce.Server.Steam.SteamGameServerValidator(AppId);
            Log.Info("Steam", $"Steam game-server ticket validation ENABLED (AppID {AppId}).");
            return v;
        }
        catch (System.Exception ex)
        {
            Log.Warn("Steam", $"Steam game-server init failed ({ex.GetType().Name}: {ex.Message}); " +
                              "falling back to Null validator — public peers will fail closed.");
            return new NullSteamTicketValidator();
        }
#else
        Log.Info("Steam", "Steam validation not compiled in (no BlackIce.Server.Steam / STEAM_ENABLED); " +
                          "public peers fail closed, LAN is unaffected. See docs for enabling it.");
        return new NullSteamTicketValidator();
#endif
    }
}
