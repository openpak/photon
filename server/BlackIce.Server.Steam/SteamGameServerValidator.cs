using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing.Auth;
using Steamworks;

namespace BlackIce.Server.Steam;

/// <summary>
/// The real <see cref="ISteamTicketValidator"/>: validates a client's Steam auth-session ticket via the Steam
/// game-server API (<c>BeginAuthSession</c>) using Facepunch.Steamworks. This proves the client owns the
/// SteamID it claims, closing the spoofable-identity SECURITY GATE.
///
/// <para>The server runs as an <b>anonymous</b> game server for the app (no publisher Web API key needed — see
/// SECURITY.md option 1). <c>BeginAuthSession</c> accepts the ticket for processing; the verdict arrives
/// asynchronously on <see cref="SteamServer.OnValidateAuthTicketResponse"/>, which completes the awaiting
/// <see cref="TaskCompletionSource{T}"/> keyed by SteamID. The session is always ended afterward.</para>
///
/// <para><b>Not built by default</b> (see the csproj): this file compiles only in the optional, Steam-enabled
/// build, so the core server + test suite need no Steam SDK. It has no unit tests — it requires a live Steam
/// runtime — and must be validated by an integration/manual run against the real client ticket.</para>
/// </summary>
public sealed class SteamGameServerValidator : ISteamTicketValidator, IDisposable
{
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SteamAuthResult>> _pending = new();

    public SteamGameServerValidator(uint appId)
    {
        var init = new SteamServerInit("blackice", "BlackIce")
        {
            DedicatedServer = true,
            GamePort = 0,
            QueryPort = 0,
            Secure = false,
            VersionString = "1.0.0.0",
        };
        // asyncCallbacks: true → Facepunch pumps Steam callbacks on its own timer, so we don't run our own loop.
        SteamServer.Init(appId, init, asyncCallbacks: true);
        SteamServer.LogOnAnonymous();
        SteamServer.OnValidateAuthTicketResponse += OnValidate;
        Log.Info("Steam", $"Steam game server initialized for AppID {appId} (anonymous logon).");
    }

    public Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SteamAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // One in-flight validation per SteamID. If another is already pending for this SteamID (e.g. a client
        // griefing a victim's id, or a fast retry), supersede it cleanly rather than clobbering silently
        // (security review M1). The Steam BeginAuthSession is itself keyed by SteamID, so concurrent sessions
        // for the same id conflict at Steam's level anyway; this just keeps our bookkeeping consistent.
        _pending.AddOrUpdate(assertedSteamId, tcs, (_, old) =>
        {
            old.TrySetResult(SteamAuthResult.Unavailable("superseded by a newer validation"));
            return tcs;
        });

        var begin = SteamServer.BeginAuthSession(ticket, assertedSteamId);
        if (begin != BeginAuthResult.OK)
        {
            if (_pending.TryGetValue(assertedSteamId, out var cur) && ReferenceEquals(cur, tcs))
                _pending.TryRemove(assertedSteamId, out _);
            return Task.FromResult(SteamAuthResult.Rejected($"BeginAuthSession returned {begin}"));
        }

        // Tie the awaited result to the caller's cancellation (the NameServer's timeout) and clean up the
        // session — but only if THIS request is still the current one (don't tear down a newer session).
        ct.Register(() =>
        {
            if (_pending.TryGetValue(assertedSteamId, out var cur) && ReferenceEquals(cur, tcs)
                && _pending.TryRemove(assertedSteamId, out _))
            {
                SafeEndSession(assertedSteamId);
                tcs.TrySetResult(SteamAuthResult.Unavailable("validation timed out"));
            }
        });
        return tcs.Task;
    }

    private void OnValidate(SteamId steamId, SteamId ownerId, AuthResponse response)
    {
        if (!_pending.TryRemove(steamId.Value, out var tcs)) return;   // unknown/already-resolved
        SafeEndSession(steamId.Value);

        if (response != AuthResponse.OK)
        {
            tcs.TrySetResult(SteamAuthResult.Rejected($"AuthResponse={response}"));
            return;
        }
        // Family Sharing / borrowed license: the ticket validates but the SteamID playing (steamId) is not the
        // license owner (ownerId). Reject so a banned owner can't farm verified alts via shared copies, and so a
        // borrowed license never receives admin trust (security review H1).
        if (ownerId.Value != steamId.Value)
        {
            tcs.TrySetResult(SteamAuthResult.Rejected($"borrowed license (owner {ownerId.Value} != player {steamId.Value})"));
            return;
        }
        tcs.TrySetResult(SteamAuthResult.Verified(steamId.Value));
    }

    private static void SafeEndSession(ulong steamId)
    {
        try { SteamServer.EndAuthSession(steamId); }
        catch (Exception ex) { Log.Warn("Steam", $"EndAuthSession({steamId}) threw: {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        try { SteamServer.OnValidateAuthTicketResponse -= OnValidate; } catch { /* shutting down */ }
        try { SteamServer.Shutdown(); } catch { /* shutting down */ }
    }
}
