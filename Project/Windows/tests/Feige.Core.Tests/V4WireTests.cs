using System.Security.Cryptography;
using System.Text;
using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class V4WireTests
{
    [Fact]
    public void SharedHandshakeVector_MatchesTranscriptDigestAndDirectionalKeys()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4-handshake.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        V4Offer initiator = ReadOffer(root.GetProperty("initiatorOffer"));
        V4Offer responder = ReadOffer(root.GetProperty("responderOffer"));
        byte[] transcript = V4Wire.Transcript(initiator, responder);
        byte[] digest = SHA256.HashData(transcript);

        Assert.Equal(Hex(root.GetProperty("transcriptHex").GetString()!), transcript);
        Assert.Equal(transcript, V4Wire.Transcript(responder, initiator));
        Assert.Equal(Hex(root.GetProperty("transcriptSha256Hex").GetString()!), digest);
        byte[] shared = Hex(root.GetProperty("sharedSecretHex").GetString()!);
        byte[] i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v4 initiator-to-responder"u8.ToArray());
        byte[] r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v4 responder-to-initiator"u8.ToArray());
        Assert.Equal(Hex(root.GetProperty("initiatorToResponderKeyHex").GetString()!), i2r);
        Assert.Equal(Hex(root.GetProperty("responderToInitiatorKeyHex").GetString()!), r2i);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void SharedV5AndV6HandshakeVectors_MatchTranscriptDigestAndDirectionalKeys(ushort version)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"v{version}-handshake.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Assert.Equal($"feige-v{version}-handshake-primitives", root.GetProperty("format").GetString());
        Assert.Equal(version, root.GetProperty("version").GetUInt16());
        V4Offer initiator = ReadOffer(root.GetProperty("initiatorOffer"));
        V4Offer responder = ReadOffer(root.GetProperty("responderOffer"));
        byte[] transcript = V4Wire.Transcript(initiator, responder);
        byte[] digest = SHA256.HashData(transcript);

        Assert.Equal(Hex(root.GetProperty("transcriptHex").GetString()!), transcript);
        Assert.Equal(transcript, V4Wire.Transcript(responder, initiator));
        Assert.Equal(Hex(root.GetProperty("transcriptSha256Hex").GetString()!), digest);
        byte[] shared = Bytes(root.GetProperty("sharedSecretBytes"));
        byte[] i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest,
            Encoding.UTF8.GetBytes(root.GetProperty("hkdfInfoInitiatorToResponder").GetString()!));
        byte[] r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest,
            Encoding.UTF8.GetBytes(root.GetProperty("hkdfInfoResponderToInitiator").GetString()!));
        Assert.Equal(Bytes(root.GetProperty("initiatorToResponderKeyBytes")), i2r);
        Assert.Equal(Bytes(root.GetProperty("responderToInitiatorKeyBytes")), r2i);
    }

    [Fact]
    public void Transcript_IsDomainSeparatedAndRoleOrdered()
    {
        var initiator = Offer(1, "Mac-A", [1, 2, 3]);
        var responder = Offer(2, "Win-B", [4, 5, 6]);

        byte[] forward = V4Wire.Transcript(initiator, responder);
        byte[] reverse = V4Wire.Transcript(responder, initiator);
        byte[] expectedPrefix = Encoding.UTF8.GetBytes("FEIGE-HANDSHAKE-V4").Concat(new byte[] { 0 }).ToArray();

        Assert.Equal(expectedPrefix, forward[..19]);
        Assert.Equal(forward, reverse);
        Assert.NotEqual(Encoding.UTF8.GetBytes("FEIGE-HANDSHAKE-V3"), forward[..18]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    public void RecordCipher_RoundTripsDefinedV4Types(byte type)
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] digest = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        using var sender = new V4RecordCipher(key, digest);
        using var receiver = new V4RecordCipher(key, digest);
        byte[] payload = type == 7 ? [] : Encoding.UTF8.GetBytes("v4 payload");

        byte[] frame = sender.Seal(type, payload);
        var opened = receiver.Open(frame);

        Assert.Equal(type, opened.Type);
        Assert.Equal(payload, opened.Payload);
    }

    [Theory]
    [InlineData(0)] [InlineData(17)] [InlineData(255)]
    public void RecordCipher_RejectsUndefinedTypes(byte type)
    {
        using var cipher = new V4RecordCipher(new byte[32], new byte[32]);
        Assert.Throws<InvalidDataException>(() => cipher.Seal(type, [1]));
    }

    [Fact]
    public void RecordCipher_RejectsReplaySkipAndTamperingWithoutAdvancingOnBadTag()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] digest = RandomNumberGenerator.GetBytes(32);
        using var sender = new V4RecordCipher(key, digest);
        using var receiver = new V4RecordCipher(key, digest);
        byte[] first = sender.Seal(12, [1, 2, 3]);
        byte[] second = sender.Seal(12, [4, 5, 6]);

        Assert.Throws<InvalidDataException>(() => receiver.Open(second));
        byte[] badTag = first.ToArray(); badTag[^1] ^= 0x80;
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(badTag));
        Assert.Equal(new byte[] { 1, 2, 3 }, receiver.Open(first).Payload);
        Assert.Throws<InvalidDataException>(() => receiver.Open(first));
        Assert.Equal(new byte[] { 4, 5, 6 }, receiver.Open(second).Payload);
    }

    [Fact]
    public void Offer_RejectsVersionsOtherThanExactlyFourAndUnsupportedFields()
    {
        var offer = Offer(1, "Mac-A", [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => (offer with { Version = 3 }).Validate());
        Assert.Throws<InvalidDataException>(() => (offer with { SessionCode = "123456" }).Validate());
        Assert.Throws<InvalidDataException>(() => V4Wire.Transcript(offer, offer with { Role = 1 }));
    }

    private static V4Offer ReadOffer(System.Text.Json.JsonElement value)
    {
        var identity = value.GetProperty("identityPublicKeyDERHex").GetString()!;
        var ephemeral = value.GetProperty("ephemeralPublicKeyHex").GetString()!;
        var nonce = value.GetProperty("nonceHex").GetString()!;
        return new V4Offer(value.GetProperty("version").GetUInt16(), value.GetProperty("role").GetByte(),
            value.GetProperty("deviceName").GetString()!, Hex(identity), Hex(ephemeral), Hex(nonce),
            value.GetProperty("sessionCode").GetString()!);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static byte[] Bytes(System.Text.Json.JsonElement value)
        => value.EnumerateArray().Select(item => item.GetByte()).ToArray();

    private static V4Offer Offer(byte role, string name, byte[] identity)
    {
        byte[] ephemeral = new byte[32]; ephemeral[0] = role;
        byte[] nonce = new byte[32]; nonce[0] = (byte)(role + 10);
        return new V4Offer(4, role, name, identity, ephemeral, nonce, "");
    }
}
