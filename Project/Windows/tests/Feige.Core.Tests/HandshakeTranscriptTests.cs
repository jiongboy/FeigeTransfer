using System.Text;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Transcript encoding, SAS and fingerprints from 通信协议 §3.5 / §3.6 / §3.10, checked against the §8.1
/// vectors. The expected values come from the independent Node implementation in
/// <c>scripts/verify-protocol-vectors.js</c>, not from this code base.
/// </summary>
public sealed class HandshakeTranscriptTests
{
    private const string InitiatorOfferEncoding =
        "000101" +
        "00000005" + "4d61632d41" +
        "00000008" + "aaaaaaaaaaaaaaaa" +
        "00000020" + "1111111111111111111111111111111111111111111111111111111111111111" +
        "00000020" + "2222222222222222222222222222222222222222222222222222222222222222" +
        "00000006" + "313233343536";

    private const string ResponderOfferEncoding =
        "000102" +
        "00000005" + "57696e2d42" +
        "00000008" + "bbbbbbbbbbbbbbbb" +
        "00000020" + "3333333333333333333333333333333333333333333333333333333333333333" +
        "00000020" + "4444444444444444444444444444444444444444444444444444444444444444" +
        "00000006" + "313233343536";

    private const string TranscriptDigest = "779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273";
    private const string Sas = "472138";
    private const string InitiatorFingerprint = "6893 A08C 9C4C C97C 2279 9A6A A7AF 6F8B 8BC2 2D54 1A5E 4253 B180 BBC9 74C9 D497";
    private const string ResponderFingerprint = "8C7D 2C6F 7DEF 9A50 D4AD 3023 702D F661 BE86 637F 2854 EFF9 4DE6 0367 6A81 3341";

    [Fact]
    public void EncodeOffer_MatchesDocumentedBytes()
    {
        Assert.Equal(InitiatorOfferEncoding, ToHex(HandshakeTranscript.EncodeOffer(InitiatorOffer())));
        Assert.Equal(ResponderOfferEncoding, ToHex(HandshakeTranscript.EncodeOffer(ResponderOffer())));
    }

    [Fact]
    public void Encode_StartsWithMagicAndBothOffers()
    {
        HandshakeTranscript transcript = Transcript();

        string hex = ToHex(transcript.Encode());

        Assert.StartsWith(ToHex("FEIGE-HANDSHAKE-V1\0"u8), hex);
        Assert.Equal(
            ToHex("FEIGE-HANDSHAKE-V1\0"u8) + InitiatorOfferEncoding + ResponderOfferEncoding,
            hex);
    }

    [Fact]
    public void Digest_And_Sas_MatchSection81Vectors()
    {
        HandshakeTranscript transcript = Transcript();

        Assert.Equal(TranscriptDigest, ToHex(transcript.Digest.Span));
        Assert.Equal(Sas, transcript.Sas);
        Assert.Equal(6, transcript.Sas.Length);
    }

    [Fact]
    public void PeerFingerprint_MatchesSection81Vectors()
    {
        HandshakeTranscript transcript = Transcript();

        Assert.Equal(InitiatorFingerprint, transcript.PeerFingerprint(HandshakeRole.Responder));
        Assert.Equal(ResponderFingerprint, transcript.PeerFingerprint(HandshakeRole.Initiator));
        Assert.Equal(InitiatorFingerprint, DeviceFingerprint.Compute(Hex("aaaaaaaaaaaaaaaa")));
        Assert.Equal(ResponderFingerprint, DeviceFingerprint.Compute(Hex("bbbbbbbbbbbbbbbb")));
    }

    [Fact]
    public void Create_OrdersOffersByRoleRegardlessOfPerspective()
    {
        HandshakeOffer initiator = InitiatorOffer();
        HandshakeOffer responder = ResponderOffer();

        HandshakeTranscript fromInitiator = HandshakeTranscript.Create(initiator, responder);
        HandshakeTranscript fromResponder = HandshakeTranscript.Create(responder, initiator);

        Assert.Equal(HandshakeRole.Initiator, fromInitiator.Initiator.Role);
        Assert.Equal(HandshakeRole.Responder, fromInitiator.Responder.Role);
        Assert.Equal(HandshakeRole.Initiator, fromResponder.Initiator.Role);

        // Both sides must sign and hash the same bytes, otherwise signatures cannot verify.
        Assert.Equal(ToHex(fromInitiator.Encode()), ToHex(fromResponder.Encode()));
        Assert.Equal(ToHex(fromInitiator.Digest.Span), ToHex(fromResponder.Digest.Span));
        Assert.Equal(fromInitiator.Sas, fromResponder.Sas);
    }

    [Fact]
    public void Create_ExposesLocalAndPeerOffersPerRole()
    {
        HandshakeTranscript transcript = Transcript();

        Assert.Equal("Mac-A", transcript.LocalOffer(HandshakeRole.Initiator).DeviceName);
        Assert.Equal("Win-B", transcript.PeerOffer(HandshakeRole.Initiator).DeviceName);
        Assert.Equal("Win-B", transcript.LocalOffer(HandshakeRole.Responder).DeviceName);
        Assert.Equal("Mac-A", transcript.PeerOffer(HandshakeRole.Responder).DeviceName);
    }

    [Fact]
    public void Digest_FeedsSessionKeysThatMirrorBetweenRoles()
    {
        HandshakeTranscript transcript = Transcript();

        SessionKeys initiatorKeys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            transcript.Digest.Span,
            HandshakeRole.Initiator);
        SessionKeys responderKeys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            transcript.Digest.Span,
            HandshakeRole.Responder);

        // §3.9: the initiator's send key must be the responder's receive key, and the prefixes must pair up.
        Assert.Equal(ToHex(initiatorKeys.SendKey), ToHex(responderKeys.ReceiveKey));
        Assert.Equal(ToHex(initiatorKeys.SendNoncePrefix), ToHex(responderKeys.ReceiveNoncePrefix));
        Assert.Equal("779855ca494e4954", ToHex(initiatorKeys.SendNoncePrefix));
        Assert.Equal("779855ca52455350", ToHex(initiatorKeys.ReceiveNoncePrefix));
    }

    [Theory]
    [InlineData(HandshakeRole.Initiator)]
    [InlineData(HandshakeRole.Responder)]
    public void Create_RejectsIdenticalRoles(HandshakeRole role)
    {
        HandshakeOffer first = Offer(role, "same", Hex("cccccccccccccccc"));
        HandshakeOffer second = Offer(role, "other", Hex("dddddddddddddddd"));

        AssertError(ProtocolError.InvalidTranscript, () => HandshakeTranscript.Create(first, second));
    }

    [Fact]
    public void Create_RejectsPairingWithOwnIdentity()
    {
        byte[] identity = Hex("eeeeeeeeeeeeeeee");
        HandshakeOffer local = Offer(HandshakeRole.Initiator, "self", identity);
        HandshakeOffer peer = Offer(HandshakeRole.Responder, "self", identity);

        AssertError(ProtocolError.InvalidTranscript, () => HandshakeTranscript.Create(local, peer));
    }

    [Fact]
    public void Create_RejectsDifferentSessionCode()
    {
        HandshakeOffer local = Offer(HandshakeRole.Initiator, "Mac-A", Hex("aaaaaaaaaaaaaaaa"), sessionCode: "123456");
        HandshakeOffer peer = Offer(HandshakeRole.Responder, "Win-B", Hex("bbbbbbbbbbbbbbbb"), sessionCode: "654321");

        AssertError(ProtocolError.PairingCodeMismatch, () => HandshakeTranscript.Create(local, peer));
    }

    [Fact]
    public void Offer_EqualityIsStructural()
    {
        HandshakeOffer first = InitiatorOffer();
        HandshakeOffer same = InitiatorOffer();
        HandshakeOffer differentNonce = new(
            HandshakeOffer.CurrentVersion,
            HandshakeRole.Initiator,
            "Mac-A",
            Hex("aaaaaaaaaaaaaaaa"),
            [.. Enumerable.Repeat((byte)0x11, 32)],
            [.. Enumerable.Repeat((byte)0x23, 32)],
            "123456");

        Assert.Equal(first, same);
        Assert.True(first == same);
        Assert.NotEqual(first, differentNonce);
        Assert.True(first != differentNonce);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void OfferValidation_RejectsOutOfRangeFields()
    {
        byte[] identity = Hex("aaaaaaaaaaaaaaaa");
        byte[] ephemeral = [.. Enumerable.Repeat((byte)0x11, 32)];
        byte[] nonce = [.. Enumerable.Repeat((byte)0x22, 32)];

        // version must be exactly 1
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(2, HandshakeRole.Initiator, "x", identity, ephemeral, nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(0, HandshakeRole.Initiator, "x", identity, ephemeral, nonce, "123456"));

        // role must be 1 or 2
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, (HandshakeRole)3, "x", identity, ephemeral, nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, (HandshakeRole)0, "x", identity, ephemeral, nonce, "123456"));

        // deviceName: non-empty, at most 128 UTF-8 bytes
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "", identity, ephemeral, nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, new string('a', 129), identity, ephemeral, nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, new string('飞', 43), identity, ephemeral, nonce, "123456"));

        // identity DER: 1..1024 bytes
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", [], ephemeral, nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", new byte[1025], ephemeral, nonce, "123456"));

        // ephemeral key and nonce are fixed size
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", identity, new byte[31], nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", identity, new byte[33], nonce, "123456"));
        AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", identity, ephemeral, new byte[31], "123456"));

        // sessionCode: exactly six ASCII digits
        foreach (string code in new[] { "12345", "1234567", "12345a", "12 456", "１２３４５６" })
        {
            AssertError(ProtocolError.InvalidOffer, () => new HandshakeOffer(1, HandshakeRole.Initiator, "x", identity, ephemeral, nonce, code));
        }

        // 128 ASCII bytes and a 42-character CJK name (126 bytes) are still legal
        _ = new HandshakeOffer(1, HandshakeRole.Initiator, new string('a', 128), identity, ephemeral, nonce, "123456");
        _ = new HandshakeOffer(1, HandshakeRole.Initiator, new string('飞', 42), identity, ephemeral, nonce, "123456");
    }

    [Fact]
    public void CreateSessionCode_AlwaysSixDigits()
    {
        for (int i = 0; i < 200; i++)
        {
            string code = HandshakeOffer.CreateSessionCode();
            Assert.Equal(6, code.Length);
            Assert.True(HandshakeOffer.IsValidSessionCode(code));
        }
    }

    [Fact]
    public void Fingerprint_NormalizationIgnoresSpacingAndCase()
    {
        Assert.Equal("6893A08C9C4CC97C22799A6AA7AF6F8B8BC22D541A5E4253B180BBC974C9D497", DeviceFingerprint.Normalize(InitiatorFingerprint));
        Assert.True(DeviceFingerprint.Matches(InitiatorFingerprint, InitiatorFingerprint.ToLowerInvariant()));
        Assert.True(DeviceFingerprint.Matches(InitiatorFingerprint, InitiatorFingerprint.Replace(" ", "   ")));
        Assert.False(DeviceFingerprint.Matches(InitiatorFingerprint, ResponderFingerprint));
        Assert.False(DeviceFingerprint.Matches(InitiatorFingerprint, "not a fingerprint"));
        Assert.False(DeviceFingerprint.Matches(null, null));
        Assert.Equal(string.Empty, DeviceFingerprint.Normalize("zz"));
    }

    [Fact]
    public void Fingerprint_VerifyThrowsOnMismatch()
    {
        byte[] identity = Hex("aaaaaaaaaaaaaaaa");

        DeviceFingerprint.Verify(identity, InitiatorFingerprint);
        AssertError(ProtocolError.FingerprintMismatch, () => DeviceFingerprint.Verify(identity, ResponderFingerprint));
    }

    private static HandshakeOffer InitiatorOffer() => Offer(HandshakeRole.Initiator, "Mac-A", Hex("aaaaaaaaaaaaaaaa"),
        ephemeral: Fill(0x11), nonce: Fill(0x22));

    private static HandshakeOffer ResponderOffer() => Offer(HandshakeRole.Responder, "Win-B", Hex("bbbbbbbbbbbbbbbb"),
        ephemeral: Fill(0x33), nonce: Fill(0x44));

    private static HandshakeTranscript Transcript() => HandshakeTranscript.Create(InitiatorOffer(), ResponderOffer());

    private static HandshakeOffer Offer(
        HandshakeRole role,
        string deviceName,
        byte[] identity,
        byte[]? ephemeral = null,
        byte[]? nonce = null,
        string sessionCode = "123456")
        => new(
            HandshakeOffer.CurrentVersion,
            role,
            deviceName,
            identity,
            ephemeral ?? Fill(0x11),
            nonce ?? Fill(0x22),
            sessionCode);

    private static byte[] Fill(byte value) => [.. Enumerable.Repeat(value, 32)];

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();
}
