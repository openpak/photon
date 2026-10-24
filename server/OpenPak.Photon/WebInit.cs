using System.Text;

namespace OpenPak.Photon;

/// <summary>
/// The web-style Init some clients send as their first reliable message instead of the binary
/// [0xF3][0]... Init: an HTTP-like request line, "POST /?init=&amp;app=&lt;AppId&gt;&amp;clientversion=...",
/// optionally followed by header lines and a binary trailer. The Photon C++ SDK uses it on the Master
/// (and Game) hop after AuthenticateOnce, where it carries the Name Server's token so the client
/// does not authenticate again.
/// </summary>
public sealed record WebInit(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Query,
                             IReadOnlyList<string> HeaderLines, byte[] Trailer)
{
    private const int MaxHeader = 8192;

    /// <summary>True when <paramref name="payload"/> starts with an ASCII "POST /" or "GET /" request line.</summary>
    public static bool Looks(ReadOnlySpan<byte> payload) =>
        payload.StartsWith("POST /"u8) || payload.StartsWith("GET /"u8);

    public static bool TryParse(byte[] payload, out WebInit init)
    {
        init = null!;
        if (!Looks(payload)) return false;

        // The text part: up to a blank line (CRLFCRLF), else up to the first byte that is not printable
        // ASCII / CR / LF / TAB, else the whole payload.
        int end = 0, trailerStart = -1;
        while (end < payload.Length && end < MaxHeader)
        {
            if (end + 3 < payload.Length && payload[end] == '\r' && payload[end + 1] == '\n'
                && payload[end + 2] == '\r' && payload[end + 3] == '\n')
            { trailerStart = end + 4; break; }
            byte b = payload[end];
            if (!(b is >= 0x20 and < 0x7F || b is (byte)'\r' or (byte)'\n' or (byte)'\t')) break;
            end++;
        }
        if (trailerStart < 0) trailerStart = end;

        var text = Encoding.ASCII.GetString(payload, 0, end);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var requestLine = lines[0];
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return false;
        var target = parts[1];
        int q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = new List<KeyValuePair<string, string>>();
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var k = eq >= 0 ? pair[..eq] : pair;
                var v = eq >= 0 ? pair[(eq + 1)..] : "";
                try { v = Uri.UnescapeDataString(v); } catch (UriFormatException) { }
                query.Add(new(k, v));
            }

        init = new WebInit(parts[0], path, query, lines.Skip(1).Where(l => l.Length > 0).ToList(),
                           payload[trailerStart..]);
        return true;
    }

    public string? Get(string key) => Query.FirstOrDefault(kv => kv.Key == key).Value;

    /// <summary>Every value that could be the client's auth token: each query value, each header value,
    /// and the trailer (as a GpBinary typed string / byte[], or as plain text).</summary>
    public IEnumerable<string> TokenCandidates()
    {
        foreach (var kv in Query) if (kv.Value.Length > 0) yield return kv.Value;
        foreach (var line in HeaderLines)
        {
            int c = line.IndexOf(':');
            if (c >= 0) yield return line[(c + 1)..].Trim();
        }
        if (Trailer.Length == 0) yield break;
        object? typed = null;
        try { typed = new GpBinaryReader(Trailer).ReadTyped(); } catch (Exception) { }
        if (typed is string s) yield return s;
        else if (typed is byte[] b) yield return Encoding.UTF8.GetString(b);
        yield return Encoding.UTF8.GetString(Trailer).Trim('\0', '\r', '\n', ' ');
    }

    /// <summary>A log-safe summary: names and lengths, the AppId and versions in full (public values),
    /// never a token.</summary>
    public string Describe()
    {
        static bool Public(string k) => k is "init" or "app" or "clientversion" or "protocol" or "clientversioncode";
        var q = string.Join(", ", Query.Select(kv => Public(kv.Key) ? $"{kv.Key}={kv.Value}" : $"{kv.Key}=<{kv.Value.Length}>"));
        var h = string.Join(", ", HeaderLines.Select(l => { int c = l.IndexOf(':'); return c >= 0 ? $"{l[..c]}:<{l.Length - c - 1}>" : $"<{l.Length}>"; }));
        var t = Trailer.Length == 0 ? "none" : $"{Trailer.Length}B type {Trailer[0]}";
        return $"{Method} {Path} [{q}] headers [{h}] trailer {t}";
    }
}
