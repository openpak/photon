using System;
using System.IO;
using System.Text;
using BepInEx;
using Steamworks;
using UnityEngine;

namespace BlackIce.SteamTicketSpike;

/// <summary>
/// Feasibility spike for Steam game-server ticket authentication (Black Ice server, pre-SP3
/// security gate). It answers ONE decisive question: can a BepInEx plugin, running inside the
/// game's Mono runtime, obtain a validated Steam auth-session ticket via Steamworks.NET?
///
/// Why this is in question: SP1 found <c>SteamUser.GetSteamID()</c> reported "not initialized"
/// from the plugin context, so it fell back to reading the SteamID from the registry. That
/// SteamID is client-asserted and therefore spoofable, which blocks networked admin. A real
/// auth ticket — produced by Steam, validated server-side via BeginAuthSession — is unspoofable.
///
/// The spike auto-fires as soon as Steam becomes reachable (no keypress, no menu navigation),
/// so it can be run headlessly: launch the game, then read BepInEx/steam-ticket-spike.log.
/// </summary>
[BepInPlugin("blackice.steamticketspike", "BlackIce Steam Ticket Spike", "0.1.0")]
public sealed class SteamTicketSpikePlugin : BaseUnityPlugin
{
    private enum Phase { WaitingForSteam, Requested, Done }

    private Phase _phase = Phase.WaitingForSteam;
    private float _elapsed;
    private float _nextPoll;
    private bool _warnedNoSteam;

    private StreamWriter _log = null!;

    // Held as a field so the GC does not collect the native callback registration.
    private Callback<GetAuthSessionTicketResponse_t> _ticketResponse = null!;
    private HAuthTicket _pendingTicket = HAuthTicket.Invalid;

    private void Awake()
    {
        var path = Path.Combine(Paths.BepInExRootPath, "steam-ticket-spike.log");
        _log = new StreamWriter(path, append: true) { AutoFlush = true };
        Line("====================================================================");
        Line($"Steam ticket spike started @ {DateTime.UtcNow:o}");
        Line("Goal: prove this plugin can obtain a Steam-validated auth ticket.");
        Line("--------------------------------------------------------------------");

        // Register the async validation callback up front. The game's SteamManager already pumps
        // SteamAPI.RunCallbacks() every 0.2s, so this fires without us running our own pump.
        try
        {
            _ticketResponse = Callback<GetAuthSessionTicketResponse_t>.Create(OnTicketResponse);
            Line("Registered GetAuthSessionTicketResponse_t callback.");
        }
        catch (Exception ex)
        {
            Line($"FATAL: could not register callback (Steamworks not loadable in this context?): {ex}");
        }

        Logger.LogInfo($"Steam ticket spike armed -> {path}");
    }

    private void Update()
    {
        if (_phase == Phase.Done) return;

        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed < _nextPoll) return;
        _nextPoll = _elapsed + 1f; // poll once per second

        if (_phase == Phase.WaitingForSteam)
        {
            if (TrySteamId(out CSteamID id))
            {
                Line($"Steamworks REACHABLE from plugin context after {_elapsed:F1}s. " +
                     $"RISK 1 status: BUSTED — GetSteamID() succeeded = {id.m_SteamID}.");
                RequestTicket();
            }
            else if (_elapsed > 60f && !_warnedNoSteam)
            {
                _warnedNoSteam = true;
                Line("Steamworks still unreachable after 60s. RISK 1 status: CONFIRMED — " +
                     "GetSteamID() keeps throwing. Next step: investigate init order / SteamAPI.Init() in our context.");
            }
        }
    }

    /// <summary>
    /// Attempts to read the local SteamID. This is the same call SP1 saw throw "not initialized";
    /// it succeeds only once SteamAPI.Init() has run in the shared runtime, so it doubles as our
    /// "is Steamworks usable from here yet?" probe.
    /// </summary>
    private static bool TrySteamId(out CSteamID id)
    {
        id = CSteamID.Nil;
        try
        {
            if (!SteamAPI.IsSteamRunning()) return false;
            id = SteamUser.GetSteamID();
            return id.IsValid();
        }
        catch
        {
            return false; // InteropHelp.TestIfAvailableClient() throws until init completes
        }
    }

    private void RequestTicket()
    {
        _phase = Phase.Requested;
        try
        {
            var buffer = new byte[1024];
            _pendingTicket = SteamUser.GetAuthSessionTicket(buffer, buffer.Length, out uint written);

            Line($"GetAuthSessionTicket() returned handle 0x{_pendingTicket.m_HAuthTicket:X8}, {written} bytes.");
            Line($"Ticket bytes (hex): {ToHex(buffer, (int)written)}");
            Line("Ticket obtained synchronously. Awaiting GetAuthSessionTicketResponse_t for Steam validation...");
        }
        catch (Exception ex)
        {
            _phase = Phase.Done;
            Line($"FAILURE: GetAuthSessionTicket() threw: {ex}");
            Line("RISK 1 status: ticket production NOT possible in plugin context as written.");
        }
    }

    private void OnTicketResponse(GetAuthSessionTicketResponse_t cb)
    {
        if (cb.m_hAuthTicket != _pendingTicket) return; // not ours

        bool ok = cb.m_eResult == EResult.k_EResultOK;
        Line($"GetAuthSessionTicketResponse_t: result={cb.m_eResult} (handle 0x{cb.m_hAuthTicket.m_HAuthTicket:X8}).");
        if (ok)
        {
            Line("SUCCESS: Steam VALIDATED the ticket. A BepInEx plugin CAN produce a server-usable auth ticket.");
            Line("=> Path to SP3 is open: send these bytes via a Photon auth param; server validates with BeginAuthSession.");
        }
        else
        {
            Line("Steam did NOT return k_EResultOK — ticket would be rejected by a server. Investigate result code.");
        }

        // Release the ticket; the spike only needed to prove production+validation.
        try { SteamUser.CancelAuthTicket(_pendingTicket); Line("Cancelled the ticket (spike cleanup)."); }
        catch (Exception ex) { Line($"CancelAuthTicket threw (non-fatal): {ex.Message}"); }

        Line("Spike complete.");
        Line("====================================================================");
        _phase = Phase.Done;
    }

    private static string ToHex(byte[] data, int count)
    {
        var sb = new StringBuilder(count * 2);
        for (int i = 0; i < count; i++) sb.Append(data[i].ToString("x2"));
        return sb.ToString();
    }

    private void Line(string text)
    {
        _log.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}  {text}");
    }
}
