using System;
using BepInEx;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using UnityEngine;

namespace BlackIce.SteamAuth;

/// <summary>
/// CLIENT-side BepInEx mod that attaches a Steam game-server auth ticket to the PUN authentication, so a
/// BlackIce server can PROVE this client owns the SteamID it presents (server-side <c>BeginAuthSession</c>).
/// This is the production counterpart to the BlackIce.SteamTicketSpike feasibility spike: it mints the same
/// validated ticket and injects it into <see cref="AuthenticationValues.AuthData"/> (Photon param 217,
/// ClientAuthenticationData), which the server reads at the Name Server auth step.
///
/// <para>Without this mod, a public BlackIce server rejects the client (fail-closed) because no ticket is
/// present; on LAN the server accepts the unverified asserted SteamID as before.</para>
///
/// <para><b>Experimental.</b> The exact PUN auth-values plumbing varies by game/PUN version. This sets
/// <see cref="PhotonNetwork.AuthValues"/>.AuthData while disconnected and refreshes the ticket as needed; if
/// the server log still reports "public auth without a Steam ticket", the injection point needs adjusting to
/// this build's PUN (see the BlackIce server's NameServer logs to confirm arrival).</para>
/// </summary>
[BepInPlugin("blackice.steamauth", "BlackIce Steam Auth", "0.1.0")]
public sealed class SteamAuthPlugin : BaseUnityPlugin
{
    private Callback<GetAuthSessionTicketResponse_t> _ticketResponse = null!;
    private HAuthTicket _ticketHandle = HAuthTicket.Invalid;
    private byte[]? _ticket;            // the validated ticket bytes, once Steam confirms them
    private bool _requested;
    private bool _attached;
    private float _nextPoll;

    private void Awake()
    {
        try { _ticketResponse = Callback<GetAuthSessionTicketResponse_t>.Create(OnTicketValidated); }
        catch (Exception ex) { Logger.LogError($"Steamworks callback registration failed: {ex.Message}"); }
        Logger.LogInfo("BlackIce Steam Auth armed — will attach a Steam ticket to PUN authentication.");
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 1f;

        // 1) Mint the ticket once Steam is reachable (same path the spike proved).
        if (!_requested && SteamRunning())
        {
            _requested = true;
            try
            {
                var buffer = new byte[1024];
                _ticketHandle = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint written);
                _ticket = new byte[written];
                Array.Copy(buffer, _ticket, (int)written);
                Logger.LogInfo($"Requested Steam auth ticket ({written} bytes); awaiting Steam validation...");
            }
            catch (Exception ex) { Logger.LogError($"GetAuthSessionTicket failed: {ex.Message}"); }
        }

        // 2) While not connected, keep PUN's auth values carrying the validated ticket so the next connect
        //    sends it. (Re-applied because the game may rebuild AuthValues before connecting.)
        if (_ticket is { } ticket && !PhotonNetwork.IsConnected)
        {
            var auth = PhotonNetwork.AuthValues ??= new AuthenticationValues();
            auth.SetAuthPostData(ticket);   // Photon sends this as ClientAuthenticationData (param 217)
            if (!_attached) { _attached = true; Logger.LogInfo($"Attached Steam ticket to PUN AuthValues ({ticket.Length} bytes)."); }
        }
    }

    private static bool SteamRunning()
    {
        try { return SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid(); }
        catch { return false; }
    }

    private void OnTicketValidated(GetAuthSessionTicketResponse_t cb)
    {
        if (cb.m_hAuthTicket != _ticketHandle) return;
        if (cb.m_eResult == EResult.k_EResultOK)
            Logger.LogInfo("Steam VALIDATED the auth ticket — it will be accepted by a BlackIce server.");
        else
            Logger.LogWarning($"Steam did not validate the ticket (result={cb.m_eResult}); server will reject it.");
    }

    private void OnDestroy()
    {
        if (_ticketHandle != HAuthTicket.Invalid)
            try { SteamUser.CancelAuthTicket(_ticketHandle); } catch { /* shutting down */ }
    }
}
