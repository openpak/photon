using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// The account core's routing table (GET /internal/v1/routing) and the one placement rule, the C# twin of
/// the core's <c>pkg/routing</c>. Both run the same fixture (<c>RoutingCases.json</c>, copied from
/// <c>account/pkg/routing/testdata/cases.json</c>), so the two cannot drift apart unnoticed.
///
/// <para>The rule: an explicit in-game region always wins (a token shared by several regions is refined by
/// the account country inside that set); with no choice the account country decides; a region with no
/// eligible node walks its fallback chain, which ends at home. An unknown token, no country or no table all
/// mean home. Test nodes are only for allow-listed accounts. Nothing here throws.</para>
/// </summary>
public sealed class RoutingTable
{
    public const string HomeRegionDefault = "eu-west";
    public const string Stack = "photon";

    [JsonPropertyName("home")] public string Home { get; set; } = HomeRegionDefault;
    [JsonPropertyName("regions")] public List<RoutingRegion> Regions { get; set; } = new();
    [JsonPropertyName("country_routes")] public Dictionary<string, List<string>> CountryRoutes { get; set; } = new();
    [JsonPropertyName("nodes")] public List<RoutingNode> Nodes { get; set; } = new();

    /// <summary>Parses the core's answer; null when it is not a table. Go encodes an empty list as null, so
    /// nulls are made empty here once instead of checked everywhere.</summary>
    public static RoutingTable? Parse(string json)
    {
        RoutingTable? t;
        try { t = JsonSerializer.Deserialize<RoutingTable>(json); }
        catch (JsonException) { return null; }
        if (t is null) return null;
        t.Regions ??= new();
        t.Nodes ??= new();
        t.CountryRoutes ??= new();
        t.Regions.RemoveAll(r => r is null);
        t.Nodes.RemoveAll(n => n is null);
        foreach (var r in t.Regions)
        {
            r.Countries ??= new();
            r.Fallback ??= new();
            r.Aliases ??= new();
        }
        foreach (var n in t.Nodes) n.Services ??= new();
        return t;
    }

    private string HomeCode => string.IsNullOrEmpty(Home) ? HomeRegionDefault : Home;

    public static string NormalizeToken(string? token)
    {
        token = (token ?? "").Trim();
        var slash = token.IndexOf('/');
        if (slash >= 0) token = token[..slash];
        return token.ToLowerInvariant();
    }

    public List<string> RegionsForToken(string stack, string? token)
    {
        var t = NormalizeToken(token);
        var outList = new List<string>();
        if (t.Length == 0) return outList;
        foreach (var r in Regions)
            if (r.Aliases.TryGetValue(stack, out var names) && names.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase)))
                outList.Add(r.Code);
        return outList;
    }

    public string? RegionOfCountry(string? country)
    {
        var c = (country ?? "").Trim().ToUpperInvariant();
        if (c.Length == 0) return null;
        foreach (var r in Regions)
            if (r.Countries.Contains(c)) return r.Code;
        return null;
    }

    public List<string> CountryRoute(string? country)
    {
        var c = (country ?? "").Trim().ToUpperInvariant();
        if (CountryRoutes.TryGetValue(c, out var route)) return new List<string>(route);
        return RegionOfCountry(c) is { } code ? new List<string> { code } : new List<string>();
    }

    public List<string> Chain(List<string> start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<string>(start);
        var outList = new List<string>();
        for (var i = 0; i < queue.Count; i++)
        {
            if (!seen.Add(queue[i])) continue;
            outList.Add(queue[i]);
            if (Regions.FirstOrDefault(r => r.Code == queue[i]) is { } region) queue.AddRange(region.Fallback);
        }
        return outList;
    }

    public string? NodeRegion(RoutingNode n) => !string.IsNullOrEmpty(n.Region) ? n.Region : RegionOfCountry(n.Country);

    /// <summary>A region's nodes running <paramref name="service"/> this caller may use, stable order; a
    /// tester gets the test nodes first when there are any.</summary>
    public List<RoutingNode> Eligible(string region, string service, bool tester)
    {
        var pub = new List<RoutingNode>();
        var test = new List<RoutingNode>();
        foreach (var n in Nodes)
        {
            if (NodeRegion(n) != region || !n.Services.TryGetValue(service, out var addr) || string.IsNullOrEmpty(addr)) continue;
            if (n.Test) { if (tester) test.Add(n); }
            else pub.Add(n);
        }
        var list = test.Count > 0 ? test : pub;
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    public RoutingResult Resolve(string stack, string? token, string? country, bool tester, string service, string? key = null)
    {
        var home = new RoutingResult(HomeCode, null, null, RoutingResult.ViaHome);
        if (string.IsNullOrEmpty(service)) return home;
        var route = CountryRoute(country);
        List<string> start;
        var via = RoutingResult.ViaCountry;
        if (NormalizeToken(token).Length > 0)
        {
            var cands = RegionsForToken(stack, token);
            if (cands.Count == 0) return home; // a name we do not know: never guess
            via = RoutingResult.ViaExplicit;
            var pick = route.FirstOrDefault(cands.Contains) ?? cands[0];
            start = new List<string> { pick };
        }
        else start = route;

        var chain = Chain(start);
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i] == HomeCode) return home; // home serves its own region
            var nodes = Eligible(chain[i], service, tester);
            if (nodes.Count == 0) continue;
            var n = nodes[0];
            if (!string.IsNullOrEmpty(key)) n = nodes[(int)(Fnv1a32(key) % (uint)nodes.Count)];
            return new RoutingResult(chain[i], n.Id, n.Services[service], i >= start.Count ? RoutingResult.ViaFallback : via);
        }
        return home;
    }

    /// <summary>One canonical token per non-home region with an eligible node, with that node's address; a
    /// token home already uses is skipped (the caller lists home itself).</summary>
    public List<(string Token, string Region, string Node, string Address)> Advertise(string stack, string service, bool tester)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Regions.FirstOrDefault(r => r.Code == HomeCode) is { } h && h.Aliases.TryGetValue(stack, out var hn))
            foreach (var a in hn) taken.Add(a);
        var outList = new List<(string, string, string, string)>();
        foreach (var r in Regions)
        {
            if (r.Code == HomeCode || !r.Aliases.TryGetValue(stack, out var names) || names.Count == 0 || taken.Contains(names[0])) continue;
            var nodes = Eligible(r.Code, service, tester);
            if (nodes.Count == 0) continue;
            taken.Add(names[0]);
            outList.Add((names[0], r.Code, nodes[0].Id, nodes[0].Services[service]));
        }
        return outList;
    }

    public RoutingNode? NodeById(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    private static uint Fnv1a32(string s)
    {
        uint h = 2166136261;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
        return h;
    }
}

public sealed class RoutingRegion
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("countries")] public List<string> Countries { get; set; } = new();
    [JsonPropertyName("fallback")] public List<string> Fallback { get; set; } = new();
    [JsonPropertyName("aliases")] public Dictionary<string, List<string>> Aliases { get; set; } = new();
}

public sealed class RoutingNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("services")] public Dictionary<string, string> Services { get; set; } = new();
    [JsonPropertyName("test")] public bool Test { get; set; }
}

/// <summary>Where a new room goes. <see cref="Node"/> null = home.</summary>
public readonly record struct RoutingResult(string Region, string? Node, string? Address, string Via)
{
    public const string ViaExplicit = "explicit", ViaCountry = "country", ViaFallback = "fallback", ViaHome = "home";
    public bool IsHome => Node is null;
}
