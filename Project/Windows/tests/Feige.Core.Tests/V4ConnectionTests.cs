using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Conversations;
using Feige.Core.Crypto;

namespace Feige.Core.Tests;

public sealed class V4ConnectionTests
{
    [Fact]
    public async Task AuthenticatedV4Connection_ExchangesTextAndRejectsNoFallback()
    {
        var pair = await EstablishPair();
        var received = new TaskCompletionSource<V3Text>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.TextReceived = value => { received.TrySetResult(value); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync());
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            await pair.Initiator.SendTextAsync("跨平台 v4");
            V3Text message = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("跨平台 v4", message.Text);
            Assert.NotEqual(Guid.Empty, message.Id);
            Assert.Equal(4, pair.Initiator.ProtocolVersion);
            Assert.Equal(pair.ResponderFingerprint, pair.Initiator.PeerFingerprint);
            Assert.Equal(pair.InitiatorFingerprint, pair.Responder.PeerFingerprint);
            Assert.Equal(pair.InitiatorFingerprint, pair.Initiator.InitiatorFingerprint);
            Assert.Equal(pair.InitiatorFingerprint, pair.Responder.InitiatorFingerprint);
            Assert.Equal(pair.Initiator.TranscriptDigest, pair.Responder.TranscriptDigest);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task DecliningV4DirectFile_ClosesTheConnection()
    {
        var pair = await EstablishPair();
        string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(source, "decline me");
        pair.Responder.FileReceiving = _ => Task.FromResult(false);
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync(() => Path.GetTempPath()));
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            await Assert.ThrowsAsync<IOException>(() => pair.Initiator.SendFileAsync(source));
            Assert.True(pair.Initiator.IsClosed);
            Assert.True(pair.Responder.IsClosed);
        }
        finally
        {
            File.Delete(source);
            await ClosePair(pair, responderLoop, initiatorLoop);
        }
    }

    [Fact]
    public async Task DecliningV4GroupText_KeepsConnectionAvailable()
    {
        var pair = await EstablishPair();
        var text = new TaskCompletionSource<V3Text>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.GroupRecordReceived = async (type, payload) =>
        {
            Assert.Equal((byte)12, type);
            GroupMessageEnvelope envelope = V4GroupWire.DecodeGroupText(payload);
            await pair.Responder.SendGroupTextAckAsync(V4GroupWire.EncodeCanonical(
                new GroupTextAck(envelope.GroupID, envelope.Id, envelope.MemberRevision, accepted: false, reason: "stale-membership")));
        };
        pair.Responder.TextReceived = value => { text.TrySetResult(value); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync());
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            var message = new GroupMessageEnvelope(Guid.NewGuid(), Guid.NewGuid(), pair.InitiatorFingerprint, 1,
                DateTimeOffset.UtcNow, GroupMessageKind.Text, text: "群成员版本不一致");
            await Assert.ThrowsAsync<IOException>(() => pair.Initiator.SendGroupTextAsync(
                V4GroupWire.EncodeCanonical(message), message.Id));
            Assert.False(pair.Initiator.IsClosed);
            Assert.False(pair.Responder.IsClosed);
            await pair.Initiator.SendTextAsync("拒收后私聊仍在线");
            Assert.Equal("拒收后私聊仍在线", (await text.Task.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task V4GroupFileTransfer_UsesStrictResponsesAndSharedOutgoingSlot()
    {
        var pair = await EstablishPair();
        string root = Path.Combine(Path.GetTempPath(), "feige-v4-group-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string receiveDirectory = Path.Combine(root, "receive");
        Directory.CreateDirectory(sourceDirectory);
        byte[] content = RandomNumberGenerator.GetBytes(180_000);
        string source = Path.Combine(sourceDirectory, "group-payload.bin");
        await File.WriteAllBytesAsync(source, content);
        Guid groupID = Guid.NewGuid();
        Guid messageID = Guid.NewGuid();
        Guid transferID = Guid.NewGuid();
        byte[] digest = SHA256.HashData(content);
        var metadata = new V4GroupFileMetadata(transferID, Path.GetFileName(source), (ulong)content.Length);
        var manifest = new V4GroupFileManifest(groupID, messageID, pair.Responder.PeerFingerprint, 3, metadata, digest);
        var received = new TaskCompletionSource<(V4GroupFileManifest Manifest, string Path)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.GroupFileReceiving = value => Task.FromResult(value.SenderFingerprint == pair.Responder.PeerFingerprint && value.MemberRevision == 3);
        pair.Responder.GroupFileReceived = (value, path) => { received.TrySetResult((value, path)); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync(() => receiveDirectory));
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            byte[] decisionBytes = await pair.Initiator.SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID);
            Assert.True(V4GroupFileWire.DecodeDecision(decisionBytes).Accepted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Initiator.SendFileAsync(source));

            ulong offset = 0;
            while (offset < (ulong)content.Length)
            {
                int count = (int)Math.Min(V4Wire.MaxPlaintext - 24, (ulong)content.Length - offset);
                await pair.Initiator.SendGroupFileChunkAsync(transferID, offset, content.AsMemory((int)offset, count));
                offset += (ulong)count;
            }
            byte[] finish = V4Wire.Encode(new { TransferID = transferID, Sha256 = digest });
            byte[] receiptBytes = await pair.Initiator.SendGroupFileFinishAsync(finish, transferID);
            V4GroupFileReceipt receipt = V4GroupFileWire.DecodeReceipt(receiptBytes);
            Assert.Equal(groupID, receipt.GroupID);
            Assert.Equal(digest, receipt.Sha256);
            var file = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(manifest.GroupID, file.Manifest.GroupID);
            Assert.Equal(manifest.MessageID, file.Manifest.MessageID);
            Assert.Equal(manifest.SenderFingerprint, file.Manifest.SenderFingerprint);
            Assert.Equal(manifest.MemberRevision, file.Manifest.MemberRevision);
            Assert.Equal(manifest.Metadata, file.Manifest.Metadata);
            Assert.Equal(manifest.Sha256, file.Manifest.Sha256);
            Assert.Equal(content, await File.ReadAllBytesAsync(file.Path));
        }
        finally
        {
            await ClosePair(pair, responderLoop, initiatorLoop);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DecliningV4GroupFile_KeepsConnectionAvailable()
    {
        var pair = await EstablishPair();
        byte[] content = "not accepted"u8.ToArray();
        Guid groupID = Guid.NewGuid();
        Guid messageID = Guid.NewGuid();
        Guid transferID = Guid.NewGuid();
        var manifest = new V4GroupFileManifest(groupID, messageID, pair.Responder.PeerFingerprint, 1,
            new V4GroupFileMetadata(transferID, "declined.txt", (ulong)content.Length), SHA256.HashData(content));
        var text = new TaskCompletionSource<V3Text>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.GroupFileReceiving = _ => Task.FromResult(false);
        pair.Responder.TextReceived = value => { text.TrySetResult(value); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync(() => Path.GetTempPath()));
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            byte[] response = await pair.Initiator.SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID);
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(response);
            Assert.False(decision.Accepted);
            await pair.Initiator.SendTextAsync("群文件拒绝后连接仍可用");
            Assert.Equal("群文件拒绝后连接仍可用", (await text.Task.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task V4FileTransfer_CommitsBeforeReceiptAndPreservesContents()
    {
        var pair = await EstablishPair();
        string root = Path.Combine(Path.GetTempPath(), "feige-v4-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string receiveDirectory = Path.Combine(root, "receive");
        Directory.CreateDirectory(sourceDirectory);
        byte[] content = RandomNumberGenerator.GetBytes(220_000);
        string source = Path.Combine(sourceDirectory, "payload.bin");
        await File.WriteAllBytesAsync(source, content);
        var received = new TaskCompletionSource<(V3File Metadata, string Path)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.FileReceiving = _ => Task.FromResult(true);
        pair.Responder.FileReceived = (metadata, path) => { received.TrySetResult((metadata, path)); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync(() => receiveDirectory));
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            V3File sent = await pair.Initiator.SendFileAsync(source);
            var receivedFile = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sent.TransferID, receivedFile.Metadata.TransferID);
            Assert.Equal(content, await File.ReadAllBytesAsync(receivedFile.Path));
            Assert.True(File.Exists(receivedFile.Path));
        }
        finally
        {
            await ClosePair(pair, responderLoop, initiatorLoop);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticatedV5Connection_UsesVersionBoundTranscriptAndEnablesDurableAbort()
    {
        var pair = await EstablishPair(V4Offer.DurableAbortVersion);
        var received = new TaskCompletionSource<V3Text>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Responder.TextReceived = value => { received.TrySetResult(value); return Task.CompletedTask; };
        Task responderLoop = Task.Run(() => pair.Responder.RunAsync());
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            Assert.Equal(5, pair.Initiator.ProtocolVersion);
            Assert.Equal(5, pair.Responder.ProtocolVersion);
            Assert.True(pair.Initiator.SupportsDurableAbort);
            Assert.True(pair.Responder.SupportsDurableAbort);
            await pair.Initiator.SendTextAsync("跨平台 v5");
            Assert.Equal("跨平台 v5", (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Text);
            Assert.Equal(pair.Initiator.TranscriptDigest, pair.Responder.TranscriptDigest);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    private static async Task<Pair> EstablishPair(ushort protocolVersion = V4Offer.CurrentVersion)
    {
        using var initiatorIdentity = IdentityKey.Create();
        using var responderIdentity = IdentityKey.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient(AddressFamily.InterNetwork);
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        Task<V4Connection> responderTask = V4Connection.EstablishAsync(accepted.GetStream(), responderIdentity, false, "Win-B",
            (_, _) => Task.FromResult(true));
        V4Connection initiator = await V4Connection.EstablishAsync(client.GetStream(), initiatorIdentity, true, "Mac-A",
            (_, _) => throw new InvalidOperationException("initiator must not request local consent"), protocolVersion: protocolVersion);
        V4Connection responder = await responderTask;
        return new Pair(initiator, responder, client, accepted, initiatorIdentity.Fingerprint, responderIdentity.Fingerprint);
    }

    private static async Task ClosePair(Pair pair, params Task[] loops)
    {
        pair.Initiator.Close(); pair.Responder.Close();
        await Task.WhenAll(loops.Select(Observe));
        await pair.Initiator.DisposeAsync(); await pair.Responder.DisposeAsync();
        pair.Client.Dispose(); pair.Accepted.Dispose();
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private sealed record Pair(V4Connection Initiator, V4Connection Responder, TcpClient Client, TcpClient Accepted,
        string InitiatorFingerprint, string ResponderFingerprint);
}
