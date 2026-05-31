using System.Reflection;
using BlackIce.Photon.Crypto;
using Xunit;

namespace BlackIce.Photon.Tests;

/// <summary>
/// Verifies our DH provider interoperates with Photon's own (internal) DiffieHellmanCryptoProvider,
/// reached via reflection. Proves the key agreement and AES scheme match byte-for-byte: a key
/// derived on each side encrypts/decrypts the other's ciphertext.
/// </summary>
public class CryptoTests
{
    private static Type RealProviderType()
    {
        var asm = typeof(ExitGames.Client.Photon.StreamBuffer).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "DiffieHellmanCryptoProvider"
                                                   && !x.Name.Contains("Native"));
        Assert.NotNull(t);
        return t!;
    }

    [Fact]
    public void Our_provider_agrees_with_real_photon_provider()
    {
        var t = RealProviderType();
        var real = Activator.CreateInstance(t, nonPublic: true)!;
        var realPub = (byte[])t.GetProperty("PublicKey")!.GetValue(real)!;

        var ours = new DiffieHellmanCryptoProvider();
        ours.DeriveSharedKey(realPub);
        t.GetMethod("DeriveSharedKey")!.Invoke(real, new object[] { ours.PublicKey });

        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // real encrypts -> we decrypt
        var realCipher = (byte[])t.GetMethod("Encrypt", new[] { typeof(byte[]) })!.Invoke(real, new object[] { plaintext })!;
        Assert.Equal(plaintext, ours.Decrypt(realCipher));

        // we encrypt -> real decrypts
        var ourCipher = ours.Encrypt(plaintext);
        var realPlain = (byte[])t.GetMethod("Decrypt", new[] { typeof(byte[]) })!.Invoke(real, new object[] { ourCipher })!;
        Assert.Equal(plaintext, realPlain);
    }
}
