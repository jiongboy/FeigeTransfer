using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Record layer from 通信协议 §4.1 / §4.2 / §8.2.
/// <para>
/// The fixed vectors below were produced by an independent Node implementation
/// (<c>scripts/verify-protocol-vectors.js</c> uses the same AES-256-GCM construction), so an error in
/// this code base cannot make the expectation agree with the bug.
/// </para>
/// </summary>
public sealed class RecordCipherTests
{
    private const string VectorKey = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string VectorPrefix = "779855ca494e4954";

    // §8.2 frame without the outer u32 length prefix.
    private const string ManifestFrame =
        "01000000000000000000000021" +
        "02eff278f8692682ae1bec1a6c54adc0da" +
        "f7e2e9d4e1b6bfbac728898115a6db64";

    // seq=1, type=3 (chunk), payload 0x00..0x1f
    private const string ChunkFrame =
        "03000000000000000100000030" +
        "d28326309f5a2dc13c837b11a21cdf3ea86263a9c58cc4f0467f4db33f722945" +
        "47b6443017390c042da902933d1e4f52";

    // seq=2, type=7 (finish), empty payload
    private const string FinishFrame =
        "07000000000000000200000010" +
        "bcc19843d8716dbafb740136ca263574";

    private static readonly byte[] ChunkPayload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Seal_ProducesDocumentedSection82Vector()
    {
        var cipher = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));

        byte[] frame = cipher.Seal(TransferMessageType.Manifest, RecordPlaintext);

        Assert.Equal(ManifestFrame, ToHex(frame));
        Assert.Equal(RecordCipher.HeaderSize, 13);
        Assert.Equal(RecordCipher.MaxFrameSize, 65565);
    }

    [Fact]
    public void Seal_AdvancesSequenceAndMatchesIndependentFrames()
    {
        var cipher = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));

        byte[] first = cipher.Seal(TransferMessageType.Manifest, RecordPlaintext);
        byte[] second = cipher.Seal(TransferMessageType.Chunk, ChunkPayload);
        byte[] third = cipher.Seal(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty);

        Assert.Equal(ManifestFrame, ToHex(first));
        Assert.Equal(ChunkFrame, ToHex(second));
        Assert.Equal(FinishFrame, ToHex(third));
        Assert.Equal(3UL, cipher.NextSequence);
    }

    [Fact]
    public void Open_RoundTripsSealedFramesInOrder()
    {
        var sender = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));

        byte[] manifest = sender.Seal(TransferMessageType.Manifest, RecordPlaintext);
        byte[] chunk = sender.Seal(TransferMessageType.Chunk, ChunkPayload);
        byte[] finish = sender.Seal(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty);

        OpenedRecord first = receiver.Open(manifest);
        Assert.Equal(TransferMessageType.Manifest, first.Type);
        Assert.Equal(0UL, first.Sequence);
        Assert.Equal("feige-record-test", System.Text.Encoding.UTF8.GetString(first.Payload));

        OpenedRecord second = receiver.Open(chunk);
        Assert.Equal(TransferMessageType.Chunk, second.Type);
        Assert.Equal(1UL, second.Sequence);
        Assert.Equal(ToHex(ChunkPayload), ToHex(second.Payload));

        OpenedRecord third = receiver.Open(finish);
        Assert.Equal(TransferMessageType.Finish, third.Type);
        Assert.Equal(2UL, third.Sequence);
        Assert.Empty(third.Payload);
    }

    [Fact]
    public void Open_RejectsReplayedRecord()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame);

        receiver.Open(frame);

        AssertError(ProtocolError.UnexpectedSequence, () => receiver.Open(frame));
    }

    [Fact]
    public void Open_RejectsSkippedSequence()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));

        // Expects sequence 0, receives 1.
        AssertError(ProtocolError.UnexpectedSequence, () => receiver.Open(Hex(ChunkFrame)));
    }

    [Fact]
    public void Open_RejectsTamperedTypeByte()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame);
        frame[0] = (byte)TransferMessageType.Decision; // still a defined type, so GCM must catch it

        AssertError(ProtocolError.AuthenticationFailed, () => receiver.Open(frame));
    }

    [Fact]
    public void Open_RejectsTamperedSequenceCoveredByAad()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        receiver.Open(Hex(ManifestFrame)); // now expects sequence 1

        byte[] frame = Hex(ManifestFrame);
        frame[8] = 0x01; // header now claims sequence 1, matching what the receiver expects

        AssertError(ProtocolError.AuthenticationFailed, () => receiver.Open(frame));
    }

    [Fact]
    public void Open_RejectsTamperedCiphertext()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame);
        frame[RecordCipher.HeaderSize + 2] ^= 0x01;

        AssertError(ProtocolError.AuthenticationFailed, () => receiver.Open(frame));
    }

    [Fact]
    public void Open_RejectsTruncatedTag()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame)[..^1];

        AssertError(ProtocolError.InvalidLength, () => receiver.Open(frame));
    }

    [Fact]
    public void Open_RejectsInconsistentCiphertextLength()
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame);
        frame[12] = 0x20; // ctLen 0x21 -> 0x20 no longer matches the frame size

        AssertError(ProtocolError.InvalidLength, () => receiver.Open(frame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(255)]
    public void Open_RejectsUndefinedType(int type)
    {
        var receiver = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] frame = Hex(ManifestFrame);
        frame[0] = (byte)type;

        AssertError(ProtocolError.InvalidType, () => receiver.Open(frame));
    }

    [Fact]
    public void Seal_RejectsEmptyPayloadForNonFinishType()
    {
        var cipher = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));

        AssertError(ProtocolError.InvalidLength, () => cipher.Seal(TransferMessageType.Decision, ReadOnlySpan<byte>.Empty));
        AssertError(ProtocolError.InvalidLength, () => cipher.Seal(TransferMessageType.Manifest, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Seal_AllowsMaximumPayloadAndRejectsOneByteMore()
    {
        var cipher = new RecordCipher(Hex(VectorKey), Hex(VectorPrefix));
        byte[] maximum = new byte[RecordCipher.MaxPlaintextSize];

        byte[] frame = cipher.Seal(TransferMessageType.Chunk, maximum);

        Assert.Equal(RecordCipher.MaxFrameSize, frame.Length);

        // ctLen is plaintext + tag, written big-endian at offset 9 (§4.1).
        uint ciphertextLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9, 4));
        Assert.Equal(65552u, ciphertextLength);

        AssertError(
            ProtocolError.InvalidLength,
            () => cipher.Seal(TransferMessageType.Chunk, new byte[RecordCipher.MaxPlaintextSize + 1]));
    }

    [Fact]
    public void Directions_MirrorBetweenInitiatorAndResponder()
    {
        SessionKeys keys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            Hex("779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273"),
            HandshakeRole.Initiator);
        SessionKeys peerKeys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            Hex("779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273"),
            HandshakeRole.Responder);

        RecordCipher initiatorSend = RecordCipher.ForSending(keys);
        RecordCipher responderReceive = RecordCipher.ForReceiving(peerKeys);

        byte[] frame = initiatorSend.Seal(TransferMessageType.Decision, DecisionAccept);
        OpenedRecord record = responderReceive.Open(frame);

        Assert.Equal(TransferMessageType.Decision, record.Type);
        Assert.Equal("01", ToHex(record.Payload));

        // The reverse direction must use the other key, so the same plaintext yields different bytes.
        byte[] reverse = RecordCipher.ForSending(peerKeys).Seal(TransferMessageType.Decision, DecisionAccept);
        Assert.NotEqual(ToHex(frame), ToHex(reverse));
    }

    [Fact]
    public void Constructor_RejectsWrongKeyOrPrefixLength()
    {
        Assert.Throws<ArgumentException>(() => new RecordCipher(new byte[16], Hex(VectorPrefix)));
        Assert.Throws<ArgumentException>(() => new RecordCipher(Hex(VectorKey), new byte[12]));
    }

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }

    private static readonly byte[] RecordPlaintext = System.Text.Encoding.UTF8.GetBytes("feige-record-test");

    private static readonly byte[] DecisionAccept = [0x01];

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();
}
