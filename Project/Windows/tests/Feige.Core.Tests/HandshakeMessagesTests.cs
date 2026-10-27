using System.Text;
using System.Text.Json;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Offer/proof JSON codec and the <c>u32</c> handshake framing (通信协议 §3.1, §3.3, §3.7).
/// The wire shape has to match Swift's <c>JSONEncoder</c>: integers as JSON numbers, <c>Data</c> as Base64 strings.
/// </summary>
public sealed class HandshakeMessagesTests
{
    [Fact]
    public void OfferFrame_RoundTrips()
    {
        HandshakeOffer original = Offer();

        byte[] frame = HandshakeMessages.EncodeOfferFrame(original);

        // length prefix counts the JSON payload only
        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame);
        Assert.Equal((uint)(frame.Length - 4), declared);

        HandshakeOffer decoded = HandshakeMessages.DecodeOfferFrame(frame);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void OfferJson_UsesProtocolFieldNamesAndBase64()
    {
        HandshakeOffer offer = Offer();

        using JsonDocument document = JsonDocument.Parse(HandshakeMessages.EncodeOfferJson(offer));
        JsonElement root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        JsonElement body = root.GetProperty("offer");
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal(1, body.GetProperty("role").GetInt32());
        Assert.Equal("Win-B", body.GetProperty("deviceName").GetString());
        Assert.Equal("123456", body.GetProperty("sessionCode").GetString());
        Assert.Equal(Convert.ToBase64String(Offer().IdentityPublicKeyDer.ToArray()), body.GetProperty("identityPublicKeyDER").GetString());
        Assert.Equal(Convert.ToBase64String(Offer().EphemeralPublicKey.ToArray()), body.GetProperty("ephemeralPublicKey").GetString());

        // No unexpected top-level keys.
        Assert.Single(root.EnumerateObject());
    }

    [Fact]
    public void ProofFrame_RoundTripsSignatureAndOffer()
    {
        HandshakeOffer offer = Offer();
        byte[] signature = new byte[IdentityKey.SignatureSize];
        Random.Shared.NextBytes(signature);

        byte[] frame = HandshakeMessages.EncodeProofFrame(offer, signature);
        HandshakeProof proof = HandshakeMessages.DecodeProofFrame(frame);

        Assert.Equal(offer, proof.Offer);
        Assert.Equal(signature, proof.Signature);
    }

    [Fact]
    public void DecodeOffer_IgnoresUnknownFields()
    {
        string json = """
            {"offer":{"version":1,"role":1,"deviceName":"Win-B","identityPublicKeyDER":"AAECAwQ=","ephemeralPublicKey":"ERERERERERERERERERERERERERERERERERERERERERE=","nonce":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=","sessionCode":"123456","futureField":true},"extra":1}
            """;

        HandshakeOffer offer = HandshakeMessages.DecodeOffer(Encoding.UTF8.GetBytes(json));

        Assert.Equal("Win-B", offer.DeviceName);
    }

    [Theory]
    // not an object / wrong wrapper
    [InlineData("[]")]
    [InlineData("\"offer\"")]
    [InlineData("{}")]
    [InlineData("{\"offer\":{}}")]
    // missing fields
    [InlineData("{\"offer\":{\"version\":1,\"role\":1,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"sessionCode\":\"123456\"}}")]
    // wrong JSON types
    [InlineData("{\"offer\":{\"version\":1,\"role\":\"1\",\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    [InlineData("{\"offer\":{\"version\":\"1\",\"role\":1,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    [InlineData("{\"offer\":{\"version\":1,\"role\":1,\"deviceName\":null,\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    // invalid base64
    [InlineData("{\"offer\":{\"version\":1,\"role\":1,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"not base64!\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    // field constraints from §3.3
    [InlineData("{\"offer\":{\"version\":2,\"role\":1,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    [InlineData("{\"offer\":{\"version\":1,\"role\":3,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    [InlineData("{\"offer\":{\"version\":1,\"role\":1,\"deviceName\":\"\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"123456\"}}")]
    [InlineData("{\"offer\":{\"version\":1,\"role\":1,\"deviceName\":\"x\",\"identityPublicKeyDER\":\"AAECAwQ=\",\"ephemeralPublicKey\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\"nonce\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\"sessionCode\":\"12345\"}}")]
    public void DecodeOffer_RejectsMalformedInput(string json)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(() => HandshakeMessages.DecodeOffer(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(error.Error, new[] { ProtocolError.InvalidOffer, ProtocolError.InvalidHandshakeFrame });
    }

    [Fact]
    public void Decode_RejectsInvalidJsonAndEmptyPayload()
    {
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.DecodeOffer([]));
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.DecodeOffer("{\"offer\":"u8));
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.DecodeOffer("{\"offer\":{}} trailing"u8));
    }

    [Fact]
    public void DecodeProof_RejectsWrongSignatureLength()
    {
        HandshakeOffer offer = Offer();
        byte[] json = HandshakeMessages.EncodeProofJson(offer, new byte[IdentityKey.SignatureSize]);

        // Shrink the Base64 signature by re-encoding a short one.
        string text = Encoding.UTF8.GetString(json).Replace(
            Convert.ToBase64String(new byte[IdentityKey.SignatureSize]),
            Convert.ToBase64String(new byte[10]),
            StringComparison.Ordinal);

        AssertError(ProtocolError.SignatureFailed, () => HandshakeMessages.DecodeProof(Encoding.UTF8.GetBytes(text)));

        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.DecodeProof(HandshakeMessages.EncodeOfferJson(offer)));
    }

    [Theory]
    [InlineData("00000000")]          // zero length
    [InlineData("00000003")]          // shorter than the body
    [InlineData("00004001")]          // one byte above 16 KiB
    [InlineData("7fffffff")]
    public void Unframe_RejectsImpossibleLengths(string frameHex)
    {
        byte[] frame = [.. Convert.FromHexString(frameHex), .. new byte[8]];
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.Unframe(frame));
    }

    [Fact]
    public void Unframe_RejectsTrailingBytes()
    {
        byte[] frame = HandshakeMessages.EncodeOfferJson(Offer());
        byte[] padded = [.. frame, 0x00];

        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.Unframe(padded));
    }

    [Fact]
    public void Frame_RejectsOversizedPayload()
    {
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.Frame(new byte[HandshakeMessages.MaxJsonBytes + 1]));
        AssertError(ProtocolError.InvalidHandshakeFrame, () => HandshakeMessages.Frame([]));
        Assert.Equal(HandshakeMessages.MaxJsonBytes, 16384);
    }

    [Fact]
    public void Reader_ReassemblesMessagesFromArbitraryFragments()
    {
        var reader = new HandshakeFrameReader();
        byte[] first = HandshakeMessages.EncodeOfferFrame(Offer());
        byte[] second = HandshakeMessages.EncodeOfferFrame(Offer(role: HandshakeRole.Responder));

        var payloads = new List<byte[]>();
        foreach (byte value in first)
        {
            payloads.AddRange(reader.Append([value]));
        }

        Assert.Single(payloads);
        Assert.Equal(Offer(), HandshakeMessages.DecodeOffer(payloads[0]));

        // Two frames in one read.
        payloads = [.. reader.Append([.. first, .. second])];
        Assert.Equal(2, payloads.Count);
        Assert.Equal(HandshakeRole.Responder, HandshakeMessages.DecodeOffer(payloads[1]).Role);
        Assert.Equal(0, reader.PendingByteCount);
    }

    [Fact]
    public void Reader_RejectsBadLengthPrefixImmediately()
    {
        var reader = new HandshakeFrameReader();

        AssertError(ProtocolError.InvalidHandshakeFrame, () => reader.Append([0x00, 0x00, 0x00, 0x00]));
        AssertError(ProtocolError.InvalidHandshakeFrame, () => reader.Append([0x00, 0x00, 0x40, 0x01]));
    }

    [Fact]
    public void Reader_KeepsIncompleteHeaderBuffered()
    {
        var reader = new HandshakeFrameReader();
        byte[] frame = HandshakeMessages.EncodeOfferFrame(Offer());

        Assert.Empty(reader.Append(frame.AsSpan(0, 3)));
        Assert.Equal(3, reader.PendingByteCount);

        // 6 bytes: length prefix complete, body still partial.
        Assert.Empty(reader.Append(frame.AsSpan(3, 3)));
        Assert.Equal(6, reader.PendingByteCount);

        Assert.Empty(reader.Append([]));
        Assert.Single(reader.Append(frame.AsSpan(6)));
        Assert.Equal(0, reader.PendingByteCount);
    }

    private static HandshakeOffer Offer(HandshakeRole role = HandshakeRole.Initiator) => new(
        HandshakeOffer.CurrentVersion,
        role,
        "Win-B",
        [0xAA, 0xBB, 0xCC, 0xDD],
        [.. Enumerable.Repeat((byte)0x11, 32)],
        [.. Enumerable.Repeat((byte)0x22, 32)],
        "123456");

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }
}
