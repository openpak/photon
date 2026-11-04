using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing;

/// <summary>Which console ids (the account's "switch" link subjects, i.e. BAAS user ids) the core holds.</summary>
public interface ILinkSource
{
    Task<IReadOnlyList<string>> SwitchSubjectsAsync(string accountId, CancellationToken ct);
}

/// <summary>
/// ListAccountLinks over gRPC without generated code (the one call needed): HTTP/2 cleartext to the account
/// core, x-internal-key metadata, hand-encoded protobuf (request account_id=1, namespace=2; response
/// links=1 {namespace=2, subject_id=3}).
/// </summary>
public sealed class CoreLinks : ILinkSource
{
    private readonly HttpClient _http;
    private readonly string _addr, _key;

    public CoreLinks(HttpClient http, string addr, string key)
    {
        _http = http;
        _addr = addr.Contains("://") ? addr.TrimEnd('/') : "http://" + addr.TrimEnd('/');
        _key = key;
    }

    public async Task<IReadOnlyList<string>> SwitchSubjectsAsync(string accountId, CancellationToken ct)
    {
        var msg = new List<byte>();
        AddString(msg, 1, accountId);
        AddString(msg, 2, "switch");
        var body = new byte[5 + msg.Count];
        var n = msg.Count;
        body[1] = (byte)(n >> 24); body[2] = (byte)(n >> 16); body[3] = (byte)(n >> 8); body[4] = (byte)n;
        msg.CopyTo(body, 5);
        using var req = new HttpRequestMessage(HttpMethod.Post, _addr + "/openpak.account.v1.Links/ListAccountLinks")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        req.Headers.TryAddWithoutValidation("te", "trailers");
        req.Headers.TryAddWithoutValidation("x-internal-key", _key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var status = resp.TrailingHeaders.TryGetValues("grpc-status", out var t) ? t.First()
            : resp.Headers.TryGetValues("grpc-status", out var h) ? h.First() : null;
        if (resp.StatusCode != HttpStatusCode.OK || status != "0")
            throw new InvalidOperationException("account core refused the link lookup (grpc-status " + (status ?? "none") + ")");
        return ParseResponse(data);
    }

    internal static IReadOnlyList<string> ParseResponse(byte[] data)
    {
        var subjects = new List<string>();
        var pos = 0;
        while (pos + 5 <= data.Length)
        {
            var len = (data[pos + 1] << 24) | (data[pos + 2] << 16) | (data[pos + 3] << 8) | data[pos + 4];
            pos += 5;
            if (len < 0 || pos + len > data.Length) break;
            foreach (var link in Fields(data, pos, len).Where(f => f.No == 1))
            {
                string ns = "", subject = "";
                foreach (var f in Fields(data, link.Start, link.Len))
                {
                    if (f.No == 2) ns = Encoding.UTF8.GetString(data, f.Start, f.Len);
                    else if (f.No == 3) subject = Encoding.UTF8.GetString(data, f.Start, f.Len);
                }
                if (ns == "switch" && subject.Length > 0) subjects.Add(subject);
            }
            pos += len;
        }
        return subjects;
    }

    private static IEnumerable<(int No, int Start, int Len)> Fields(byte[] d, int start, int len)
    {
        var p = start;
        var end = start + len;
        while (p < end)
        {
            var tag = Varint(d, ref p);
            if ((tag & 7) == 0) { Varint(d, ref p); continue; }
            if ((tag & 7) != 2) yield break;
            var l = (int)Varint(d, ref p);
            yield return ((int)(tag >> 3), p, l);
            p += l;
        }
    }

    private static ulong Varint(byte[] d, ref int p)
    {
        ulong v = 0;
        var shift = 0;
        while (p < d.Length)
        {
            var b = d[p++];
            v |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static void AddString(List<byte> m, int no, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        m.Add((byte)((no << 3) | 2));
        var l = (uint)b.Length;
        while (l >= 0x80) { m.Add((byte)(l | 0x80)); l >>= 7; }
        m.Add((byte)l);
        m.AddRange(b);
    }
}

/// <summary>Holds the live objects that keep per-account state, so an erase can reach them.</summary>
public sealed class ErasureHub
{
    public IRewardsStore? Store { get; set; }
    public RewardsWatcher? Watcher { get; set; }
    public RewardsEmitter? Emitter { get; set; }
    public AccountIdentityRegistry? Identities { get; set; }
    public NodeDirectory? Nodes { get; set; }
}

/// <summary>
/// Account erasure (openpak/prds#120). Photon keeps nothing per OpenPak account in its Accounts table (that is
/// Steam ids only; console peers are "openpak:" ids and get no row). What it holds, keyed by the BAAS user id
/// (the account's "switch" link), is the rewards state: userId to subject cache, session clock, unsent events,
/// plus in-memory credential, country and tester caches for the userIds mapped to it.
/// </summary>
public sealed class AccountEraser
{
    public const string Service = "photon";
    private static readonly Regex Uuid = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private readonly ILinkSource? _links;
    private readonly ErasureHub _hub;
    private readonly string _key;

    public AccountEraser(ILinkSource? links, ErasureHub hub, string? key)
    {
        _links = links; _hub = hub; _key = key ?? "";
    }

    /// <summary>The erase endpoint's answer for DELETE /internal/accounts/{account_id}/data.</summary>
    public (int Status, string Json) Handle(string accountId, string? authorization)
    {
        static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);
        if (_key.Length == 0) return (503, J(new { error = "erase not configured" }));
        if (!KeyMatches(authorization, _key)) return (401, J(new { error = "unauthorized" }));
        if (!Uuid.IsMatch(accountId)) return (400, J(new { error = "account_id must be a UUID" }));
        if (_links is null) return (503, J(new { error = "account core not configured" }));
        try
        {
            var rows = EraseAsync(accountId.ToLowerInvariant(), CancellationToken.None).GetAwaiter().GetResult();
            return (200, J(new { service = Service, rows }));
        }
        catch (Exception ex)
        {
            Log.Warn("Erase", $"erase failed: {ex.GetType().Name}");
            return (500, J(new { error = "erase failed" }));
        }
    }

    public async Task<int> EraseAsync(string accountId, CancellationToken ct)
    {
        var subjects = (await _links!.SwitchSubjectsAsync(accountId, ct).ConfigureAwait(false)).Distinct().ToList();
        var rows = 0;
        if (subjects.Count > 0)
        {
            var users = new List<string>();
            if (_hub.Watcher is { } w) { var (n, u) = w.ForgetSubjects(subjects); rows += n; users = u; }
            else if (_hub.Store is { } s) rows += RewardsWatcher.ForgetInStore(s, subjects) + SessionClock.ForgetInStore(s, subjects);
            if (_hub.Emitter is { } e) rows += e.Forget(subjects);
            else if (_hub.Store is { } s2) rows += RewardsEmitter.ForgetInStore(s2, subjects);
            foreach (var u in users)
            {
                if (_hub.Identities?.Forget(u) == true) rows++;
                rows += _hub.Nodes?.ForgetUser(u) ?? 0;
            }
        }
        Log.Info("Erase", $"account erased account={accountId} rows={rows} service={Service}");
        return rows;
    }

    public static bool KeyMatches(string? header, string key)
    {
        if (key.Length == 0 || header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
