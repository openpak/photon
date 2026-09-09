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
    /// <summary>Home: where the approved region nodes are read from (openpak.org's
    /// <c>/api/v1/regions</c>). Empty = no regions, every room is served locally.</summary>
    public string RegionsUrl { get; set; } = "";

    /// <summary>Node: this process's node slug (as registered on openpak.org). Set = run the Game
    /// role only and report rooms to <see cref="HomeUrl"/>.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Node: the home server's HTTP (the health port, published through TLS), e.g.
    /// <c>https://photon.openpak.org</c>.</summary>
    public string HomeUrl { get; set; } = "";

    public bool IsNode => !string.IsNullOrWhiteSpace(NodeId);
}

/// <summary>One approved region as <c>/api/v1/regions</c> lists it.</summary>
public sealed record Region(string Id, string Country, string? PhotonAddress);

/// <summary>
/// Home's view of the regions: which nodes exist, which country each serves, which node a room
/// lives on, and which country each authenticated user is from. Refreshed from the regions URL
/// once a minute; countries are asked of the Switch adapter when an identity is registered, so
/// the answer is already here by the time that peer creates a room.
/// </summary>
public sealed class NodeDirectory
{
    private readonly HttpClient _http;
    private readonly string? _regionsUrl;
    private readonly string? _adapterUrl;
    private readonly string? _internalKey;
    private volatile IReadOnlyList<Region> _regions = Array.Empty<Region>();
    private readonly ConcurrentDictionary<string, string> _countryByUser = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _nodeByRoom = new(StringComparer.Ordinal);

    public NodeDirectory(HttpClient http, string? regionsUrl, string? adapterUrl, string? internalKey)
    {
        _http = http;
        _regionsUrl = regionsUrl;
        _adapterUrl = adapterUrl?.TrimEnd('/');
        _internalKey = internalKey;
    }

    public IReadOnlyList<Region> Regions => _regions;

    /// <summary>Re-reads the regions list. Failures keep the last good list: a home that briefly
    /// cannot reach the site must not forget where its rooms are.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_regionsUrl)) return;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(_regionsUrl, ct).ConfigureAwait(false));
            _regions = Parse(doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.Warn("Federation", $"regions refresh failed: {ex.GetType().Name}");
        }
    }

    public static IReadOnlyList<Region> Parse(JsonElement root)
    {
        var list = new List<Region>();
        if (!root.TryGetProperty("regions", out var arr)) return list;
        foreach (var r in arr.EnumerateArray())
        {
            var id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
            var country = r.TryGetProperty("country", out var c) ? c.GetString() : null;
            string? photon = null;
            if (r.TryGetProperty("services", out var s) && s.TryGetProperty("photon", out var p)) photon = p.GetString();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(country))
                list.Add(new Region(id, country.ToUpperInvariant(), photon));
        }
        return list;
    }

    /// <summary>The node serving a country, if one announced a Photon address. First match wins;
    /// ponytail: nearest-by-ping when someone in Perth complains about Sydney.</summary>
    public Region? ForCountry(string? country)
    {
        if (string.IsNullOrEmpty(country)) return null;
        foreach (var r in _regions)
            if (r.PhotonAddress is not null && r.Country == country.ToUpperInvariant()) return r;
        return null;
    }

    public Region? ById(string nodeId)
    {
        foreach (var r in _regions) if (r.Id == nodeId) return r;
        return null;
    }

    /// <summary>The Game address for a room, or null for home's own. A room pinned to a node that
    /// has since vanished from the list falls back to home: better a room somewhere than nowhere.</summary>
    public string? AddressForRoom(string room)
        => _nodeByRoom.TryGetValue(room, out var node) ? ById(node)?.PhotonAddress : null;

    public void PinRoom(string room, string nodeId) => _nodeByRoom[room] = nodeId;
    public void UnpinRoom(string room) => _nodeByRoom.TryRemove(room, out _);
    public string? NodeOfRoom(string room) => _nodeByRoom.TryGetValue(room, out var n) ? n : null;

    public string? CountryOf(string? userId)
        => userId is not null && _countryByUser.TryGetValue(userId, out var c) ? c : null;

    /// <summary>Asks the Switch adapter which country the account behind an identity lives in.
    /// Fire-and-forget at Name Server auth; a miss simply means home serves the room.</summary>
    public async Task PrefetchCountryAsync(string userId, string identity)
    {
        if (string.IsNullOrEmpty(_adapterUrl) || _countryByUser.ContainsKey(userId)) return;
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
        }
        catch (Exception ex)
        {
            Log.Warn("Federation", $"country lookup failed: {ex.GetType().Name}");
        }
    }

    public void SetCountry(string userId, string country) => _countryByUser[userId] = country.ToUpperInvariant();
}

/// <summary>
/// What a node tells home about one of its rooms: who is in it and the game properties the
/// lobby advertises. Sent on every join and leave; the whole room each time, so a lost report
/// is corrected by the next one rather than compounded.
/// </summary>
public sealed class RoomReport
{
    public string Node { get; set; } = "";
    public string Room { get; set; } = "";
    public List<int> Actors { get; set; } = new();
    public Dictionary<string, object?> GameProperties { get; set; } = new();
    /// <summary>The creator's identity ("nex:..." / "bsdid:..."), on the first report only, so home
    /// can publish the room as that player's presence: the node never talks to the adapter.</summary>
    public string? Identity { get; set; }

    public static RoomReport From(string node, Room room, string? identity)
    {
        var report = new RoomReport { Node = node, Room = room.Name, Identity = identity };
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
        var room = registry.GetOrCreate(Room);
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
        if (Actors.Count > 0) nodes.PinRoom(Room, Node); else nodes.UnpinRoom(Room);
    }
}

/// <summary>A node's side: posts room reports to home. Same shared secret the roles already sign
/// tokens with, so a node holds nothing it did not already need.
/// ponytail: per-node secrets when nodes are run by people we have not met.</summary>
public sealed class RoomReporter
{
    private readonly HttpClient _http;
    private readonly string _homeUrl;
    private readonly string _secret;
    private readonly string _node;

    public RoomReporter(HttpClient http, string homeUrl, string secret, string node)
    {
        _http = http;
        _homeUrl = homeUrl.TrimEnd('/');
        _secret = secret;
        _node = node;
    }

    public async Task ReportAsync(Room room, string? identity = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_homeUrl}/internal/node-room");
            req.Headers.Add("X-Photon-Secret", _secret);
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
