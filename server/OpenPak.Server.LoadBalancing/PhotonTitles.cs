using System.Collections.Generic;
using System.Linq;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// Which title a Photon AppId belongs to. One server hosts several games — Outbound and
/// Moving Out each dial it with their own AppId — and a room has to be filed under the right
/// one or a player's friend presence names somebody else's game.
///
/// Configured as <c>OPENPAK_PHOTON_TITLES=&lt;appid&gt;=&lt;titleid&gt;,…</c>, e.g.
/// <c>cffbe809-…=0100ED9024EB8000,a956fef2-…=0100C4C00E73E000</c>. AppIds compare
/// case-insensitively (clients send them in either case).
/// </summary>
public static class PhotonTitles
{
    /// <param name="spec">The raw <c>OPENPAK_PHOTON_TITLES</c> value; null/empty yields an empty map.</param>
    public static IReadOnlyDictionary<string, string> Parse(string? spec)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (spec ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2) continue;
            var appId = pair[0].Trim();
            var titleId = pair[1].Trim();
            if (appId.Length > 0 && titleId.Length > 0) map[appId] = titleId;
        }
        return map;
    }
}
