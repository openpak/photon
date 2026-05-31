namespace BlackIce.Photon.Crypto;

/// <summary>
/// Standard Oakley Diffie-Hellman group parameters (RFC 2409). Photon uses Group 1 (768-bit)
/// with generator 22. These are well-known public constants, not Photon-specific code.
/// </summary>
public static class OakleyGroups
{
    public const int Generator = 22;

    /// <summary>Oakley Group 1 768-bit prime, big-endian.</summary>
    public static readonly byte[] OakleyPrime768 =
    {
        255, 255, 255, 255, 255, 255, 255, 255, 201, 15,
        218, 162, 33, 104, 194, 52, 196, 198, 98, 139,
        128, 220, 28, 209, 41, 2, 78, 8, 138, 103,
        204, 116, 2, 11, 190, 166, 59, 19, 155, 34,
        81, 74, 8, 121, 142, 52, 4, 221, 239, 149,
        25, 179, 205, 58, 67, 27, 48, 43, 10, 109,
        242, 95, 20, 55, 79, 225, 53, 109, 109, 81,
        194, 69, 228, 133, 181, 118, 98, 94, 126, 198,
        244, 76, 66, 233, 166, 58, 54, 32, 255, 255,
        255, 255, 255, 255, 255, 255,
    };
}
