using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Conversations;
using Feige.Core.Crypto;

namespace Feige.Core.Tests;

public sealed class V3ConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v3", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Consent_BlocksConnectionAndCreatesNoFilesUntilApproved()
    {
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Pair.CreateAsync(_root, (_, _) => { requested.SetResult(); return approval.Task; });
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(pending.IsCompleted);
        Assert.False(Directory.Exists(_root));
        approval.SetResult(true);
        await using var pair = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Windows-B", pair.A.PeerName);
        Assert.Equal(pair.IdentityB.Fingerprint, pair.A.PeerFingerprint);
        Assert.Equal(pair.IdentityA.Fingerprint, pair.B.PeerFingerprint);
        Assert.Equal(1, pair.ApprovalCount);
    }

    [Fact]
    public async Task Consent_RejectsAndClosesBothEnds()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Pair.CreateAsync(_root, (_, _) => Task.FromResult(false))
            .WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Consent_CancellationDoesNotWaitForAnUnresponsivePrompt()
    {
        using var cancel = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating = Pair.CreateAsync(_root, (_, _) => { requested.TrySetResult(); return prompt.Task; }, cancellationToken: cancel.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating.WaitAsync(TimeSpan.FromSeconds(10)));
        prompt.TrySetResult(true); // A late approval must not resurrect the connection.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Conversation_BidirectionalTextsAndMultipleFilesReuseConnectionAfterEachApproval()
    {
        await using var pair = await Pair.CreateAsync(_root);
        var textsA = new List<V3Text>(); var textsB = new List<V3Text>();
        pair.A.TextReceived = m => { textsA.Add(m); return Task.CompletedTask; };
        pair.B.TextReceived = m => { textsB.Add(m); return Task.CompletedTask; };
        var committed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.B.FileReceiving = _ => Task.FromResult(true);
        pair.A.FileReceiving = _ => Task.FromResult(true);
        pair.B.FileReceived = (_, path) => { Assert.True(File.Exists(path)); committed.TrySetResult(path); return Task.CompletedTask; };
        pair.Start();
        var source = await SourceAsync("report.bin", 200_000);
        var reply = await SourceAsync("reply.png", 70_003);
        await Task.WhenAll(pair.A.SendFileAsync(source), pair.B.SendFileAsync(reply, "image"),
            pair.A.SendTextAsync("  你好 👋\n文件来了  "), pair.B.SendTextAsync("已收到请求"))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("  你好 👋\n文件来了  ", Assert.Single(textsB).Text);
        Assert.Equal("已收到请求", Assert.Single(textsA).Text);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(Path.Combine(pair.InboxB, "report.bin")));
        Assert.Equal(await File.ReadAllBytesAsync(reply), await File.ReadAllBytesAsync(Path.Combine(pair.InboxA, "reply.png")));
        Assert.True(File.Exists(await committed.Task));
        // finish does not close the stream; a second file gets a suffix and continues the same sequence.
        await pair.A.SendFileAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Path.Combine(pair.InboxB, "report (1).bin")));
        await pair.A.SendTextAsync("第二批完成").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, textsB.Count);
        Assert.Equal(1, pair.ApprovalCount);
        Assert.Empty(Directory.GetFiles(pair.InboxB, ".feige-*.tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(105906177)] // >100 MiB, exceeding the old v1 cap.
    public async Task File_StreamsEmptyAndLargeWithoutLegacyCap(int length)
    {
        await using var pair = await Pair.CreateAsync(_root); pair.Start();
        var source = await SourceAsync("size.bin", length);
        var result = await pair.A.SendFileAsync(source).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(length, result.Size);
        await using var input = File.OpenRead(source);
        await using var output = File.OpenRead(Path.Combine(pair.InboxB, "size.bin"));
        Assert.Equal(length, output.Length);
        Assert.Equal(await SHA256.HashDataAsync(input), await SHA256.HashDataAsync(output));
    }

    [Fact]
    public async Task Text_DeliveryWaitsForRecordingAndRetriesAreDeduplicated()
    {
        await using var pair = await Pair.CreateAsync(_root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        pair.B.TextReceived = async _ => { count++; started.TrySetResult(); await recorded.Task; };
        pair.Start(); var id = Guid.NewGuid();
        var sending = pair.A.SendTextAsync("retry", id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(sending.IsCompleted);
        recorded.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10));
        await pair.A.SendTextAsync("retry", id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Text_DeduplicatesAcrossConnectionsAndSeededHistory()
    {
        using var identityA = IdentityKey.Create(); using var identityB = IdentityKey.Create();
        var id = Guid.NewGuid(); var inbox = new V3TextInbox(); int count = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var pair = await Pair.CreateAsync(_root, identityA: identityA, identityB: identityB);
            pair.B.TextReceived = _ => { count++; return Task.CompletedTask; };
            pair.Start(inbox);
            await pair.A.SendTextAsync("retry", id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, count);
        await using var restored = await Pair.CreateAsync(_root, identityA: identityA, identityB: identityB);
        restored.B.TextReceived = _ => throw new InvalidOperationException("History already contains this ID");
        restored.Start(new V3TextInbox(received: [(identityA.Fingerprint.ToLowerInvariant().Replace(" ", ""), id)]));
        await restored.A.SendTextAsync("retry", id).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Text_RecordingFailureClosesConnectionAndNeverAcknowledges()
    {
        await using var pair = await Pair.CreateAsync(_root);
        pair.B.TextReceived = _ => throw new IOException("simulated history write failure");
        pair.Start();
        await Assert.ThrowsAnyAsync<Exception>(() => pair.A.SendTextAsync("unsaved").WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<IOException>(() => pair.RunB!);
    }

    [Fact]
    public async Task Text_WithoutRecordingHandlerNeverClaimsDelivery()
    {
        await using var pair = await Pair.CreateAsync(_root); pair.Start();
        await Assert.ThrowsAnyAsync<Exception>(() => pair.A.SendTextAsync("unrecorded").WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.RunB!);
    }

    [Fact]
    public async Task File_ReceiverRecordsActiveMessageBeforeAcceptingData()
    {
        await using var pair = await Pair.CreateAsync(_root);
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool chunkReceived = false;
        pair.B.FileReceiving = async _ => { recording.TrySetResult(); await recorded.Task; return true; };
        pair.B.ReceiveProgress = (_, bytes) => { if (bytes > 0) chunkReceived = true; };
        pair.Start(); var source = await SourceAsync("record-before-receive.bin", 1024);
        var sending = pair.A.SendFileAsync(source);
        await recording.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(sending.IsCompleted); Assert.False(chunkReceived);
        recorded.TrySetResult(); await sending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(chunkReceived); Assert.True(File.Exists(Path.Combine(pair.InboxB, "record-before-receive.bin")));
    }

    [Fact]
    public async Task File_DeclineSendsNegativeDecisionWithoutCreatingFilesAndClosesConnection()
    {
        await using var pair = await Pair.CreateAsync(_root);
        pair.B.FileReceiving = _ => Task.FromResult(false);
        pair.Start();
        var source = await SourceAsync("declined.bin", 1024);
        await Assert.ThrowsAsync<IOException>(() => pair.A.SendFileAsync(source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.Exists(pair.InboxB) ? Directory.GetFiles(pair.InboxB) : []);
        await Assert.ThrowsAnyAsync<Exception>(() => pair.A.SendTextAsync("拒绝后连接已结束").WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task File_ActiveMessageStorageFailureRejectsTransferAndCleansTempFile()
    {
        await using var pair = await Pair.CreateAsync(_root);
        pair.B.FileReceiving = _ => Task.FromException<bool>(new IOException("simulated message storage failure"));
        pair.Start(); var source = await SourceAsync("not-accepted.bin", 1024);
        await Assert.ThrowsAnyAsync<Exception>(() => pair.A.SendFileAsync(source).WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<IOException>(() => pair.RunB!);
        Assert.Empty(Directory.Exists(pair.InboxB) ? Directory.GetFiles(pair.InboxB) : []);
    }

    [Fact]
    public async Task Frames_FragmentedHandshakeAndPersistentRecordsKeepEveryByte()
    {
        await using var pair = await Pair.CreateAsync(_root, fragmentReads: true);
        var received = new List<string>();
        pair.B.TextReceived = m => { received.Add(m.Text); return Task.CompletedTask; }; pair.Start();
        var file = await SourceAsync("fragmented.bin", 1025);
        await Task.WhenAll(pair.A.SendTextAsync("握手后的第一条"), pair.A.SendFileAsync(file)).WaitAsync(TimeSpan.FromSeconds(10));
        await pair.A.SendTextAsync("finish 后继续发送").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "握手后的第一条", "finish 后继续发送" }, received);
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(Path.Combine(pair.InboxB, "fragmented.bin")));
    }

    [Fact]
    public async Task Text_LateRecordingAfterDisconnectStillDeduplicatesOnReconnect()
    {
        using var identityA = IdentityKey.Create(); using var identityB = IdentityKey.Create();
        var inbox = new V3TextInbox(); var id = Guid.NewGuid(); int count = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var first = await Pair.CreateAsync(_root, identityA: identityA, identityB: identityB))
        {
            first.B.TextReceived = async _ => { count++; started.TrySetResult(); await recorded.Task; };
            first.Start(inbox);
            var sending = first.A.SendTextAsync("recording", id);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); first.B.Close();
            await Assert.ThrowsAnyAsync<Exception>(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await using var second = await Pair.CreateAsync(_root, identityA: identityA, identityB: identityB);
        second.B.TextReceived = _ => { count++; return Task.CompletedTask; }; second.Start(inbox);
        var retry = second.A.SendTextAsync("recording", id);
        recorded.TrySetResult();
        await retry.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, count);
    }

    [Fact]
    public async Task Cancel_ClosesPendingSendAndRemovesUncommittedFile()
    {
        await using var pair = await Pair.CreateAsync(_root);
        pair.Start();
        var source = await SourceAsync("cancel.bin", 1_000_000);
        using var cancel = new CancellationTokenSource();
        var sending = pair.A.SendFileAsync(source, progress: (_, _) => cancel.Cancel(), cancellationToken: cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(10)));
        await IgnoreFailure(pair.RunB!);
        Assert.Empty(Directory.Exists(pair.InboxB) ? Directory.GetFiles(pair.InboxB) : []);
    }

    [Fact]
    public async Task ClosingConnection_WakesEveryPendingTextReceipt()
    {
        await using var pair = await Pair.CreateAsync(_root);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.B.TextReceived = _ => { received.TrySetResult(); return new TaskCompletionSource().Task; };
        pair.Start();
        var first = pair.A.SendTextAsync("first"); var second = pair.A.SendTextAsync("second");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); pair.B.Close();
        await Assert.ThrowsAnyAsync<Exception>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("id")]
    [InlineData("overflow")]
    [InlineData("digest")]
    [InlineData("empty-finish")]
    [InlineData("missing-digest")]
    [InlineData("duplicate-manifest")]
    [InlineData("unexpected-receipt")]
    [InlineData("repeat-consent")]
    [InlineData("tamper")]
    public async Task InvalidPeer_ClosesConnectionAndCleansTemporaryFiles(string failure)
    {
        await using var peer = await RawPeer.CreateAsync();
        var inbox = Path.Combine(_root, "invalid");
        var running = peer.Connection.RunAsync(() => inbox);
        var id = Guid.NewGuid();
        if (failure == "unexpected-receipt") await peer.SendAsync(10, V3Wire.Encode(id));
        else if (failure == "repeat-consent") await peer.SendAsync(8, [1]);
        else if (failure == "tamper") await peer.SendAsync(9, V3Wire.Encode(new V3Text(id, "tampered")), tamper: true);
        else
        {
            await peer.SendAsync(1, V3Wire.Encode(new V3File(id, "bad.bin", 3)));
            Assert.Equal((byte)2, (await peer.ReadAsync()).Type);
            if (failure == "empty-finish") await peer.SendAsync(7, "{}"u8.ToArray());
            else if (failure == "missing-digest") await peer.SendAsync(7, V3Wire.Encode(new { transferID = id }));
            else if (failure == "duplicate-manifest") await peer.SendAsync(1, V3Wire.Encode(new V3File(Guid.NewGuid(), "other.bin", 0)));
            else
            {
                var chunk = new byte[failure == "overflow" ? 28 : 27];
                (failure == "id" ? Guid.NewGuid() : id).TryWriteBytes(chunk.AsSpan(0, 16), bigEndian: true, out _);
                BinaryPrimitives.WriteUInt64BigEndian(chunk.AsSpan(16, 8), failure == "offset" ? 1UL : 0UL);
                await peer.SendAsync(3, chunk);
                if (failure == "digest") await peer.SendAsync(7, V3Wire.Encode(new { transferID = id, sha256 = new byte[32] }));
            }
        }
        await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.Exists(inbox) ? Directory.GetFiles(inbox) : []);
    }

    private async Task<string> SourceAsync(string name, int length)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, name);
        await using var file = File.Create(path);
        var buffer = RandomNumberGenerator.GetBytes(65536);
        for (int offset = 0; offset < length; offset += buffer.Length)
            await file.WriteAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - offset)));
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static async Task IgnoreFailure(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is not TimeoutException) { } }

    private sealed class Pair : IAsyncDisposable
    {
        public required V3Connection A, B;
        public required IdentityKey IdentityA, IdentityB;
        public required string InboxA, InboxB;
        public int ApprovalCount;
        public Task? RunA, RunB;
        private bool _ownsIdentities;
        public static async Task<Pair> CreateAsync(string root, Func<string, CancellationToken, Task<bool>>? approve = null,
            IdentityKey? identityA = null, IdentityKey? identityB = null, CancellationToken cancellationToken = default, bool fragmentReads = false)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var a = new TcpClient(); var bTask = listener.AcceptTcpClientAsync();
            await a.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var b = await bTask;
            a.NoDelay = true; b.NoDelay = true;
            var pair = new Pair { A = null!, B = null!, IdentityA = identityA ?? IdentityKey.Create(), IdentityB = identityB ?? IdentityKey.Create(),
                InboxA = Path.Combine(root, "inbox-a"), InboxB = Path.Combine(root, "inbox-b"), _ownsIdentities = identityA is null };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            Stream streamA = a.GetStream(), streamB = b.GetStream();
            if (fragmentReads) { streamA = new FragmentedReadStream(streamA); streamB = new FragmentedReadStream(streamB); }
            var accept = V3Connection.EstablishAsync(streamB, pair.IdentityB, false, "Windows-B", (name, ct) =>
            { pair.ApprovalCount++; return approve?.Invoke(name, ct) ?? Task.FromResult(true); }, deadline.Token);
            var connect = V3Connection.EstablishAsync(streamA, pair.IdentityA, true, "Windows-A", (_, _) => throw new InvalidOperationException("Initiator must never prompt"), deadline.Token);
            try { await Task.WhenAll(connect, accept); pair.A = await connect; pair.B = await accept; return pair; }
            catch { a.Dispose(); b.Dispose(); if (pair._ownsIdentities) { pair.IdentityA.Dispose(); pair.IdentityB.Dispose(); } throw; }
        }
        public void Start(V3TextInbox? inbox = null)
        {
            RunA = A.RunAsync(() => InboxA);
            RunB = B.RunAsync(() => InboxB, textInbox: inbox);
        }
        public async ValueTask DisposeAsync()
        {
            A.Close(); B.Close();
            if (RunA is not null) await IgnoreFailure(RunA);
            if (RunB is not null) await IgnoreFailure(RunB);
            if (_ownsIdentities) { IdentityA.Dispose(); IdentityB.Dispose(); }
        }
    }

    private sealed class FragmentedReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(3, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // Independent peer drives malformed application records through the real signed handshake and TCP stream.
    private sealed class RawPeer : IAsyncDisposable
    {
        private NetworkStream _stream = null!;
        private V3RecordCipher _send = null!, _receive = null!;
        public required V3Connection Connection;
        public static async Task<RawPeer> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); var accepting = listener.AcceptTcpClientAsync();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await accepting;
            client.NoDelay = true; server.NoDelay = true;
            using var identity = IdentityKey.Create(); using var remoteIdentity = IdentityKey.Create();
            var stream = client.GetStream();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
            var established = V3Connection.EstablishAsync(server.GetStream(), remoteIdentity, false, "Receiver", (_, _) => Task.FromResult(true), ct);
            var ephemeral = X25519KeyAgreement.CreatePrivateKey();
            var offer = new V3Offer(3, 1, "Scripted peer", identity.SubjectPublicKeyInfo.ToArray(), X25519KeyAgreement.GetPublicKey(ephemeral), RandomNumberGenerator.GetBytes(32), "");
            await WriteFrame(stream, V3Wire.Encode(new { offer }), ct);
            using var envelope = System.Text.Json.JsonDocument.Parse(await ReadFrame(stream, ct));
            var remote = V3Wire.Decode<V3Offer>(System.Text.Encoding.UTF8.GetBytes(envelope.RootElement.GetProperty("offer").GetRawText()));
            var transcript = V3Wire.Transcript(offer, remote);
            await WriteFrame(stream, V3Wire.Encode(new { offer, signature = identity.Sign(transcript) }), ct);
            _ = await ReadFrame(stream, ct);
            var digest = SHA256.HashData(transcript); var shared = X25519KeyAgreement.Agree(ephemeral, remote.EphemeralPublicKey);
            var send = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v3 initiator-to-responder"u8.ToArray());
            var receive = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v3 responder-to-initiator"u8.ToArray());
            var peer = new RawPeer { _stream = stream, _send = new(send, digest[..4]), _receive = new(receive, digest[..4]), Connection = await established };
            CryptographicOperations.ZeroMemory(ephemeral); CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(send); CryptographicOperations.ZeroMemory(receive);
            Assert.Equal((byte)8, (await peer.ReadAsync()).Type);
            return peer;
        }
        public async Task SendAsync(byte type, byte[] payload, bool tamper = false)
        { var frame = _send.Seal(type, payload); if (tamper) frame[^1] ^= 1; await WriteFrame(_stream, frame, CancellationToken.None); }
        public async Task<(byte Type, byte[] Payload)> ReadAsync()
        { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); return _receive.Open(await ReadFrame(_stream, deadline.Token)); }
        private static async Task WriteFrame(Stream stream, byte[] payload, CancellationToken ct)
        { await stream.WriteAsync(V3Wire.Length(payload.Length), ct); await stream.WriteAsync(payload, ct); }
        private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken ct)
        {
            var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
            var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(prefix)]; await stream.ReadExactlyAsync(payload, ct); return payload;
        }
        public ValueTask DisposeAsync() { Connection.Close(); _stream.Dispose(); _send.Dispose(); _receive.Dispose(); return ValueTask.CompletedTask; }
    }
}
