namespace BlackIce.Server.Data;

/// <summary>
/// Helpers for SteamID64 values. NOTE: a well-formed SteamID is an *asserted* identity, not a
/// *proven* one — the client supplies it. Format validation here is defense-in-depth (rejects
/// junk/GUIDs), NOT anti-spoofing. Proving ownership requires Steam ticket validation
/// (see SECURITY.md). Do not gate privilege escalation on a network-asserted SteamID.
/// </summary>
public static class SteamId
{
    // Individual account in the Public universe: the high 32 bits are 0x01100001.
    private const ulong IndividualPublicMask = 0xFFFFFFFF00000000UL;
    private const ulong IndividualPublicTag = 0x0110000100000000UL;

    public static bool IsValidIndividual(string? value) =>
        ulong.TryParse(value, out var id) && (id & IndividualPublicMask) == IndividualPublicTag;
}
