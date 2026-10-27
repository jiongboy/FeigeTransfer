using System.Text;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Length-prefixed framing and TCP fragmentation handling from 通信协议 §4.1 / §4.2 / §4.6.
/// The expected frame bytes come from the independent Node implementation in
/// <c>scripts/verify-protocol-vectors.js</c>.
/// </summary>
public sealed class AuthenticatedRecordStreamTests
{
    private const string VectorKey = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string VectorPrefix = "779855ca494e4954";

    private const string ManifestFrame =
        "0000002e" +
        "01000000000000000000000021" +
        "02eff278f8692682ae1bec1a6c54adc0da" +
        "f7e2e9d4e1b6bfbac728898115a6db64";

    private const string ChunkFrame =
        "0000003d" +
        "03000000000000000100000030" +
        "d28326309f5a2dc13c837b11a21cdf3ea86263a9c58cc4f0467f4db33f722945" +
        "47b6443017390c042da902933d1e4f52";

    private const string FinishFrame =
        "0000001d" +
        "07000000000000000200000010" +
        "bcc19843d8716dbafb740136ca263574";

    private static readonly byte[] RecordPlaintext = Encoding.UTF8.GetBytes("feige-record-test");
    private static readonly byte[] ChunkPayload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Encode_PrependsLengthAndMatchesIndependentFrames()
    {
        AuthenticatedRecordStream stream = CreatePair();

        Assert.Equal(ManifestFrame, ToHex(stream.Encode(TransferMessageType.Manifest, RecordPlaintext)));
        Assert.Equal(ChunkFrame, ToHex(stream.Encode(TransferMessageType.Chunk, ChunkPayload)));
        Assert.Equal(FinishFrame, ToHex(stream.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void Append_ReassemblesRecordFromSingleByteFragments()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();

        byte[] frame = sender.Encode(TransferMessageType.Manifest, RecordPlaintext);

        var received = new List<OpenedRecord>();
        foreach (byte value in frame)
        {
            received.AddRange(receiver.Append([value]));
        }

        OpenedRecord record = Assert.Single(received);
        Assert.Equal(TransferMessageType.Manifest, record.Type);
        Assert.Equal("feige-record-test", Encoding.UTF8.GetString(record.Payload));
        Assert.Equal(0, receiver.PendingByteCount);
    }

    [Fact]
    public void Append_ReturnsEveryRecordFromOneBatch()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();

        byte[] batch = [.. sender.Encode(TransferMessageType.Manifest, RecordPlaintext), .. sender.Encode(TransferMessageType.Chunk, ChunkPayload)];

        IReadOnlyList<OpenedRecord> records = receiver.Append(batch);

        Assert.Equal(2, records.Count);
        Assert.Equal(TransferMessageType.Manifest, records[0].Type);
        Assert.Equal(TransferMessageType.Chunk, records[1].Type);
        Assert.Equal(1UL, records[1].Sequence);
    }

    [Fact]
    public void Append_HandlesSplitHeaderAndSplitBody()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();
        byte[] frame = sender.Encode(TransferMessageType.Chunk, ChunkPayload);

        Assert.Empty(receiver.Append(frame.AsSpan(0, 2)));   // partial length field
        Assert.Empty(receiver.Append(frame.AsSpan(2, 3)));   // length field completed, header partial
        Assert.Empty(receiver.Append(frame.AsSpan(5, 10)));  // header partial
        Assert.Single(receiver.Append(frame.AsSpan(15)));    // remainder completes the frame
    }

    [Fact]
    public void Encode_RejectsAnythingAfterFinish()
    {
        AuthenticatedRecordStream stream = CreatePair();
        Assert.False(stream.SendFinished);

        stream.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty);
        Assert.True(stream.SendFinished);

        AssertError(ProtocolError.AlreadyFinished, () => stream.Encode(TransferMessageType.Chunk, ChunkPayload));
    }

    [Fact]
    public void Append_RejectsTrailingBytesAfterFinishInSameBatch()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();
        byte[] finish = sender.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty);

        AssertError(ProtocolError.UnexpectedTrailingData, () => receiver.Append([.. finish, 0x00]));
    }

    [Fact]
    public void Append_RejectsBytesAfterFinishArrivedSeparately()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();

        receiver.Append(sender.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty));
        Assert.True(receiver.ReceiveFinished);
        Assert.Empty(receiver.Append(ReadOnlySpan<byte>.Empty)); // harmless no-op

        AssertError(ProtocolError.AlreadyFinished, () => receiver.Append([0x00]));
    }

    [Fact]
    public void EndOfStream_RequiresAuthenticatedFinish()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();

        AssertError(ProtocolError.UnexpectedEndOfStream, receiver.EndOfStream);

        receiver.Append(sender.Encode(TransferMessageType.Manifest, RecordPlaintext));
        AssertError(ProtocolError.UnexpectedEndOfStream, receiver.EndOfStream);

        receiver.Append(sender.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty));
        receiver.EndOfStream();
    }

    [Fact]
    public void EndOfStream_RejectsPartialFrame()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();
        byte[] frame = sender.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty);

        receiver.Append(frame.AsSpan(0, frame.Length - 3));

        AssertError(ProtocolError.UnexpectedEndOfStream, receiver.EndOfStream);
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("0000001c")] // 28, one byte below the minimum frame size
    [InlineData("0001001e")] // 65566, one byte above the maximum frame size
    [InlineData("ffffffff")]
    public void Append_RejectsImpossibleLengthField(string lengthPrefixHex)
    {
        AuthenticatedRecordStream receiver = CreatePair();

        AssertError(ProtocolError.InvalidLength, () => receiver.Append(Hex(lengthPrefixHex)));
    }

    [Fact]
    public void Append_RejectsFrameWhoseCiphertextLengthDisagrees()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();
        byte[] frame = sender.Encode(TransferMessageType.Manifest, RecordPlaintext);

        frame[4 + 12] = 0x20; // ctLen 0x21 -> 0x20 inside the authenticated header

        AssertError(ProtocolError.InvalidLength, () => receiver.Append(frame));
    }

    [Fact]
    public void Append_RejectsTamperedCiphertext()
    {
        AuthenticatedRecordStream sender = CreatePair();
        AuthenticatedRecordStream receiver = CreatePair();
        byte[] frame = sender.Encode(TransferMessageType.Manifest, RecordPlaintext);

        frame[^1] ^= 0x01;

        AssertError(ProtocolError.AuthenticationFailed, () => receiver.Append(frame));
    }

    [Fact]
    public void TwoStreams_TransferLargestChunkAndFinish()
    {
        SessionKeys keys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            Hex("779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273"),
            HandshakeRole.Initiator);
        SessionKeys peerKeys = SessionKeySchedule.Derive(
            Hex("0bd4d1211eb35a14d1b85c6c9392787de66575f611a49b66ba1d1314b436991d"),
            Hex("779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273"),
            HandshakeRole.Responder);

        var sender = AuthenticatedRecordStream.FromSessionKeys(keys);
        var receiver = AuthenticatedRecordStream.FromSessionKeys(peerKeys);

        // §4.5 chunk limit used by the macOS sender: 65536 - 128.
        byte[] chunk = new byte[65408];
        Random.Shared.NextBytes(chunk);

        byte[] wire = [.. sender.Encode(TransferMessageType.Chunk, chunk), .. sender.Encode(TransferMessageType.Finish, ReadOnlySpan<byte>.Empty)];

        IReadOnlyList<OpenedRecord> records = receiver.Append(wire);
        receiver.EndOfStream();

        Assert.Equal(2, records.Count);
        Assert.Equal(ToHex(chunk), ToHex(records[0].Payload));
        Assert.Equal(TransferMessageType.Finish, records[1].Type);
    }

    [Fact]
    public void MaxReadBatchSize_IsEightMaximumFrames()
    {
        Assert.Equal(RecordCipher.MaxFrameSize * 8, AuthenticatedRecordStream.MaxReadBatchSize);
    }

    private static AuthenticatedRecordStream CreatePair()
        => new(new RecordCipher(Hex(VectorKey), Hex(VectorPrefix)), new RecordCipher(Hex(VectorKey), Hex(VectorPrefix)));

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static string ToHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();
}
