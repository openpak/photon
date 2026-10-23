using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Federation settings. Matchmaking (Name + Master) runs at home; a region node runs only the
/// Game role and reports its rooms back to home so the lobby still lists them. The home Master
/// hands out the node's Game address per room, so a joiner lands where the host's room lives.
/// </summary>
public sealed class FederationOptions
{
    /// <summary>Home: the account core's HTTP base URL, where the routing table is read
    /// (<c>/internal/v1/routing</c>), e.g. <c>http://openpak-account:8000</c>. Empty = no regions, every
    /// room is served locally, exactly as before federation. Env <c>OPENPAK_ROUTING_URL</c> also works.</summary>
    public string RoutingUrl { get; set; } = "";

    /// <summary>Home: the core's read-only routing key (<c>ACCOUNT_ROUTING_KEY</c> there). Env
    /// <c>OPENPAK_ROUTING_KEY</c> also works.</summary>
    public string RoutingKey { get; set; } = "";

    /// <summary>Retired: the public <c>/api/v1/regions</c> list carries no region names. Ignored, with a
    /// warning at start, so an old config still boots (home-only).</summary>
    public string RegionsUrl { get; set; } = "";

    /// <summary>Home: each node's room-report key, by node id. A report is accepted only with its own
    /// node's key, so one node cannot speak for another.</summary>
    public Dictionary<string, string> NodeKeys { get; set; } = new();

    /// <summary>Node: this process's node slug (as registered on openpak.org). Set = run the Game
    /// role only and report rooms to <see cref="HomeUrl"/>.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Node: the home server's HTTP (the health port, published through TLS), e.g.
    /// <c>https://photon.openpak.org</c>.</summary>
    public string HomeUrl { get; set; } = "";

    /// <summary>Node: the key it sends with room reports (home's <see cref="NodeKeys"/> entry for it).</summary>
    public string ReportKey { get; set; } = "";

    /// <summary>UDP port answering Photon's region ping (13 bytes, echoed). Clients ping a region's host on
    /// 5055 whatever port it advertises, so a node whose Game role is elsewhere runs this. 0 = off; it is
    /// also skipped when a listener already owns the port (every listener answers pings itself).</summary>
    public int PingPort { get; set; } = 5055;

    public bool IsNode => !string.IsNullOrWhiteSpace(NodeId);
}

/// <summary>
/// Home's view of the regions: the routing table (which nodes are in rotation and under which Photon region
/// names), which node a room lives on, and per player the account country and whether the account may reach
/// test nodes. The table is refreshed from the account core every 30 s; a player's country and tester flag
/// are asked when they authenticate, so the answer is here by the time they create a room.
/// </summary>
public sealed class NodeDirectory
{
    /// <summary>Node role a Photon room needs, and the optional separate ping responder.</summary>
    public const string ServicePhoton = "photon", ServicePing = "photon_ping";
    private static readonly TimeSpan TesterIpTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string? _routingUrl;
    private readonly string? _routingKey;
    private readonly string? _adapterUrl;
    private readonly string? _internalKey;
    private volatile RoutingTable? _table;
    private readonly ConcurrentDictionary<string, string> _countryByUser = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _testers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<System.Net.IPAddress, DateTime> _testerIps = new();
    private readonly ConcurrentDictionary<string, string> _nodeByRoom = new(StringComparer.Ordinal);

    public NodeDirectory(HttpClient http, string? routingUrl, string? routingKey, string? adapterUrl, string? internalKey)
    {
        _http = http;
        _routingUrl = string.IsNullOrWhiteSpace(routingUrl) ? null : routingUrl.TrimEnd('/');
        _routingKey = routingKey;
        _adapterUrl = adapterUrl?.TrimEnd('/');
        _internalKey = internalKey;
    }

    public bool Enabled => _routingUrl is not null;

    /// <summary>The last good table, or null (no routing configured, or never fetched): home for everything.</summary>
    public RoutingTable? Table { get => _table; set => _table = value; }

    /// <summary>Re-reads the routing table. Failures keep the last good one: a home that briefly cannot
    /// reach the core must not forget where its rooms are.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_routingUrl is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_routingUrl}/internal/v1/routing");
            if (!string.IsNullOrEmpty(_routingKey)) req.Headers.Add("X-Routing-Key", _routingKey);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("Federation", $"routing refresh failed: HTTP {(int)resp.StatusCode}");
                return;
            }
            if (RoutingTable.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is { } t) _table = t;
            else Log.Warn("Federation", "routing refresh failed: not a routing table");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn("Federation", $"routing refresh failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Places a new room: the client's region choice (param 210, sealed in its token) wins; otherwise the
    /// account country; then the fallback chain; else home. Returns the node's Game address and pins the
    /// room there, or null for home.
    /// </summary>
    public string? Place(string room, string? userId, string? regionToken)
    {
        var t = _table;
        if (t is null) { UnpinRoom(room); return null; }
        var r = t.Resolve(RoutingTable.Stack, regionToken, CountryOf(userId), IsTester(userId), ServicePhoton, room);
        if (r.IsHome || r.Address is null) { UnpinRoom(room); return null; }
        PinRoom(room, r.Node!);
        Log.Info("Federation", $"room {Redact.Room(room)} placed on {r.Node} ({r.Region}, {r.Via})");
        return r.Address;
    }

    /// <summary>
    /// What GetRegions lists besides home's own "eu": one real Photon token per region with a node this
    /// caller may use, each with the address the client pings (the node's ping responder, else its Game
    /// host; the client pings port 5055 of that host whatever port is given).
    /// </summary>
    public List<(string Token, string Address)> Adverts(bool tester)
    {
        var t = _table;
        var list = new List<(string, string)>();
        if (t is null) return list;
        foreach (var a in t.Advertise(RoutingTable.Stack, ServicePhoton, tester))
        {
            var node = t.NodeById(a.Node);
            var ping = node is not null && node.Services.TryGetValue(ServicePing, out var p) && !string.IsNullOrEmpty(p) ? p : a.Address;
            list.Add((a.Token, ping));
        }
        return list;
    }

    /// <summary>The Game address for a room, or null for home's own. A room pinned to a node that has
    /// since left rotation falls back to home: better a room somewhere than nowhere.</summary>
    public string? AddressForRoom(string room)
        => _nodeByRoom.TryGetValue(room, out var node) && _table?.NodeById(node) is { } n &&
           n.Services.TryGetValue(ServicePhoton, out var addr) && !string.IsNullOrEmpty(addr) ? addr : null;

    public void PinRoom(string room, string nodeId) => _nodeByRoom[room] = nodeId;
    public void UnpinRoom(string room) => _nodeByRoom.TryRemove(room, out _);
    public string? NodeOfRoom(string room) => _nodeByRoom.TryGetValue(room, out var n) ? n : null;

    public string? CountryOf(string? userId)
        => userId is not null && _countryByUser.TryGetValue(userId, out var c) ? c : null;

    public bool IsTester(string? userId) => userId is not null && _testers.ContainsKey(userId);

    /// <summary>GetRegions comes before Authenticate, so the only thing known about the asker is its
    /// address: one that authenticated as an allow-listed account in the last day sees test regions.</summary>
    public bool IsTesterAddress(System.Net.IPAddress? ip)
        => ip is not null && _testerIps.TryGetValue(ip, out var at) && DateTime.UtcNow - at < TesterIpTtl;

    /// <summary>
    /// Asks the Switch adapter which country the account behind an identity lives in, and (routing on) the
    /// account core whether that account is on the test allow-list. Fire-and-forget at Name Server auth; a
    /// miss simply means home serves the room.
    /// </summary>
    public async Task PrefetchCountryAsync(string userId, string identity, System.Net.IPAddress? from = null)
    {
        if (string.IsNullOrEmpty(_adapterUrl) || (_countryByUser.ContainsKey(userId) && _routingUrl is null)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_adapterUrl}/internal/photon-identity");
            if (!string.IsNullOrEmpty(_internalKey)) req.Headers.Add("X-Internal-Key", _internalKey);
            req.Content = JsonContent.Create(new { identity });
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("country", out var c) && c.GetString() is { Length: 2 } country)
                _countryByUser[userId] = country.ToUpperInvariant();
            if (_routingUrl is not null && doc.RootElement.TryGetProperty("baas_user_id", out var b) && b.GetString() is { Length: > 0 } baas)
                await PrefetchTesterAsync(userId, baas, from).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Federation", $"country lookup failed: {ex.GetType().Name}");
        }
    }

    private async Task PrefetchTesterAsync(string userId, string baasUserId, System.Net.IPAddress? from)
    {
        var url = $"{_routingUrl}/internal/v1/routing/account?namespace=switch&subject={Uri.EscapeDataString(baasUserId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_routingKey)) req.Headers.Add("X-Routing-Key", _routingKey);
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("tester", out var t) && t.ValueKind == JsonValueKind.True)
        {
            _testers[userId] = true;
            if (from is not null) _testerIps[from] = DateTime.UtcNow;
        }
        else _testers.TryRemove(userId, out _);
        if (!_countryByUser.ContainsKey(userId) && doc.RootElement.TryGetProperty("country", out var c) && c.GetString() is { Length: 2 } country)
            _countryByUser[userId] = country.ToUpperInvariant();
    }

    /// <summary>Asks the Switch adapter whether the OpenPak account behind an identity is banned
    /// (website/docs/ban-lookup.md): nx-baas answers /internal/photon-identity with 403
    /// {"error":"account_banned"} (or "account_deleted") for a locked-out account. True only on that
    /// answer; no adapter, an unknown identity or an adapter outage is not a ban.</summary>
    public async Task<bool> IsBannedAsync(string identity)
    {
        if (string.IsNullOrEmpty(_adapterUrl)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_adapterUrl}/internal/photon-identity");
            if (!string.IsNullOrEmpty(_internalKey)) req.Headers.Add("X-Internal-Key", _internalKey);
            req.Content = JsonContent.Create(new { identity });
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return resp.StatusCode == System.Net.HttpStatusCode.Forbidden;
        }
        catch (Exception ex)
        {
            Log.Warn("Federation", $"ban lookup failed: {ex.GetType().Name}");
            return false;
        }
    }

    public void SetCountry(string userId, string country) => _countryByUser[userId] = country.ToUpperInvariant();
    public void SetTester(string userId, bool tester, System.Net.IPAddress? from = null)
    {
        if (!tester) { _testers.TryRemove(userId, out _); return; }
        _testers[userId] = true;
        if (from is not null) _testerIps[from] = DateTime.UtcNow;
    }
}

/// <summary>
/// What a node tells home about one of its rooms: who is in it and the game properties the
/// lobby advertises. Sent on every join and leave; the whole room each time, so a lost report
/// is corrected by the next one rather than compounded.
/// </summary>
public sealed class RoomReport
{
    public string Node { get; set; } = "";
    /// <summary>The room's public name.</summary>
    public string Room { get; set; } = "";
    /// <summary>The AppId and AppVersion the room's clients authenticated with, which place it in
    /// its game build's namespace at home too (<see cref="RoomRegistry.KeyFor"/>). A report without
    /// them is a room of clients that sent none.</summary>
    public string? AppId { get; set; }
    public string? AppVersion { get; set; }
    public List<int> Actors { get; set; } = new();
    public Dictionary<string, object?> GameProperties { get; set; } = new();
    /// <summary>The creator's identity ("nex:..." / "bsdid:..."), on the first report only, so home
    /// can publish the room as that player's presence: the node never talks to the adapter.</summary>
    public string? Identity { get; set; }

    public static RoomReport From(string node, Room room, string? identity)
    {
        var report = new RoomReport
        {
            Node = node, Room = room.Name, AppId = room.AppId, AppVersion = room.AppVersion, Identity = identity,
        };
        lock (room.ActorNumbers) report.Actors.AddRange(room.ActorNumbers);
        foreach (var kv in room.GameProperties)
            if (kv.Key is string k && kv.Value is string or int or long or bool or double)
                report.GameProperties[k] = kv.Value;
        return report;
    }

    /// <summary>Applies a report to home's registry. JSON knows strings, numbers and booleans;
    /// Photon's typed hashtable had more, so only the lobby-relevant string-keyed props travel.
    /// Numbers come back as int (what Fusion's RoomInfo parser unboxes).</summary>
    public void ApplyTo(RoomRegistry registry, NodeDirectory nodes)
    {
        var key = RoomRegistry.KeyFor(AppId, AppVersion, Room);
        var room = registry.GetOrCreate(key);
        lock (room.ActorNumbers)
        {
            room.ActorNumbers.Clear();
            room.ActorNumbers.AddRange(Actors);
        }
        var props = new System.Collections.Hashtable();
        foreach (var kv in GameProperties)
        {
            object? v = kv.Value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt32(out var i) ? (object)i : e.GetDouble(),
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                JsonElement => null,
                var other => other,
            };
            if (v is not null) props[kv.Key] = v;
        }
        if (props.Count > 0) room.SetProperties(null, props);
        if (Actors.Count > 0) nodes.PinRoom(key, Node); else nodes.UnpinRoom(key);
    }
}

/// <summary>A node's side: posts room reports to home with its own report key (home's
/// <see cref="FederationOptions.NodeKeys"/> entry for it). Tokens are checked with home's public key, so
/// the node holds nothing that could mint an identity.</summary>
public sealed class RoomReporter
{
    private readonly HttpClient _http;
    private readonly string _homeUrl;
    private readonly string _key;
    private readonly string _node;

    public RoomReporter(HttpClient http, string homeUrl, string reportKey, string node)
    {
        _http = http;
        _homeUrl = homeUrl.TrimEnd('/');
        _key = reportKey;
        _node = node;
    }

    public async Task ReportAsync(Room room, string? identity = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_homeUrl}/internal/node-room");
            req.Headers.Add("X-Node-Key", _key);
            req.Content = JsonContent.Create(RoomReport.From(_node, room, identity));
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Log.Warn("Federation", $"room report failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warn("Federation", $"room report failed: {ex.GetType().Name}");
        }
    }
}
