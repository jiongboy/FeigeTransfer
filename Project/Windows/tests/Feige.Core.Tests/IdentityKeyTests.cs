using System.Security.Cryptography;
using System.Text;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// RSA-3072 identity, RSASSA-PSS signing and fingerprints (通信协议 §3.6).
/// <para>
/// <see cref="OpenSslPssSignature_Verifies"/> is the interop gate: the key and signature were produced by
/// OpenSSL 3.5.7 (<c>openssl dgst -sha256 -sign … -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:32</c>)
/// over the §8.1 transcript bytes, and .NET must accept them. The signature covers
/// <c>SHA-256 = 779855ca…</c>, which is the documented §8.1 digest, so this also pins the transcript bytes.
/// </para>
/// </summary>
public sealed class IdentityKeyTests
{
    // OpenSSL-generated RSA-3072 public key (SubjectPublicKeyInfo DER). Public data only.
    private const string OpenSslSpki =
        "308201a2300d06092a864886f70d01010105000382018f003082018a0282018100b334177d29b985fefa94c2eb00d22f757495b02b8e4cc269932cb8c22a73727e27bdf334ed58f052793abc3a7a9fd58c723a3ff47f248b277c32f5bc2c399aedb90634773d5b184404e197e782ca29bc929e61ceeade6589e1bfa2cbe0bfa34f7b59643dfe600e1912f445987697dc8247455c3d0cc3811e9ca1950e5f9832613c21136deb905ae6453b677600ab610a5bcbfb3bf88e7ec68e44e912834c97cb7c9cb1e17fd4eb71bb5d2219108c8bfb9284d1c3e614733d8a7aab79313088910d5e82c230054092c0b21b69d3e679f71443afda9ff478578a3c6828c5afae23944fec3685a050b794086d4666e7ae5955f8c78d913015f69432734f286174d6883927a03770664fbb39d66c456b58b476af1942e7c8d4d77b5b1651675383df081f523f3184a17bcbbb42996baf733f847136cc5f5e38c2730c9bbbf5f40203ace537c44397fdff2c8f229cbad48d3679070297f498d03bfce498cbb3300305ec99d7963264df988860d57a1f944caa481e2f236a3e6b787c60b819b725fd190203010001";

    // Signature over the §8.1 transcript, PSS / SHA-256 / salt length 32.
    private const string OpenSslSignature =
        "5c360d4a854938d7fb1a8d27e20468779febb7ae13444c019a6847c6bc17229fece851736dc9e8969eaab3151d9da4700974219aecf7930a25dde429b7ea4889c9baeac45bca82a1ebac0fe3b264283415a0bb3b9f534214a2fa3155622b64b57ebba7d7049ca259f9a3ae109262a96b3d691fc7d4facbf708b22fb28290386e98983cf6cbe06b418e73af685e5e97d15a021fd888402730b440a09b1e5277fb1ee61f29c11ae4cee6e15cbc3fd0026e5e1fe9d819c41e845de51c0fffda35feb5ef8d94dc46a1f1e974bfe1b572dc793c89de152d130ad09c0493d9aaae096729ae652263aaba4e906d4446ddb65e4ae9938e55f22f6c570cf14db630e169553bf3d4dede9d2f19296919887c09a90bc1ffcdc8a9e1163914a06f42d50a5efd8bf8ca7574104c38db7d33a0608b6f725c790770463fc77d796683f36410f63ad916d0de7cdd4b75c07713a61d269e1298c43ab31ef71220606183103b0267e2773e72a9b078d17d11c861ecd21ec48887bec71e87ffeead573e33d68875d047";

    private const string TranscriptDigestHex = "779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273";

    [Fact]
    public void OpenSslPssSignature_Verifies()
    {
        byte[] transcript = Section81Transcript();
        Assert.Equal(TranscriptDigestHex, ToHex(SHA256.HashData(transcript)));

        Assert.True(IdentityKey.Verify(Hex(OpenSslSpki), transcript, Hex(OpenSslSignature)));

        // Any change to the signed bytes must fail.
        byte[] tampered = transcript.ToArray();
        tampered[^1] ^= 0x01;
        Assert.False(IdentityKey.Verify(Hex(OpenSslSpki), tampered, Hex(OpenSslSignature)));

        // A different key must not accept the signature.
        using IdentityKey other = IdentityKey.Create();
        Assert.False(IdentityKey.Verify(other.SubjectPublicKeyInfo, transcript, Hex(OpenSslSignature)));
    }

    [Fact]
    public void Sign_RoundTripsAndReportsExpectedSizes()
    {
        using IdentityKey identity = IdentityKey.Create();
        byte[] transcript = Section81Transcript();

        byte[] signature = identity.Sign(transcript);

        Assert.Equal(IdentityKey.SignatureSize, signature.Length);
        Assert.Equal(384, signature.Length);
        Assert.True(IdentityKey.Verify(identity.SubjectPublicKeyInfo, transcript, signature));
        Assert.Equal(IdentityKey.KeySizeInBits, 3072);
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        using IdentityKey identity = IdentityKey.Create();
        byte[] transcript = Section81Transcript();
        byte[] signature = identity.Sign(transcript);
        signature[^1] ^= 0x80;

        Assert.False(IdentityKey.Verify(identity.SubjectPublicKeyInfo, transcript, signature));
        AssertError(ProtocolError.SignatureFailed, () =>
            IdentityKey.VerifyOrThrow(identity.SubjectPublicKeyInfo, transcript, signature));
    }

    [Fact]
    public void Verify_RejectsNonSpkiAndGarbageKeys()
    {
        byte[] transcript = Section81Transcript();
        byte[] signature = Hex(OpenSslSignature);

        Assert.False(IdentityKey.Verify([], transcript, signature));
        Assert.False(IdentityKey.Verify(Hex("00112233"), transcript, signature));
        Assert.False(IdentityKey.Verify(Hex(OpenSslSpki), transcript, []));

        // Truncated SPKI must not be partially accepted.
        byte[] spki = Hex(OpenSslSpki);
        Assert.False(IdentityKey.Verify(spki.AsSpan(0, spki.Length - 1), transcript, signature));
    }

    [Fact]
    public void FromPkcs8_RestoresSameIdentity()
    {
        using IdentityKey identity = IdentityKey.Create();
        byte[] pkcs8 = identity.ExportPkcs8();

        using IdentityKey restored = IdentityKey.FromPkcs8(pkcs8);

        Assert.Equal(ToHex(identity.SubjectPublicKeyInfo), ToHex(restored.SubjectPublicKeyInfo));
        Assert.Equal(identity.Fingerprint, restored.Fingerprint);

        byte[] transcript = Section81Transcript();
        Assert.True(IdentityKey.Verify(restored.SubjectPublicKeyInfo, transcript, identity.Sign(transcript)));
    }

    [Fact]
    public void FromPkcs8_RejectsNonPkcs8Input()
    {
        Assert.ThrowsAny<CryptographicException>(() => IdentityKey.FromPkcs8(Hex("00112233")));
        Assert.ThrowsAny<CryptographicException>(() => IdentityKey.FromPkcs8(Hex(OpenSslSpki)));
    }

    [Fact]
    public void Fingerprint_IsStableAcrossInstances()
    {
        using IdentityKey identity = IdentityKey.Create();
        string fingerprint = identity.Fingerprint;

        Assert.Equal(79, fingerprint.Length); // 64 hex characters + 15 spaces
        Assert.Equal(DeviceFingerprint.HexLength + 15, fingerprint.Length);
        Assert.Equal(fingerprint, DeviceFingerprint.Compute(identity.SubjectPublicKeyInfo));
        Assert.True(DeviceFingerprint.Matches(fingerprint, fingerprint.ToLowerInvariant()));
    }

    /// <summary>Transcript bytes of the §8.1 vector, rebuilt through the production encoder.</summary>
    private static byte[] Section81Transcript()
        => HandshakeTranscript.Create(
            new HandshakeOffer(
                HandshakeOffer.CurrentVersion,
                HandshakeRole.Initiator,
                "Mac-A",
                Hex("aaaaaaaaaaaaaaaa"),
                [.. Enumerable.Repeat((byte)0x11, 32)],
                [.. Enumerable.Repeat((byte)0x22, 32)],
                "123456"),
            new HandshakeOffer(
                HandshakeOffer.CurrentVersion,
                HandshakeRole.Responder,
                "Win-B",
                Hex("bbbbbbbbbbbbbbbb"),
                [.. Enumerable.Repeat((byte)0x33, 32)],
                [.. Enumerable.Repeat((byte)0x44, 32)],
                "123456")).Encode();

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();
}
