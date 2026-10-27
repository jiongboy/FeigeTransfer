using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Sender and receiver state machines of 通信协议 §5.1 / §5.2, plus one end-to-end run over a real TCP socket that
/// performs a real handshake first and then transfers 120000 bytes through the encrypted record layer.
/// </summary>
public sealed class TransferFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-transfer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Transfer_WritesVerifiedFileAndSendsReceipt()
    {
        byte[] content = RandomNumberGenerator.GetBytes(120_000);
        TransferManifest manifest = ManifestFor("photo.bin", content);
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, m =>
        {
            Assert.Equal("photo.bin", m.FileName);
            Assert.Equal(content.Length, m.Size);
            return true;
        });
        Task sendTask = TransferSender.SendAsync(sender, manifest, new MemoryStream(content));

        await Task.WhenAll(receiveTask, sendTask).WaitAsync(TimeSpan.FromSeconds(10));
        ReceivedFile received = await receiveTask;

        Assert.Equal(Path.Combine(_root, "photo.bin"), received.Path);
        Assert.Equal(content, await File.ReadAllBytesAsync(received.Path));
        Assert.Empty(Directory.GetFiles(_root, ".feige-*.tmp"));
    }

    [Fact]
    public async Task Transfer_DeclinedByReceiver_MakesSenderStop()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1000);
        TransferManifest manifest = ManifestFor("photo.bin", content);
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => false);
        Task sendTask = TransferSender.SendAsync(sender, manifest, new MemoryStream(content));

        ProtocolException receiverError = await Assert.ThrowsAsync<ProtocolException>(() => receiveTask);
        Assert.Equal(ProtocolError.UserRejected, receiverError.Error);

        ProtocolException senderError = await Assert.ThrowsAsync<ProtocolException>(() => sendTask);
        Assert.Equal(ProtocolError.PeerRejected, senderError.Error);

        // Nothing was written, not even a temp file, and the directory was never created.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Transfer_ChecksumMismatch_LeavesNoFileAndNoReceipt()
    {
        byte[] content = RandomNumberGenerator.GetBytes(5000);
        TransferManifest manifest = new("photo.bin", content.Length, new byte[32]);
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => true);
        Task sendTask = TransferSender.SendAsync(sender, manifest, new MemoryStream(content));

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => receiveTask);
        Assert.Equal(ProtocolError.ChecksumMismatch, error.Error);

        // A receiver never sends receipt without a committed file, so the sender only sees the connection drop.
        sender.Close();
        await Assert.ThrowsAnyAsync<ProtocolException>(() => sendTask);

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Receive_RejectsChunkLargerThanTheSpecLimit()
    {
        byte[] content = new byte[65536];
        TransferManifest manifest = ManifestFor("photo.bin", content);
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => true);

        await sender.WriteAsync(TransferMessageType.Manifest, manifest.ToJson());
        OpenedRecord decision = await sender.ReadAsync();
        Assert.Equal(0x01, decision.Payload[0]);

        // The record layer allows up to 65536 bytes, but §4.5 caps chunk payloads at 65408.
        await sender.WriteAsync(TransferMessageType.Chunk, content);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => receiveTask);
        Assert.Equal(ProtocolError.InvalidLength, error.Error);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Receive_RejectsChunksThatExceedTheManifestSize()
    {
        TransferManifest manifest = new("photo.bin", 10, new byte[32]);
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => true);

        await sender.WriteAsync(TransferMessageType.Manifest, manifest.ToJson());
        await sender.ReadAsync();
        await sender.WriteAsync(TransferMessageType.Chunk, new byte[11]);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => receiveTask);
        Assert.Equal(ProtocolError.SizeMismatch, error.Error);
    }

    [Fact]
    public async Task Receive_RejectsRecordsBeforeTheManifest()
    {
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => true);

        await sender.WriteAsync(TransferMessageType.Chunk, new byte[10]);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => receiveTask);
        Assert.Equal(ProtocolError.UnexpectedMessage, error.Error);
    }

    [Fact]
    public async Task Send_RejectsSourceShorterThanManifest()
    {
        byte[] content = new byte[10];
        TransferManifest manifest = new("photo.bin", 100, SHA256.HashData(content));
        (MemoryRecordChannel sender, MemoryRecordChannel receiver) = MemoryRecordChannel.CreatePair();

        Task sendTask = TransferSender.SendAsync(sender, manifest, new MemoryStream(content));
        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiver, _root, _ => true);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => sendTask);
        Assert.Equal(ProtocolError.SizeMismatch, error.Error);

        sender.Close();
        await Assert.ThrowsAnyAsync<ProtocolException>(() => receiveTask);
    }

    [Fact]
    public async Task FullStack_HandshakeThenTransferOverTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

        await using NetworkStream senderStream = client.GetStream();
        await using NetworkStream receiverStream = server.GetStream();

        using IdentityKey senderIdentity = IdentityKey.Create();
        using IdentityKey receiverIdentity = IdentityKey.Create();
        using LocalHandshake senderHandshake = LocalHandshake.Create(senderIdentity, HandshakeRole.Initiator, "Win-A", "135790");
        using LocalHandshake receiverHandshake = LocalHandshake.Create(receiverIdentity, HandshakeRole.Responder, "Win-B", "135790");

        var senderDuplex = new StreamHandshakeDuplex(senderStream);
        var receiverDuplex = new StreamHandshakeDuplex(receiverStream);

        Task<HandshakeOutcome> senderHandshakeTask = senderHandshake.RunAsync(senderDuplex, _ => true);
        Task<HandshakeOutcome> receiverHandshakeTask = receiverHandshake.RunAsync(receiverDuplex, _ => true);
        HandshakeOutcome[] outcomes = await Task.WhenAll(senderHandshakeTask, receiverHandshakeTask).WaitAsync(TimeSpan.FromSeconds(20));

        // Bytes the handshake read past its last message belong to the record layer.
        var senderChannel = new StreamRecordChannel(senderStream, outcomes[0].Keys, senderDuplex.TakeRemainder());
        var receiverChannel = new StreamRecordChannel(receiverStream, outcomes[1].Keys, receiverDuplex.TakeRemainder());

        byte[] content = RandomNumberGenerator.GetBytes(120_000);
        TransferManifest manifest = ManifestFor("payload.bin", content);

        Task<ReceivedFile> receiveTask = TransferReceiver.ReceiveAsync(receiverChannel, _root, _ => true);
        Task sendTask = TransferSender.SendAsync(senderChannel, manifest, new MemoryStream(content));

        await Task.WhenAll(receiveTask, sendTask).WaitAsync(TimeSpan.FromSeconds(30));
        ReceivedFile received = await receiveTask;

        Assert.Equal(content, await File.ReadAllBytesAsync(received.Path));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), Convert.ToHexString(received.Manifest.Sha256.Span));
    }

    [Fact]
    public async Task StreamRecordChannel_ConsumesHandshakeRemainder()
    {
        SessionKeys senderKeys = SessionKeySchedule.Derive(
            Enumerable.Repeat((byte)0x55, 32).ToArray(),
            Enumerable.Repeat((byte)0x66, 32).ToArray(),
            HandshakeRole.Initiator);
        SessionKeys receiverKeys = SessionKeySchedule.Derive(
            Enumerable.Repeat((byte)0x55, 32).ToArray(),
            Enumerable.Repeat((byte)0x66, 32).ToArray(),
            HandshakeRole.Responder);

        byte[] payload = "split-across-reads"u8.ToArray();
        byte[] recordFrame = AuthenticatedRecordStream.FromSessionKeys(senderKeys).Encode(TransferMessageType.Chunk, payload);

        // Simulate one TCP read that ends in the middle of the first record.
        var reader = new HandshakeFrameReader();
        IReadOnlyList<byte[]> messages = reader.Append([.. HandshakeMessages.EncodeOfferFrame(Offer()), .. recordFrame.AsSpan(0, 7)]);
        Assert.Single(messages);

        byte[] remainder = reader.TakeRemainder();
        Assert.Equal(7, remainder.Length);

        using var stream = new MemoryStream(recordFrame[7..]);
        var channel = new StreamRecordChannel(stream, receiverKeys, remainder);
        OpenedRecord record = await channel.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, record.Payload);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    private static TransferManifest ManifestFor(string fileName, byte[] content)
        => new(fileName, content.Length, SHA256.HashData(content));

    private static HandshakeOffer Offer() => new(
        HandshakeOffer.CurrentVersion,
        HandshakeRole.Initiator,
        "Win-A",
        [1, 2, 3, 4],
        Enumerable.Repeat((byte)0x11, 32).ToArray(),
        Enumerable.Repeat((byte)0x22, 32).ToArray(),
        "123456");

    /// <summary>Paired channels that exchange records in memory, without encryption, for state-machine tests.</summary>
    private sealed class MemoryRecordChannel : IRecordChannel
    {
        private readonly Channel<OpenedRecord> _inbox = Channel.CreateUnbounded<OpenedRecord>();
        private MemoryRecordChannel? _peer;
        private ulong _sequence;

        public static (MemoryRecordChannel Sender, MemoryRecordChannel Receiver) CreatePair()
        {
            var a = new MemoryRecordChannel();
            var b = new MemoryRecordChannel();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public ValueTask WriteAsync(TransferMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => _peer!._inbox.Writer.WriteAsync(new OpenedRecord(type, _sequence++, payload.ToArray()), cancellationToken);

        public async ValueTask<OpenedRecord> ReadAsync(CancellationToken cancellationToken = default)
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_inbox.Reader.TryRead(out OpenedRecord? record))
                {
                    return record;
                }
            }

            throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
        }

        /// <summary>Simulates the peer closing the connection after a failure.</summary>
        public void Close()
        {
            _inbox.Writer.TryComplete();
            _peer?._inbox.Writer.TryComplete();
        }
    }
}
