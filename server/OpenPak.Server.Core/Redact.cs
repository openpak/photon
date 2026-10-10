using System.Collections;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenPak.Photon;

namespace OpenPak.Server.Core;

/// <summary>
/// Keeps personal data out of logs. A log line never carries a user id, account or Steam id, ticket, token,
/// nickname, room name or client address; where two lines have to be matched up it carries a <see cref="Tag"/>
/// instead. Packet and parameter-table dumps are off unless <c>PHOTON_TRACE_BODIES=1</c> is set (debugging on
/// a developer machine only; never in production).
/// </summary>
public static class Redact
{
    // Random per process: tags correlate lines within one run and cannot be reversed or compared across restarts.
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Raw packet / parameter dumps are emitted only when this is true. Read once from
    /// <c>PHOTON_TRACE_BODIES</c>; tests may flip it.</summary>
    public static bool TraceBodies { get; set; } = Environment.GetEnvironmentVariable("PHOTON_TRACE_BODIES") == "1";

    /// <summary>A short stable per-process tag for <paramref name="value"/> ("~" and six hex digits).</summary>
    public static string Tag(object? value)
    {
        var h = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        return "~" + Convert.ToHexString(h.AsSpan(0, 3)).ToLowerInvariant();
    }

    /// <summary>Tag for a client address (the address and port together).</summary>
    public static string Peer(EndPoint? remote) => Tag(remote);

    /// <summary>Tag for a room: users type room names, so a name never reaches a log. A registry key
    /// (<c>[appId/appVersion] name</c>) and the bare name it holds get the same tag.</summary>
    public static string Room(string? nameOrKey)
    {
        if (nameOrKey is null) return "<none>";
        if (nameOrKey.StartsWith('['))
        {
            int close = nameOrKey.IndexOf("] ", StringComparison.Ordinal);
            int slash = nameOrKey.IndexOf('/');
            if (close > 0 && slash > 0 && slash < close) nameOrKey = nameOrKey[(close + 2)..];
        }
        return Tag(nameOrKey);
    }

    /// <summary>Parameter table with the key names and the shape of each value, but not the values:
    /// numbers and booleans (codes, counts, actor numbers) are shown, strings and byte arrays are reduced to
    /// their length, everything else to its type. Full table only under <see cref="TraceBodies"/>.</summary>
    public static string Params(IDictionary<byte, object> p)
    {
        if (TraceBodies) return PhotonNames.Params(p);
        if (p.Count == 0) return "{}";
        var sb = new StringBuilder();
        foreach (var kv in p)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(PhotonNames.Param(kv.Key)).Append('(').Append(kv.Key).Append(")=").Append(Shape(kv.Value));
        }
        return sb.ToString();
    }

    /// <summary>Shape of a value (see <see cref="Params"/>).</summary>
    public static string Shape(object? v) => v switch
    {
        null => "null",
        bool or byte or sbyte or short or ushort or int or uint or float or double => $"{Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)}({v.GetType().Name})",
        string s => $"string[{s.Length}]",
        byte[] b => $"byte[{b.Length}]",
        IDictionary d => $"dict[{d.Count}]",
        ICollection c => $"{v.GetType().Name}[{c.Count}]",
        _ => v.GetType().Name,
    };

    /// <summary>A parameter value: its shape, or the full formatted value under <see cref="TraceBodies"/>.</summary>
    public static string Value(object? v) => TraceBodies ? PhotonNames.Value(v) : Shape(v);

    /// <summary>A configured URL without credentials, query string or fragment.</summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "<none>";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "<unparseable url>";
    }

    /// <summary>Hex dump, or just its length unless <see cref="TraceBodies"/> is set.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int max = 256) =>
        TraceBodies ? PhotonNames.Hex(bytes, max) : $"<{bytes.Length}B withheld>";

    // IPv4 or bracketed IPv6 with a port, or a bare IPv4: what socket / HTTP error texts spell out.
    private static readonly Regex Address = new(
        @"(\[[0-9A-Fa-f:.]+\]:\d+)|((?<!Version=)\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b)", RegexOptions.Compiled);

    /// <summary>Text with any address written in it replaced by a tag (exception messages from the socket and
    /// HTTP layers name the remote end).</summary>
    public static string Scrub(string text) => Address.Replace(text, m => Tag(m.Value));

    /// <summary>An exception's text without the addresses it spells out.</summary>
    public static string Err(Exception ex) => Scrub(ex.ToString());
}
