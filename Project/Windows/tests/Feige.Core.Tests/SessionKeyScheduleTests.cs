using System.Security.Cryptography;
using System.Text;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Key schedule from 通信协议 §3.9 / §4.1.
/// The expected values were produced by an independent Node implementation (crypto.hkdfSync over
/// crypto.diffieHellman) for the fixed inputs below, so they do not come from this code base.
/// </summary>
public sealed class SessionKeyScheduleTests
{
    // Fixed inputs: local private key = 0x55 x32, peer public key = 0x11 x32,
    // transcript digest = §8.1 digest.
    private const string SharedSecret = "0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d";
    private const string TranscriptDigest = "779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273";
    private const string InitiatorToResponder = "a296f49124c77fbbb8d2027ed9ca4f67c072f2c070568094677a3239cb29652b";
    private const string ResponderToInitiator = "3fb7cc3be6a343871d8aaba9011c330862c5ab4f2b010651d20cbde2161de217";
    private const string InitiatorNoncePrefix = "779855ca494e4954";
    private const string ResponderNoncePrefix = "779855ca52455350";

    [Fact]
    public void Derive_Initiator_UsesI2rForSending()
    {
        SessionKeys keys = SessionKeySchedule.Derive(Hex(SharedSecret), Hex(TranscriptDigest), HandshakeRole.Initiator);

        Assert.Equal(InitiatorToResponder, ToHex(keys.SendKey));
        Assert.Equal(ResponderToInitiator, ToHex(keys.ReceiveKey));
        Assert.Equal(InitiatorNoncePrefix, ToHex(keys.SendNoncePrefix));
        Assert.Equal(ResponderNoncePrefix, ToHex(keys.ReceiveNoncePrefix));
    }

    [Fact]
    public void Derive_Responder_UsesR2iForSending()
    {
        SessionKeys keys = SessionKeySchedule.Derive(Hex(SharedSecret), Hex(TranscriptDigest), HandshakeRole.Responder);

        Assert.Equal(ResponderToInitiator, ToHex(keys.SendKey));
        Assert.Equal(InitiatorToResponder, ToHex(keys.ReceiveKey));
        Assert.Equal(ResponderNoncePrefix, ToHex(keys.SendNoncePrefix));
        Assert.Equal(InitiatorNoncePrefix, ToHex(keys.ReceiveNoncePrefix));
    }

    [Fact]
    public void Derive_BothSidesAgreeOnDirections()
    {
        byte[] privateKeyA = X25519KeyAgreement.CreatePrivateKey();
        byte[] privateKeyB = X25519KeyAgreement.CreatePrivateKey();
        byte[] publicKeyA = X25519KeyAgreement.GetPublicKey(privateKeyA);
        byte[] publicKeyB = X25519KeyAgreement.GetPublicKey(privateKeyB);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("feige-session-key-test"));

        byte[] sharedA = X25519KeyAgreement.Agree(privateKeyA, publicKeyB);
        byte[] sharedB = X25519KeyAgreement.Agree(privateKeyB, publicKeyA);
        Assert.Equal(ToHex(sharedA), ToHex(sharedB));

        SessionKeys a = SessionKeySchedule.Derive(sharedA, digest, HandshakeRole.Initiator);
        SessionKeys b = SessionKeySchedule.Derive(sharedB, digest, HandshakeRole.Responder);

        Assert.Equal(ToHex(a.SendKey), ToHex(b.ReceiveKey));
        Assert.Equal(ToHex(a.ReceiveKey), ToHex(b.SendKey));
        Assert.Equal(ToHex(a.SendNoncePrefix), ToHex(b.ReceiveNoncePrefix));
        Assert.Equal(ToHex(a.ReceiveNoncePrefix), ToHex(b.SendNoncePrefix));
        Assert.NotEqual(ToHex(a.SendKey), ToHex(a.ReceiveKey));
    }

    [Fact]
    public void BuildNonce_Is16BytesPrefixPlusBigEndianSequence()
    {
        byte[] prefix = Hex(InitiatorNoncePrefix);

        Assert.Equal(16, SessionKeySchedule.NonceSize);
        Assert.Equal("779855ca494e49540000000000000000", ToHex(SessionKeySchedule.BuildNonce(prefix, 0)));
        Assert.Equal("779855ca494e49540102030405060708", ToHex(SessionKeySchedule.BuildNonce(prefix, 0x0102030405060708)));
        Assert.Equal("779855ca494e4954ffffffffffffffff", ToHex(SessionKeySchedule.BuildNonce(prefix, ulong.MaxValue)));
    }

    [Fact]
    public void BuildNonce_RejectsWrongPrefixLength()
    {
        Assert.Throws<ArgumentException>(() => SessionKeySchedule.BuildNonce(new byte[12], 0));
    }

    [Fact]
    public void Derive_RejectsWrongDigestLength()
    {
        Assert.Throws<ArgumentException>(() =>
            SessionKeySchedule.Derive(Hex(SharedSecret), new byte[31], HandshakeRole.Initiator));
    }

    [Fact]
    public void Derive_RejectsZeroSharedSecret()
    {
        Assert.Throws<CryptographicException>(() =>
            SessionKeySchedule.Derive(new byte[32], Hex(TranscriptDigest), HandshakeRole.Initiator));
    }

    [Fact]
    public void Derive_RejectsUnknownRole()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionKeySchedule.Derive(Hex(SharedSecret), Hex(TranscriptDigest), (HandshakeRole)3));
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
