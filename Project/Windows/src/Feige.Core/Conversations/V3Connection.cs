using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Feige.Core.Crypto;
using Feige.Core.Storage;

namespace Feige.Core.Conversations;

/// <summary>One receiver-approved, persistent, bidirectional conversation; exactly one reader owns the stream.</summary>
public sealed class V3Connection : IAsyncDisposable
{
    private sealed record OfferEnvelope([property: JsonRequired] V3Offer Offer);
    private sealed record ProofEnvelope([property: JsonRequired] V3Offer Offer, [property: JsonRequired] byte[] Signature);
    private sealed record Decision([property: JsonRequired] Guid TransferID, [property: JsonRequired] bool Accepted);
    private sealed record Digest([property: JsonRequired] Guid TransferID, [property: JsonRequired] byte[] Sha256);
    private readonly Stream _stream;
    private readonly V3RecordCipher _send, _receive;
    private readonly SemaphoreSlim _writer = new(1), _fileSender = new(1);
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<(byte, Guid), TaskCompletionSource<byte[]>> _responses = new();
    private int _running, _disposed;
    public string PeerName { get; }
    public string PeerFingerprint { get; }
    /// <summary>Must record the received text successfully before completing; only then is a receipt sent.</summary>
    public Func<V3Text, Task> TextReceived { get; set; } = _ => Task.FromException(new InvalidOperationException("尚未配置文字消息记录。"));
    /// <summary>Called after file metadata arrives; returns true only after receiver approval.</summary>
    public Func<V3File, Task<bool>> FileReceiving { get; set; } = _ => Task.FromResult(true);
    public Func<V3File, string, Task> FileReceived { get; set; } = (_, _) => Task.CompletedTask;
    public Action<V3File, long>? ReceiveProgress { get; set; }
    /// <summary>Optional application work lease; null from the factory rejects new business during update handoff.</summary>
    public Func<IDisposable?>? BeginIncomingWork { get; set; }
    private V3Connection(Stream stream, V3Offer peer, byte[] digest, byte[] sendKey, byte[] receiveKey)
    {
        _stream = stream; PeerName = peer.DeviceName; PeerFingerprint = V3Wire.Fingerprint(peer);
        _send = new(sendKey, digest[..4]); _receive = new(receiveKey, digest[..4]);
        CryptographicOperations.ZeroMemory(sendKey); CryptographicOperations.ZeroMemory(receiveKey);
    }

    public static async Task<V3Connection> EstablishAsync(Stream stream, IdentityKey identity, bool initiator, string name,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(approve);
        byte[] ephemeral = X25519KeyAgreement.CreatePrivateKey();
        V3Connection? connection = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
            var ct = deadline.Token;
            var offer = new V3Offer(3, initiator ? (byte)1 : (byte)2, name, identity.SubjectPublicKeyInfo.ToArray(),
                X25519KeyAgreement.GetPublicKey(ephemeral), RandomNumberGenerator.GetBytes(32), "");
            offer.Validate();
            await WriteFrameAsync(stream, V3Wire.Encode(new OfferEnvelope(offer)), ct);
            var peer = V3Wire.Decode<OfferEnvelope>(await ReadFrameAsync(stream, 16384, TimeSpan.FromSeconds(15), ct)).Offer;
            if (peer is null) throw new InvalidDataException("缺少对端设备信息。");
            peer.Validate(); var transcript = V3Wire.Transcript(offer, peer);
            await WriteFrameAsync(stream, V3Wire.Encode(new ProofEnvelope(offer, identity.Sign(transcript))), ct);
            var proof = V3Wire.Decode<ProofEnvelope>(await ReadFrameAsync(stream, 16384, TimeSpan.FromMinutes(5), ct));
            if (!V3Wire.Encode(proof.Offer).AsSpan().SequenceEqual(V3Wire.Encode(peer))) throw new InvalidDataException("握手信息不一致。");
            IdentityKey.VerifyOrThrow(peer.IdentityPublicKeyDER, transcript, proof.Signature);
            var digest = SHA256.HashData(transcript);
            var shared = X25519KeyAgreement.Agree(ephemeral, peer.EphemeralPublicKey);
            try
            {
                byte[] i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v3 initiator-to-responder"u8.ToArray());
                byte[] r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest, "feige-v3 responder-to-initiator"u8.ToArray());
                connection = new(stream, peer, digest, initiator ? i2r : r2i, initiator ? r2i : i2r);
            }
            finally { CryptographicOperations.ZeroMemory(shared); }
            using var consent = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            consent.CancelAfter(TimeSpan.FromMinutes(5));
            if (initiator)
            {
                var record = await connection.ReadRecordAsync(TimeSpan.FromMinutes(5), consent.Token);
                if (record.Type != 8 || record.Payload.Length != 1 || record.Payload[0] > 1) throw new InvalidDataException("连接回执无效。");
                if (record.Payload[0] == 0) throw new InvalidOperationException("对方拒绝了连接。");
            }
            else
            {
                bool accepted = await approve(peer.DeviceName, consent.Token).WaitAsync(consent.Token);
                await connection.WriteAsync(8, new byte[] { accepted ? (byte)1 : (byte)0 }, consent.Token);
                if (!accepted) throw new InvalidOperationException("已拒绝连接。");
            }
            return connection;
        }
        catch (Exception ex)
        {
            if (connection is not null) connection.Close(); else stream.Dispose();
            // Closing one endpoint can win the socket race against cancellation at the other endpoint.
            // A caller-requested cancellation still takes precedence over the resulting EOF/socket exception.
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("连接已取消。", ex, cancellationToken);
            if (ex is OperationCanceledException)
                throw new TimeoutException("连接或同意等待超时。", ex);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(ephemeral); }
    }

    public async Task SendTextAsync(string text, Guid? id = null, CancellationToken cancellationToken = default)
    {
        var message = new V3Text(id ?? Guid.NewGuid(), text); message.Validate();
        try { await RequestAsync(9, V3Wire.Encode(message), 10, message.Id, TimeSpan.FromSeconds(30), cancellationToken); }
        catch { Close(); throw; }
    }

    public async Task<V3File> SendFileAsync(string path, string kind = "file", Guid? id = null,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _fileSender.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有文件正在发送。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var ct = linked.Token;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = new V3File(id ?? Guid.NewGuid(), Path.GetFileName(path), file.Length, kind); metadata.Validate();
            Decision decision;
            try { decision = V3Wire.Decode<Decision>(await RequestAsync(1, V3Wire.Encode(metadata), 2, metadata.TransferID, TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10), ct)); }
            catch (TimeoutException ex) { Close(); throw new IOException("等待接收确认超时。", ex); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new IOException("连接已断开。", ex); }
            if (decision.TransferID != metadata.TransferID) throw new InvalidDataException("文件接收决定 ID 无效。");
            if (!decision.Accepted) { Close(); throw new IOException("已拒绝接收文件。"); }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[65512]; long offset = 0;
            while (offset < metadata.Size)
            {
                int read = await file.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, metadata.Size - offset)), ct);
                if (read == 0) throw new EndOfStreamException("源文件在发送时发生变化。");
                var payload = new byte[24 + read];
                metadata.TransferID.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
                BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(16, 8), (ulong)offset);
                bytes.AsSpan(0, read).CopyTo(payload.AsSpan(24));
                await WriteAsync(3, payload, ct); hash.AppendData(bytes, 0, read); offset += read; progress?.Invoke(offset, metadata.Size);
            }
            var finish = new Digest(metadata.TransferID, hash.GetHashAndReset());
            var receipt = V3Wire.Decode<Digest>(await RequestAsync(7, V3Wire.Encode(finish), 4, metadata.TransferID, TimeSpan.FromMinutes(5), ct));
            if (receipt.Sha256 is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(receipt.Sha256, finish.Sha256))
                throw new InvalidDataException("文件回执摘要不一致。");
            return metadata;
        }
        catch { Close(); throw; }
        finally { _fileSender.Release(); }
    }

    public async Task RunAsync(Func<string> receiveDirectory, CancellationToken cancellationToken = default, V3TextInbox? textInbox = null)
    {
        textInbox ??= new V3TextInbox();
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("接收循环只能启动一次。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var ct = linked.Token; TransferFileSink? sink = null; V3File? incoming = null; IDisposable? receiveWork = null;
        try
        {
            while (true)
            {
                var (type, payload) = await ReadRecordAsync(incoming is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(15), ct);
                switch (type)
                {
                    case 1:
                        if (sink is not null) throw new InvalidDataException("已有文件正在接收。");
                        incoming = V3Wire.Decode<V3File>(payload); incoming.Validate();
                        if (BeginIncomingWork is { } beginFile)
                        {
                            receiveWork = beginFile();
                            if (receiveWork is null)
                            {
                                await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, false)), ct);
                                incoming = null; Close(); return;
                            }
                        }
                        await Task.Yield(); // Allow both peer read loops to start before a synchronous approval callback returns.
                        bool accepted;
                        try
                        {
                            accepted = await FileReceiving(incoming).WaitAsync(ct);
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, false)), ct);
                            incoming = null; receiveWork?.Dispose(); receiveWork = null; Close(); return;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            incoming = null; receiveWork?.Dispose(); receiveWork = null;
                            throw;
                        }
                        catch (Exception)
                        {
                            try { await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, false)), ct); } catch { }
                            incoming = null; receiveWork?.Dispose(); receiveWork = null; Close(); throw;
                        }
                        if (!accepted)
                        {
                            await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, false)), ct);
                            incoming = null; receiveWork?.Dispose(); receiveWork = null; Close(); return;
                        }
                        try { sink = TransferFileSink.CreateForSession(receiveDirectory(), incoming.FileName, incoming.Size); }
                        catch
                        {
                            try { await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, false)), ct); } catch { }
                            incoming = null; receiveWork?.Dispose(); receiveWork = null; Close();
                            throw;
                        }
                        await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, true)), ct);
                        ReceiveProgress?.Invoke(incoming, 0); break;
                    case 3:
                        if (sink is null || incoming is null || payload.Length <= 24 ||
                            new Guid(payload.AsSpan(0, 16), bigEndian: true) != incoming.TransferID ||
                            BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(16, 8)) != (ulong)sink.Written)
                            throw new InvalidDataException("文件块 ID 或偏移无效。");
                        await sink.WriteAsync(payload.AsMemory(24), ct); ReceiveProgress?.Invoke(incoming, sink.Written); break;
                    case 7:
                        var finish = V3Wire.Decode<Digest>(payload);
                        if (sink is null || incoming is null || finish.TransferID != incoming.TransferID || finish.Sha256 is not { Length: 32 })
                            throw new InvalidDataException("文件结束消息无效。");
                        string saved = await sink.CommitAsync(finish.Sha256, ct);
                        await sink.DisposeAsync(); sink = null;
                        var complete = incoming; incoming = null;
                        await WriteAsync(4, V3Wire.Encode(finish), ct);
                        await FileReceived(complete, saved);
                        receiveWork?.Dispose(); receiveWork = null; break;
                    case 9:
                        var text = V3Wire.Decode<V3Text>(payload); text.Validate();
                        using (var textWork = BeginIncomingWork?.Invoke())
                        {
                            if (BeginIncomingWork is not null && textWork is null) throw new IOException("应用正在准备安装更新。");
                            await textInbox.RecordAsync(PeerFingerprint, text, TextReceived, ct);
                            await WriteAsync(10, V3Wire.Encode(text.Id), ct);
                        }
                        break;
                    case 2: case 4: case 10:
                        Guid id = type == 2 ? V3Wire.Decode<Decision>(payload).TransferID : type == 4 ? V3Wire.Decode<Digest>(payload).TransferID : V3Wire.Decode<Guid>(payload);
                        if (!_responses.TryRemove((type, id), out var waiter)) throw new InvalidDataException("收到未知回执。");
                        waiter.TrySetResult(payload); break;
                    default: throw new InvalidDataException("不支持的会话消息。");
                }
            }
        }
        finally { Close(); try { if (sink is not null) await sink.DisposeAsync(); } finally { receiveWork?.Dispose(); } }
    }

    private async Task<byte[]> RequestAsync(byte type, byte[] payload, byte responseType, Guid id, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responses.TryAdd((responseType, id), completion)) throw new InvalidOperationException("消息已在发送。");
        try { await WriteAsync(type, payload, linked.Token); return await completion.Task.WaitAsync(timeout, linked.Token); }
        catch (TimeoutException) { Close(); throw; }
        finally { _responses.TryRemove((responseType, id), out _); }
    }
    private async Task WriteAsync(byte type, byte[] payload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _writer.WaitAsync(linked.Token);
        try { await WriteFrameAsync(_stream, _send.Seal(type, payload), linked.Token); }
        finally { _writer.Release(); }
    }
    private async Task<(byte Type, byte[] Payload)> ReadRecordAsync(TimeSpan timeout, CancellationToken ct)
        => _receive.Open(await ReadFrameAsync(_stream, 65565, timeout, ct));
    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        if (payload.Length > 65565) throw new InvalidDataException("帧长度无效。");
        await stream.WriteAsync(V3Wire.Length(payload.Length), deadline.Token);
        // A successful write refreshes the idle budget.
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await stream.WriteAsync(payload, deadline.Token);
    }
    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maximum, TimeSpan timeout, CancellationToken ct)
    {
        var prefix = new byte[4];
        // An idle conversation can wait indefinitely, but a partial frame cannot hold it forever.
        await ReadExactlyAsync(stream, prefix.AsMemory(0, 1), timeout, ct);
        var frameTimeout = timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(15) : timeout;
        await ReadExactlyAsync(stream, prefix.AsMemory(1), frameTimeout, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 1 || length > maximum) throw new InvalidDataException("帧长度无效。");
        var bytes = new byte[length]; await ReadExactlyAsync(stream, bytes, frameTimeout, ct); return bytes;
    }
    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, TimeSpan idleTimeout, CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(idleTimeout);
            int read = await stream.ReadAsync(bytes, deadline.Token);
            if (read == 0) throw new EndOfStreamException("连接已断开。");
            bytes = bytes[read..];
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel(); _stream.Dispose();
        foreach (var pending in _responses.Values)
        {
            pending.TrySetException(new IOException("连接已断开。"));
            _ = pending.Task.Exception; // Observe even when linked cancellation has already released the waiter.
        }
        _responses.Clear();
        _send.Dispose(); _receive.Dispose();
    }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
