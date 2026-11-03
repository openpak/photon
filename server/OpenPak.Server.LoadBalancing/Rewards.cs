using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Photon AppId to rewards game slug, from <c>OPENPAK_PHOTON_REWARDS=&lt;appid&gt;=&lt;slug&gt;,…</c>
/// (same shape as <see cref="PhotonTitles"/>). The slug names the metric <c>game_sessions.&lt;slug&gt;</c>.
/// </summary>
public static class PhotonRewards
{
    public static IReadOnlyDictionary<string, string> Parse(string? spec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (appId, slug) in PhotonTitles.Parse(spec))
            map[appId] = "game_sessions." + slug.ToLowerInvariant();
        return map;
    }

    /// <summary>REWARDS_SESSION_MIN (a duration such as "3m" or "90s") or 3 minutes.</summary>
    public static TimeSpan SessionMin(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return TimeSpan.FromMinutes(3);
        var s = env.Trim();
        // Go-style durations: a number with a unit suffix (s, m, h).
        if (s.Length > 1 && double.TryParse(s[..^1], System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
            switch (s[^1])
            {
                case 's': return TimeSpan.FromSeconds(n);
                case 'm': return TimeSpan.FromMinutes(n);
                case 'h': return TimeSpan.FromHours(n);
            }
        return TimeSpan.FromMinutes(3);
    }
}

/// <summary>Durable name -> JSON body store for the rewards state, so a restart loses nothing.</summary>
public interface IRewardsStore
{
    string? Get(string name);
    void Put(string name, string body);
}

/// <summary>Kept in the server's own database (SQLite on the /app/data volume in production).</summary>
public sealed class SqlRewardsStore : IRewardsStore
{
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<OpenPak.Server.Data.OpenPakDbContext> _dbf;
    private readonly object _gate = new();
    private bool _ready;

    public SqlRewardsStore(Microsoft.EntityFrameworkCore.IDbContextFactory<OpenPak.Server.Data.OpenPakDbContext> dbf) => _dbf = dbf;

    private OpenPak.Server.Data.OpenPakDbContext Open()
    {
        var db = _dbf.CreateDbContext();
        if (!_ready)
        {
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS rewards_state (name VARCHAR(64) NOT NULL PRIMARY KEY, body TEXT NOT NULL)");
            _ready = true;
        }
        return db;
    }

    public string? Get(string name)
    {
        lock (_gate)
        {
            using var db = Open();
            return db.Database.SqlQueryRaw<string>("SELECT body AS Value FROM rewards_state WHERE name = {0}", name).AsEnumerable().FirstOrDefault();
        }
    }

    public void Put(string name, string body)
    {
        lock (_gate)
        {
            using var db = Open();
            if (db.Database.ExecuteSqlRaw("UPDATE rewards_state SET body = {0} WHERE name = {1}", body, name) == 0)
                db.Database.ExecuteSqlRaw("INSERT INTO rewards_state (name, body) VALUES ({0}, {1})", name, body);
        }
    }
}

/// <summary>Process-local store, for tests and for running without a database.</summary>
public sealed class MemoryRewardsStore : IRewardsStore
{
    private readonly ConcurrentDictionary<string, string> _d = new();
    public string? Get(string name) => _d.TryGetValue(name, out var b) ? b : null;
    public void Put(string name, string body) => _d[name] = body;
}

/// <summary>One room as the clock sees it: a unique id, its metric, and the mapped players' subjects.</summary>
public sealed record ClockRoom(string Id, string Metric, IReadOnlyCollection<string> Subjects);

public sealed record SessionEvent(string EventId, string Metric, string RoomId, string Subject, DateTime At);

/// <summary>
/// Mirrors rewards-emit sessions.go: a player counts once per room after <c>min</c> of unbroken time in it
/// together with at least one other mapped player. A room under two mapped players resets its players.
/// Not thread-safe; one watcher task calls it.
/// </summary>
public sealed class SessionClock
{
    private static readonly TimeSpan RestartGap = TimeSpan.FromMinutes(2), EmittedKeep = TimeSpan.FromHours(24);
    private readonly TimeSpan _min;
    private readonly IRewardsStore? _store;
    private Dictionary<string, DateTime> _since = new(StringComparer.Ordinal);
    private Dictionary<string, DateTime> _emitted = new(StringComparer.Ordinal);   // key -> last time its room was live
    private string? _saved;

    private sealed class Snapshot
    {
        public DateTime SavedAt { get; set; }
        public Dictionary<string, DateTime> Since { get; set; } = new();
        public Dictionary<string, DateTime> Emitted { get; set; } = new();
    }

    /// <param name="store">When given, the clock reloads from it: counted pairs always, running clocks only
    /// if the snapshot is younger than a restart gap (a long outage is a break in "unbroken time").</param>
    public SessionClock(TimeSpan min, IRewardsStore? store = null, DateTime? now = null)
    {
        _min = min; _store = store;
        if (store?.Get("clock") is { } body)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<Snapshot>(body);
                if (snap is not null)
                {
                    _emitted = new(snap.Emitted, StringComparer.Ordinal);
                    if ((now ?? DateTime.UtcNow) - snap.SavedAt <= RestartGap) _since = new(snap.Since, StringComparer.Ordinal);
                }
            }
            catch (JsonException) { }
        }
    }

    public List<SessionEvent> Tick(DateTime now, IEnumerable<ClockRoom> rooms)
    {
        rooms = rooms.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<SessionEvent>();
        foreach (var room in rooms.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var subjects = room.Subjects.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (subjects.Count < 2) continue;
            foreach (var subject in subjects)
            {
                var k = room.Id + "\0" + subject;
                seen.Add(k);
                if (!_since.TryGetValue(k, out var since)) _since[k] = since = now;
                if (!_emitted.ContainsKey(k) && now - since >= _min)
                {
                    _emitted[k] = now;
                    events.Add(new SessionEvent($"{room.Metric}:{room.Id}:switch:{subject}", room.Metric, room.Id, subject, now));
                }
            }
        }
        foreach (var k in _since.Keys.Where(k => !seen.Contains(k)).ToList()) _since.Remove(k);
        // A counted pair is remembered while its room lives and for a day after (a restart empties the
        // registry for a while), so a player who leaves and returns is not counted twice.
        var live = rooms.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var k in _emitted.Keys.ToList())
        {
            if (live.Contains(k[..k.IndexOf('\0')])) _emitted[k] = now;
            else if (now - _emitted[k] > EmittedKeep) _emitted.Remove(k);
        }
        Save(now);
        return events;
    }

    private void Save(DateTime now)
    {
        if (_store is null) return;
        if (_since.Count == 0 && _emitted.Count == 0 && _saved is null) return;
        var body = JsonSerializer.Serialize(new Snapshot { SavedAt = now, Since = _since, Emitted = _emitted });
        _store.Put("clock", body);
        _saved = body;
    }
}

/// <summary>Batched, bounded, retrying POST to rewards /internal/v1/events. Never throws into callers.</summary>
public sealed class RewardsEmitter : IAsyncDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private const int Batch = 50, QueueSize = 2000;
    private readonly HttpClient _http;
    private readonly string _url, _key;
    private readonly IRewardsStore _store;
    private readonly object _gate = new();
    private readonly List<SessionEvent> _outbox = new();   // durable: persisted on every change
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly TimeSpan _poll;

    public RewardsEmitter(HttpClient http, string url, string key, IRewardsStore? store = null, TimeSpan? poll = null)
    {
        _http = http; _url = url.TrimEnd('/'); _key = key; _store = store ?? new MemoryRewardsStore();
        _poll = poll ?? TimeSpan.FromSeconds(2);
        try
        {
            if (_store.Get("outbox") is { } body && JsonSerializer.Deserialize<List<SessionEvent>>(body) is { } saved)
                _outbox.AddRange(saved);
        }
        catch (Exception) { }
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Enabled only with both REWARDS_EVENTS_URL and REWARDS_EVENTS_KEY (or _KEY_FILE).</summary>
    public static RewardsEmitter? FromEnv(Func<string, string?> env, IRewardsStore? store = null)
    {
        var url = env("REWARDS_EVENTS_URL");
        var key = env("REWARDS_EVENTS_KEY");
        if (env("REWARDS_EVENTS_KEY_FILE") is { Length: > 0 } file)
        {
            try { key = File.ReadAllText(file).Trim(); } catch (Exception) { }
        }
        return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)
            ? null
            : new RewardsEmitter(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, url, key, store);
    }

    public int Pending { get { lock (_gate) return _outbox.Count; } }

    public void Enqueue(SessionEvent e)
    {
        lock (_gate)
        {
            if (_outbox.Count >= QueueSize) return;
            _outbox.Add(e);
            Persist();
        }
    }

    private void Persist()
    {
        try { _store.Put("outbox", JsonSerializer.Serialize(_outbox)); }
        catch (Exception ex) { Log.Warn("Rewards", $"outbox save failed: {ex.GetType().Name}"); }
    }

    private async Task RunAsync()
    {
        var backoff = TimeSpan.Zero;
        var next = DateTime.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(_poll, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (DateTime.UtcNow < next) continue;
            try
            {
                while (true)
                {
                    List<SessionEvent> batch;
                    lock (_gate) batch = _outbox.Take(Batch).ToList();
                    if (batch.Count == 0) break;
                    if (!await PostAsync(batch).ConfigureAwait(false)) throw new HttpRequestException("retry");
                    lock (_gate) { _outbox.RemoveRange(0, batch.Count); Persist(); }
                    backoff = TimeSpan.Zero;
                }
            }
            catch (Exception)
            {
                backoff = TimeSpan.FromSeconds(Math.Min(Math.Max(backoff.TotalSeconds * 2, 1), 60));
                next = DateTime.UtcNow + backoff;
                lock (_gate) { if (_outbox.RemoveAll(e => DateTime.UtcNow - e.At > MaxAge) > 0) Persist(); }
            }
        }
    }

    /// <summary>true = finished with (delivered, or refused for a reason a retry cannot fix).</summary>
    private async Task<bool> PostAsync(List<SessionEvent> batch)
    {
        var events = batch.Select(e => new
        {
            event_id = e.EventId,
            source = "photon",
            metric = e.Metric,
            identity = new { @namespace = "switch", subject = e.Subject },
            keys = new[] { e.RoomId },
            occurred_at = e.At.ToUniversalTime().ToString("o"),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _url + "/internal/v1/events");
        req.Headers.Add("X-Internal-Key", _key);
        req.Content = JsonContent.Create(new { events });
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 200) { Log.Info("Rewards", $"sent {batch.Count} session event(s)"); return true; }
        if (code == 429 || code >= 500) return false;
        Log.Warn("Rewards", $"batch of {batch.Count} refused with HTTP {code}; dropped");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (Exception) { }
    }
}

/// <summary>
/// Turns the live room registry into <c>game_sessions.&lt;game&gt;</c> events. Runs on its own task, never
/// on a UDP loop. A player's account is resolved lazily through nx-baas /internal/photon-identity
/// (<c>baas_user_id</c>) and cached per minted UserId; players that do not resolve are left out.
/// </summary>
public sealed class RewardsWatcher
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15), MissRetry = TimeSpan.FromMinutes(2);
    private readonly RoomRegistry _registry;
    private readonly AccountIdentityRegistry _identities;
    private readonly IReadOnlyDictionary<string, string> _metricByAppId;
    private readonly Func<DateTime> _now;
    private readonly Func<string, Task<string?>> _resolve;
    private readonly Action<SessionEvent> _emit;
    private readonly SessionClock _clock;
    private readonly IRewardsStore _store;
    private readonly Dictionary<string, (string? Subject, DateTime At)> _subjects = new();   // positives are durable
    private Dictionary<string, Incarnation> _incarnation = new();

    /// <summary>A room's id, kept through a restart: the same name within <see cref="Reuse"/> of its last
    /// live moment is the same room, so event ids do not change when the server does.</summary>
    public sealed class Incarnation { public string Id { get; set; } = ""; public DateTime LastLive { get; set; } }
    private static readonly TimeSpan Reuse = TimeSpan.FromMinutes(10), Forget = TimeSpan.FromDays(1);

    public RewardsWatcher(RoomRegistry registry, AccountIdentityRegistry identities,
                          IReadOnlyDictionary<string, string> metricByAppId, TimeSpan min,
                          Func<string, Task<string?>> resolveIdentity, Action<SessionEvent> emit, Func<DateTime>? now = null,
                          IRewardsStore? store = null)
    {
        _store = store ?? new MemoryRewardsStore();
        _now = now ?? (() => DateTime.UtcNow);
        try
        {
            if (_store.Get("incarnations") is { } i) _incarnation = JsonSerializer.Deserialize<Dictionary<string, Incarnation>>(i) ?? new();
            if (_store.Get("subjects") is { } sj && JsonSerializer.Deserialize<Dictionary<string, string>>(sj) is { } m)
                foreach (var (u, sub) in m) _subjects[u] = (sub, DateTime.MinValue);
        }
        catch (Exception) { }
        _registry = registry; _identities = identities; _metricByAppId = metricByAppId;
        _resolve = resolveIdentity; _emit = emit;
        _clock = new SessionClock(min, _store, _now());
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await TickAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("Rewards", $"tick failed: {ex.GetType().Name}"); }
        }
    }

    public async Task TickAsync()
    {
        var now = _now();
        var rooms = new List<ClockRoom>();
        var dirty = false;
        foreach (var room in _registry.All)
        {
            if (!_metricByAppId.TryGetValue(room.AppId, out var metric)) continue;
            var session = _registry.FindSession(room.Key);
            var userIds = new List<string>();
            if (session is not null)
                foreach (var actor in session.Actors())
                    if (session.PeerOf(actor) is { SteamId: { Length: > 0 } uid }) userIds.Add(uid);
            if (userIds.Count == 0) continue;
            if (!_incarnation.TryGetValue(room.Key, out var inc) || now - inc.LastLive > Reuse)
            {
                _incarnation[room.Key] = inc = new Incarnation { Id = $"{room.Name}@{new DateTimeOffset(now).ToUnixTimeSeconds()}" };
                dirty = true;
            }
            if (now - inc.LastLive > TimeSpan.FromSeconds(30)) dirty = true;
            inc.LastLive = now;
            var subjects = new List<string>();
            foreach (var uid in userIds.Distinct())
                if (await SubjectAsync(uid, now).ConfigureAwait(false) is { } s) subjects.Add(s);
            rooms.Add(new ClockRoom(inc.Id, metric, subjects));
        }
        foreach (var k in _incarnation.Where(kv => now - kv.Value.LastLive > Forget).Select(kv => kv.Key).ToList())
        { _incarnation.Remove(k); dirty = true; }
        if (dirty) try { _store.Put("incarnations", JsonSerializer.Serialize(_incarnation)); } catch (Exception) { }
        foreach (var e in _clock.Tick(now, rooms)) _emit(e);
    }

    private async Task<string?> SubjectAsync(string userId, DateTime now)
    {
        if (_subjects.TryGetValue(userId, out var c) && (c.Subject is not null || now - c.At < MissRetry)) return c.Subject;
        string? subject = null;
        if (_identities.Find(userId) is { } identity)
        {
            try { subject = await _resolve(identity).ConfigureAwait(false); }
            catch (Exception) { }
        }
        _subjects[userId] = (string.IsNullOrEmpty(subject) ? null : subject, now);
        if (_subjects[userId].Subject is not null)
        {
            try
            {
                _store.Put("subjects", JsonSerializer.Serialize(_subjects.Where(kv => kv.Value.Subject is not null)
                                                                          .ToDictionary(kv => kv.Key, kv => kv.Value.Subject!)));
            }
            catch (Exception) { }
        }
        return _subjects[userId].Subject;
    }

    /// <summary>nx-baas /internal/photon-identity: the BAAS user id behind a Photon credential, or null.</summary>
    public static Func<string, Task<string?>> BaasResolver(HttpClient http, string baseUrl, string? key)
        => async identity =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/internal/photon-identity");
            if (!string.IsNullOrEmpty(key)) req.Headers.Add("X-Internal-Key", key);
            req.Content = JsonContent.Create(new { identity });
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("baas_user_id", out var b) ? b.GetString() : null;
        };
}
