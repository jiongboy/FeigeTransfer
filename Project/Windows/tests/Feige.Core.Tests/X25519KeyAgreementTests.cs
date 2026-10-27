using System.Security.Cryptography;
using Feige.Core.Crypto;

namespace Feige.Core.Tests;

/// <summary>
/// Vectors from RFC 7748, plus the failure modes 通信协议 §3.9 requires.
/// </summary>
public sealed class X25519KeyAgreementTests
{
    // RFC 7748 A.5: X25519(k, u) -> output
    [Theory]
    [InlineData(
        "a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4",
        "e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c",
        "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")]
    [InlineData(
        "4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d",
        "e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493",
        "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957")]
    public void ScalarMultiplication_MatchesRfc7748(string scalarHex, string peerHex, string expectedSharedHex)
    {
        byte[] shared = X25519KeyAgreement.Agree(Hex(scalarHex), Hex(peerHex));

        Assert.Equal(expectedSharedHex, ToHex(shared));
    }

    // RFC 7748 6.1: alice/bob key pairs and the shared secret
    private const string AlicePrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
    private const string AlicePublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
    private const string BobPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
    private const string BobPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
    private const string RfcShared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

    [Fact]
    public void GetPublicKey_MatchesRfc7748()
    {
        Assert.Equal(AlicePublic, ToHex(X25519KeyAgreement.GetPublicKey(Hex(AlicePrivate))));
        Assert.Equal(BobPublic, ToHex(X25519KeyAgreement.GetPublicKey(Hex(BobPrivate))));
    }

    [Fact]
    public void Agree_IsSymmetricAndMatchesRfc7748()
    {
        byte[] aliceSees = X25519KeyAgreement.Agree(Hex(AlicePrivate), Hex(BobPublic));
        byte[] bobSees = X25519KeyAgreement.Agree(Hex(BobPrivate), Hex(AlicePublic));

        Assert.Equal(RfcShared, ToHex(aliceSees));
        Assert.Equal(RfcShared, ToHex(bobSees));
    }

    [Fact]
    public void Agree_RejectsSmallOrderPeerKey()
    {
        // An all-zero public key is the canonical small-order point: the shared secret would be all zeros.
        byte[] zeroPublicKey = new byte[X25519KeyAgreement.KeySize];

        Assert.Throws<CryptographicException>(() => X25519KeyAgreement.Agree(Hex(AlicePrivate), zeroPublicKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Agree_RejectsWrongKeyLength(int length)
    {
        byte[] key = new byte[length];

        Assert.Throws<ArgumentException>(() => X25519KeyAgreement.Agree(Hex(AlicePrivate), key));
        Assert.Throws<ArgumentException>(() => X25519KeyAgreement.GetPublicKey(key));
    }

    [Fact]
    public void CreatePrivateKey_ProducesDistinctKeys()
    {
        byte[] first = X25519KeyAgreement.CreatePrivateKey();
        byte[] second = X25519KeyAgreement.CreatePrivateKey();

        Assert.Equal(X25519KeyAgreement.KeySize, first.Length);
        Assert.NotEqual(ToHex(first), ToHex(second));
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
