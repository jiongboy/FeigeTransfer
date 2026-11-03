using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Feige.Core.Crypto;
using Feige.Core.Storage;

namespace Feige.Core.Conversations;

/// <summary>Persistent authenticated v4 peer channel. It deliberately has no v3 fallback.</summary>
public sealed class V4Connection : IAsyncDisposable
{
    private sealed record OfferEnvelope([property: JsonRequired] V4Offer Offer);
    private sealed record ProofEnvelope([property: JsonRequired] V4Offer Offer, [property: JsonRequired] byte[] Signature);
    /// <remarks><c>ResumeOffset</c> and <c>AlreadyComplete</c> are v6 fields and are omitted on older channels.</remarks>
    private sealed record Decision([property: JsonRequired] Guid TransferID, [property: JsonRequired] bool Accepted,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? ResumeOffset = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AlreadyComplete = null);
    private sealed record Digest([property: JsonRequired] Guid TransferID, [property: JsonRequired] byte[] Sha256);
    private sealed record ChunkHeader(Guid TransferID, ulong Offset);

    private readonly Stream _stream;
    private readonly V4RecordCipher _send;
    private readonly V4RecordCipher _receive;
    private readonly byte[] _transcriptDigest;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _fileSender = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<(byte Type, Guid Id), TaskCompletionSource<byte[]>> _responses = new();
    private readonly ConcurrentDictionary<(byte Type, Guid Id), Func<byte[], bool>> _responseValidators = new();
    private Guid? _outgoingGroupTransferID;
    private Guid? _outgoingGroupID;
    private byte[]? _outgoingGroupDigest;
    private ulong _outgoingGroupSize;
    private ulong _outgoingGroupOffset;
    private readonly Dictionary<Guid, FolderReceiveState> _folders = [];
    private int _running;
    private int _disposed;

    private V4Connection(Stream stream, V4Offer peer, byte[] digest, string initiatorFingerprint, byte[] sendKey, byte[] receiveKey)
    {
        _stream = stream;
        PeerName = peer.DeviceName;
        PeerFingerprint = V4Wire.Fingerprint(peer);
        ProtocolVersion = peer.Version;
        InitiatorFingerprint = initiatorFingerprint;
        _transcriptDigest = digest.ToArray();
        _send = new V4RecordCipher(sendKey, digest);
        _receive = new V4RecordCipher(receiveKey, digest);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
    }

    public string PeerName { get; }
    public string PeerFingerprint { get; }
    public string InitiatorFingerprint { get; }
    public byte[] TranscriptDigest => _transcriptDigest.ToArray();
    public int ProtocolVersion { get; }
    public bool SupportsDurableAbort => ProtocolVersion >= V4Offer.DurableAbortVersion;
    public bool SupportsResume => ProtocolVersion >= V4Offer.ResumableVersion;
    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Application group router receives authenticated, decrypted v4 group records.</summary>
    public Func<byte, byte[], Task>? GroupRecordReceived { get; set; }
    public Func<V3Text, Task>? TextReceived { get; set; }
    public Func<V3File, Task<bool>>? FileReceiving { get; set; }
    public Func<V3File, string, Task>? FileReceived { get; set; }
    public Action<V3File, long>? ReceiveProgress { get; set; }
    public Func<byte, byte[], Task>? GroupFileRecordReceived { get; set; }
    public Func<V4GroupFileManifest, Task<bool>>? GroupFileReceiving { get; set; }
    public Action<V4GroupFileManifest, long>? GroupFileReceiveProgress { get; set; }
    public Func<V4GroupFileManifest, string, Task>? GroupFileReceived { get; set; }
    public Func<string>? ReceiveDirectory { get; set; }
    public event EventHandler? Closed;

    public static Task<V4Connection> EstablishAsync(Stream stream, IdentityKey identity, bool initiator, string name,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default,
        ushort protocolVersion = V4Offer.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(approve);
        return EstablishAsync(stream, identity, initiator, name, (peerName, _, token) => approve(peerName, token),
            cancellationToken, protocolVersion);
    }

    /// <summary>The responder's approval receives the peer name and its authenticated fingerprint.</summary>
    public static async Task<V4Connection> EstablishAsync(Stream stream, IdentityKey identity, bool initiator, string name,
        Func<string, string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default,
        ushort protocolVersion = V4Offer.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(approve);
        byte[] ephemeral = X25519KeyAgreement.CreatePrivateKey();
        V4Connection? connection = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
            var ct = deadline.Token;
            if (protocolVersion is not (V4Offer.CurrentVersion or V4Offer.DurableAbortVersion or V4Offer.ResumableVersion))
                throw new InvalidDataException("不支持的群组协议版本。");
            V4Offer offer;
            V4Offer peer;
            if (initiator)
            {
                offer = CreateOffer(protocolVersion, 1, name, identity, ephemeral);
                // Start reading before writing: the transport may deliver the peer's answer only
                // once the caller awaits, so a blocking read would strand our own frame.
                Task<byte[]> reply = ReadFrameAsync(stream, 16384, TimeSpan.FromSeconds(15), ct);
                await WriteFrameAsync(stream, V4Wire.Encode(new OfferEnvelope(offer)), ct);
                peer = V4Wire.Decode<OfferEnvelope>(await reply).Offer;
            }
            else
            {
                peer = V4Wire.Decode<OfferEnvelope>(await ReadFrameAsync(stream, 16384, TimeSpan.FromSeconds(15), ct)).Offer;
                if (peer is null || peer.Role != 1)
                    throw new InvalidDataException("发起方握手信息无效。");
                peer.Validate();
                offer = CreateOffer(peer.Version, 2, name, identity, ephemeral);
                await WriteFrameAsync(stream, V4Wire.Encode(new OfferEnvelope(offer)), ct);
            }
            if (peer is null) throw new InvalidDataException("缺少对端设备信息。");
            peer.Validate();
            byte[] transcript = V4Wire.Transcript(offer, peer);
            await WriteFrameAsync(stream, V4Wire.Encode(new ProofEnvelope(offer, identity.Sign(transcript))), ct);
            var proof = V4Wire.Decode<ProofEnvelope>(await ReadFrameAsync(stream, 16384, TimeSpan.FromMinutes(5), ct));
            if (proof.Offer is null || !V4Wire.Encode(proof.Offer).AsSpan().SequenceEqual(V4Wire.Encode(peer)))
                throw new InvalidDataException("握手信息不一致。");
            IdentityKey.VerifyOrThrow(peer.IdentityPublicKeyDER, transcript, proof.Signature);

            byte[] digest = SHA256.HashData(transcript);
            byte[] shared = X25519KeyAgreement.Agree(ephemeral, peer.EphemeralPublicKey);
            try
            {
                string keyVersion = $"v{offer.Version}";
                byte[] i2r = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest,
                    System.Text.Encoding.UTF8.GetBytes($"feige-{keyVersion} initiator-to-responder"));
                byte[] r2i = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, digest,
                    System.Text.Encoding.UTF8.GetBytes($"feige-{keyVersion} responder-to-initiator"));
                string initiatorFingerprint = V4Wire.Fingerprint(offer.Role == 1 ? offer : peer);
                connection = new(stream, peer, digest, initiatorFingerprint, initiator ? i2r : r2i, initiator ? r2i : i2r);
                CryptographicOperations.ZeroMemory(i2r);
                CryptographicOperations.ZeroMemory(r2i);
            }
            finally { CryptographicOperations.ZeroMemory(shared); }

            using var consent = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            consent.CancelAfter(TimeSpan.FromMinutes(5));
            if (initiator)
            {
                var (type, payload) = await connection.ReadRecordAsync(TimeSpan.FromMinutes(5), consent.Token);
                if (type != 8 || payload.Length != 1 || payload[0] > 1) throw new InvalidDataException("连接回执无效。");
                if (payload[0] == 0) throw new ConnectionRejectedException();
            }
            else
            {
                bool accepted = await approve(peer.DeviceName, connection.PeerFingerprint, consent.Token).WaitAsync(consent.Token);
                await connection.WriteAsync(8, [accepted ? (byte)1 : (byte)0], consent.Token);
                if (!accepted) throw new InvalidOperationException("已拒绝连接。");
            }
            return connection;
        }
        catch
        {
            if (connection is not null) connection.Close(); else stream.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(ephemeral); }
    }

    public async Task SendTextAsync(string text, Guid? id = null, CancellationToken cancellationToken = default)
    {
        var message = new V3Text(id ?? Guid.NewGuid(), text);
        message.Validate();
        byte[] ack = await RequestAsync(9, V4Wire.Encode(message), 10, message.Id, TimeSpan.FromSeconds(30), cancellationToken);
        if (V4Wire.Decode<Guid>(ack) != message.Id) throw new InvalidDataException("文字回执不匹配。");
    }

    public Task SendGroupControlAsync(byte[] canonicalJson, CancellationToken cancellationToken = default)
    {
        EnsureCanonical(V4GroupWire.DecodeControl(canonicalJson), canonicalJson);
        return WriteGroupRecordAsync(11, canonicalJson, cancellationToken);
    }

    public Task SendGroupTextAckAsync(byte[] canonicalJson, CancellationToken cancellationToken = default)
    {
        EnsureCanonical(V4GroupWire.DecodeGroupTextAck(canonicalJson), canonicalJson);
        return WriteGroupRecordAsync(13, canonicalJson, cancellationToken);
    }

    public Task SendGroupFileDecisionAsync(byte[] canonicalJson, CancellationToken cancellationToken = default)
    {
        _ = V4GroupFileWire.DecodeDecision(canonicalJson);
        return WriteAsync(15, canonicalJson, cancellationToken);
    }

    public Task SendGroupFileReceiptAsync(byte[] canonicalJson, CancellationToken cancellationToken = default)
    {
        _ = V4GroupFileWire.DecodeReceipt(canonicalJson);
        return WriteAsync(16, canonicalJson, cancellationToken);
    }

    public async Task<byte[]> SendGroupTextAsync(byte[] canonicalJson, Guid messageID, CancellationToken cancellationToken = default)
    {
        GroupMessageEnvelope envelope = V4GroupWire.DecodeGroupText(canonicalJson);
        EnsureCanonical(envelope, canonicalJson);
        if (envelope.Id != messageID) throw new InvalidDataException("群组记录关联 ID 不匹配。");
        byte[] response = await RequestAsync(12, canonicalJson, 13, messageID, TimeSpan.FromSeconds(30), cancellationToken,
            payload =>
            {
                GroupTextAck ack = V4GroupWire.DecodeGroupTextAck(payload);
                return ack.GroupID == envelope.GroupID && ack.MessageID == messageID && ack.Epoch == envelope.MemberRevision;
            });
        GroupTextAck result = V4GroupWire.DecodeGroupTextAck(response);
        if (!result.Accepted) throw new IOException("对方未接受群消息：" + result.Reason);
        return response;
    }

    public async Task<byte[]> SendGroupFileManifestAsync(byte[] canonicalJson, Guid transferID, CancellationToken cancellationToken = default)
    {
        V4GroupFileManifest manifest = V4GroupFileWire.DecodeManifest(canonicalJson);
        if (manifest.Metadata.TransferID != transferID) throw new InvalidDataException("群文件 manifest 关联 ID 不匹配。");
        if (!await _fileSender.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有文件正在发送。");
        bool keepSlot = false;
        try
        {
            byte[] response = await RequestAsync(14, canonicalJson, 15, transferID, TimeSpan.FromMinutes(2),
                cancellationToken, payload =>
                {
                    V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(payload);
                    return decision.GroupID == manifest.GroupID && decision.TransferID == transferID;
                });
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(response);
            if (decision.Accepted)
            {
                _outgoingGroupTransferID = transferID;
                _outgoingGroupID = manifest.GroupID;
                _outgoingGroupDigest = manifest.Sha256;
                _outgoingGroupSize = manifest.Metadata.Size;
                _outgoingGroupOffset = 0;
                keepSlot = true;
            }
            return response;
        }
        catch { Close(); throw; }
        finally { if (!keepSlot) _fileSender.Release(); }
    }

    public async Task SendGroupFileChunkAsync(Guid transferID, ulong offset, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (_outgoingGroupTransferID != transferID || _outgoingGroupID is null)
            throw new InvalidOperationException("群文件传输尚未获准或传输 ID 不匹配。");
        if (transferID == Guid.Empty || bytes.IsEmpty || bytes.Length > V4Wire.MaxPlaintext - 24 ||
            offset != _outgoingGroupOffset || (ulong)bytes.Length > _outgoingGroupSize - _outgoingGroupOffset)
            throw new InvalidDataException("群文件块长度、偏移或传输 ID 无效。");
        byte[] payload = new byte[24 + bytes.Length];
        transferID.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(16, 8), offset);
        bytes.Span.CopyTo(payload.AsSpan(24));
        await WriteAsync(3, payload, cancellationToken);
        _outgoingGroupOffset += (ulong)bytes.Length;
    }

    public async Task<byte[]> SendGroupFileFinishAsync(byte[] canonicalJson, Guid transferID, CancellationToken cancellationToken = default)
    {
        if (_outgoingGroupTransferID != transferID || _outgoingGroupID is not Guid groupID ||
            _outgoingGroupDigest is not { Length: 32 } expectedDigest || _outgoingGroupOffset != _outgoingGroupSize)
            throw new InvalidOperationException("群文件传输尚未获准或传输 ID 不匹配。");
        try
        {
            EnsureCorrelationID(canonicalJson, "transferID", transferID);
            Digest finish = V4Wire.Decode<Digest>(canonicalJson);
            if (finish.TransferID != transferID || finish.Sha256 is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(finish.Sha256, expectedDigest))
                throw new InvalidDataException("群文件完成摘要与 manifest 不一致。");
            return await RequestAsync(7, canonicalJson, 16, transferID, TimeSpan.FromMinutes(5),
                cancellationToken, payload =>
                {
                    V4GroupFileReceipt receipt = V4GroupFileWire.DecodeReceipt(payload);
                    return receipt.GroupID == groupID && receipt.TransferID == transferID &&
                        CryptographicOperations.FixedTimeEquals(receipt.Sha256, expectedDigest);
                });
        }
        catch { Close(); throw; }
        finally
        {
            _outgoingGroupTransferID = null;
            _outgoingGroupID = null;
            _outgoingGroupDigest = null;
            _outgoingGroupSize = 0;
            _outgoingGroupOffset = 0;
            _fileSender.Release();
        }
    }

    private Task WriteGroupRecordAsync(byte type, byte[] canonicalJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        if (canonicalJson.Length == 0 || canonicalJson.Length > V4Wire.MaxPlaintext)
            throw new InvalidDataException("群组 JSON 长度无效。");
        using var document = System.Text.Json.JsonDocument.Parse(canonicalJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("群组记录必须是 JSON 对象。");
        return WriteAsync(type, canonicalJson, cancellationToken);
    }

    private static void EnsureCanonical<T>(T value, byte[] payload)
    {
        byte[] canonical = V4GroupWire.EncodeCanonical(value);
        if (!canonical.AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("群组记录不是规范 JSON 编码。");
    }

    private static void EnsureCorrelationID(byte[] payload, string property, Guid expected)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(property, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String ||
            !value.TryGetGuid(out var actual) || actual != expected)
            throw new InvalidDataException("群组记录关联 ID 不匹配。");
    }

    public async Task<V3File> SendFileAsync(string path, string kind = "file", Guid? id = null,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _fileSender.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有文件正在发送。");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("找不到要发送的文件。", path);
            Guid? resumeID = SupportsResume ? ResumeIdentifiers.ResumeID(path, info.Length, info.LastWriteTimeUtc) : null;
            var metadata = new V3File(id ?? Guid.NewGuid(), Path.GetFileName(path), info.Length, kind, resumeID);
            await SendOneAsync(path, metadata, progress, cancellationToken);
            return metadata;
        }
        finally { _fileSender.Release(); }
    }

    /// <summary>
    /// v6 only. Sends every regular file below <paramref name="folder"/> in order under one transfer ID.
    /// <paramref name="progress"/> reports bytes of the whole folder, including files the receiver already had.
    /// </summary>
    public async Task<FolderSendResult> SendFolderAsync(string folder, Guid? id = null,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!SupportsResume) throw new InvalidOperationException("对方版本不支持发送文件夹。");
        if (!await _fileSender.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有文件正在发送。");
        try
        {
            IReadOnlyList<FolderEntry> entries = FolderEnumerator.Entries(folder);
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string rootName = Path.GetFileName(full);
            Guid folderID = ResumeIdentifiers.FolderID(full);
            Guid transferID = id ?? Guid.NewGuid();
            long total = entries.Sum(entry => entry.Size);
            long done = 0;
            int skipped = 0;
            foreach (FolderEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new V6FolderInfo(rootName, entry.RelativePath, folderID, entries.Count, (ulong)total);
                var metadata = new V3File(transferID, Path.GetFileName(entry.Path), entry.Size, "file",
                    ResumeIdentifiers.ResumeID(entry.Path, entry.Size, entry.ModifiedUtc), info);
                long start = done;
                if (await SendOneAsync(entry.Path, metadata, (count, _) => progress?.Invoke(start + count, total), cancellationToken))
                    skipped++;
                done += entry.Size;
                progress?.Invoke(done, total);
            }
            return new FolderSendResult(rootName, entries.Count, total, skipped);
        }
        finally { _fileSender.Release(); }
    }

    /// <summary>Returns true when the receiver reported the file as already complete.</summary>
    private async Task<bool> SendOneAsync(string path, V3File metadata, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var ct = linked.Token;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != metadata.Size) throw new IOException("源文件在发送时发生变化。");
        metadata.Validate();
        Decision decision;
        try
        {
            byte[] response = await RequestAsync(1, V3Wire.Encode(metadata), 2, metadata.TransferID,
                TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10), ct);
            decision = V3Wire.Decode<Decision>(response);
        }
        catch { Close(); throw; }
        if (decision.TransferID != metadata.TransferID) { Close(); throw new InvalidDataException("文件接收决定 ID 无效。"); }
        if (!decision.Accepted) { Close(); throw new IOException("已拒绝接收文件。"); }
        ulong resumeOffset = decision.ResumeOffset ?? 0;
        if (!SupportsResume && (decision.ResumeOffset is not null || decision.AlreadyComplete is not null) ||
            resumeOffset > (ulong)metadata.Size || decision.AlreadyComplete == true && metadata.Folder is null)
        {
            Close();
            throw new InvalidDataException("文件接收决定无效。");
        }
        if (decision.AlreadyComplete == true) return true;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65512]; long offset = 0;
            // The finish digest always covers the whole file, so the skipped prefix is hashed locally.
            while (offset < (long)resumeOffset)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, (long)resumeOffset - offset)), ct);
                if (read == 0) throw new EndOfStreamException("源文件在发送时发生变化。");
                hash.AppendData(buffer, 0, read); offset += read;
            }
            progress?.Invoke(offset, metadata.Size);
            while (offset < metadata.Size)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, metadata.Size - offset)), ct);
                if (read == 0) throw new EndOfStreamException("源文件在发送时发生变化。");
                byte[] chunk = new byte[24 + read];
                metadata.TransferID.TryWriteBytes(chunk.AsSpan(0, 16), bigEndian: true, out _);
                BinaryPrimitives.WriteUInt64BigEndian(chunk.AsSpan(16, 8), (ulong)offset);
                buffer.AsSpan(0, read).CopyTo(chunk.AsSpan(24));
                await WriteAsync(3, chunk, ct);
                hash.AppendData(buffer, 0, read); offset += read; progress?.Invoke(offset, metadata.Size);
            }
            var finish = new Digest(metadata.TransferID, hash.GetHashAndReset());
            byte[] receiptBytes = await RequestAsync(7, V3Wire.Encode(finish), 4, metadata.TransferID,
                TimeSpan.FromMinutes(5), ct);
            var receipt = V3Wire.Decode<Digest>(receiptBytes);
            if (receipt.TransferID != metadata.TransferID || receipt.Sha256 is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(receipt.Sha256, finish.Sha256))
                throw new InvalidDataException("文件回执摘要不一致。");
            return false;
        }
        catch { Close(); throw; }
    }

    public Task RunAsync(CancellationToken cancellationToken = default) => RunCoreAsync(cancellationToken);

    public Task RunAsync(Func<string> receiveDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiveDirectory);
        ReceiveDirectory = receiveDirectory;
        return RunCoreAsync(cancellationToken);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("V4 read loop already started.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        TransferFileSink? sink = null;
        ResumableFileSink? resumable = null;
        FolderReceiveState? incomingFolder = null;
        V3File? incoming = null;
        V4GroupFileManifest? groupIncoming = null;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var (type, payload) = await ReadRecordAsync(incoming is null && groupIncoming is null
                    ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(15), linked.Token);
                switch (type)
                {
                    case 1:
                    {
                        if (incoming is not null || groupIncoming is not null) throw new InvalidDataException("已有文件正在接收。");
                        incoming = V3Wire.Decode<V3File>(payload); incoming.Validate();
                        if (!SupportsResume && (incoming.ResumeID is not null || incoming.Folder is not null))
                            throw new InvalidDataException("此连接不支持续传或文件夹。");
                        FolderReceiveState? knownFolder = incoming.Folder is { } declared && _folders.TryGetValue(declared.FolderID, out var found) ? found : null;
                        if (knownFolder is not null)
                        {
                            // Later files of an accepted folder stay within its declared limits without another prompt.
                            V6FolderInfo declaredFolder = incoming.Folder!;
                            if (!declaredFolder.SharesFolder(knownFolder.Info) || knownFolder.AcceptedFiles >= declaredFolder.FileCount ||
                                (ulong)incoming.Size > declaredFolder.TotalBytes - knownFolder.AcceptedBytes)
                                throw new InvalidDataException("文件夹内容超出了已同意的范围。");
                        }
                        if (incoming.ResumeID is Guid resumeID && ReceiveDirectory is not null)
                        {
                            bool approved = knownFolder is not null;
                            if (!approved && FileReceiving is not null)
                            {
                                try { approved = await FileReceiving(incoming).WaitAsync(TimeSpan.FromMinutes(2), linked.Token); }
                                catch (TimeoutException) { approved = false; }
                                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { approved = false; }
                            }
                            ulong? offset = null;
                            bool complete = false;
                            if (approved)
                            {
                                try
                                {
                                    string directory = ReceiveDirectory();
                                    if (incoming.Folder is { } info && knownFolder is null)
                                        _folders[info.FolderID] = knownFolder = FolderReceiveState.Open(info, directory, PeerFingerprint);
                                    if (knownFolder is not null)
                                    {
                                        knownFolder.AcceptedFiles++;
                                        knownFolder.AcceptedBytes += (ulong)incoming.Size;
                                        complete = knownFolder.IsCompleted(resumeID);
                                    }
                                    if (!complete)
                                    {
                                        string target = knownFolder is null ? directory : knownFolder.ParentDirectory(incoming.Folder!);
                                        resumable = await ResumableFileSink.OpenAsync(directory, ResumeIdentifiers.StorageKey(PeerFingerprint, resumeID),
                                            incoming.Size, target, incoming.FileName, linked.Token);
                                        offset = (ulong)resumable.ExistingLength;
                                    }
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Protocol.ProtocolException)
                                { approved = false; }
                            }
                            await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, approved, approved ? offset ?? 0 : null,
                                complete ? true : null)), linked.Token);
                            if (!approved)
                            {
                                incoming = null;
                                Close();
                                throw new IOException("已拒绝接收文件。");
                            }
                            if (complete)
                            {
                                knownFolder!.FinishedBytes += (ulong)incoming.Size;
                                ReceiveProgress?.Invoke(incoming, (long)knownFolder.FinishedBytes);
                                await FinishFolderIfCompleteAsync(knownFolder, incoming);
                                incoming = null;
                                break;
                            }
                            incomingFolder = knownFolder;
                            ReceiveProgress?.Invoke(incoming, (long)(knownFolder?.FinishedBytes ?? 0) + resumable!.Written);
                            break;
                        }
                        bool accepted = false;
                        if (FileReceiving is not null && ReceiveDirectory is not null)
                        {
                            try { accepted = await FileReceiving(incoming).WaitAsync(TimeSpan.FromMinutes(2), linked.Token); }
                            catch (TimeoutException) { accepted = false; }
                            catch (OperationCanceledException) when (!linked.IsCancellationRequested) { accepted = false; }
                        }
                        if (accepted)
                        {
                            try { sink = TransferFileSink.CreateForSession(ReceiveDirectory!(), incoming.FileName, incoming.Size); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                            { accepted = false; }
                        }
                        await WriteAsync(2, V3Wire.Encode(new Decision(incoming.TransferID, accepted)), linked.Token);
                        if (!accepted)
                        {
                            incoming = null;
                            Close();
                            throw new IOException("已拒绝接收文件。");
                        }
                        ReceiveProgress?.Invoke(incoming, 0);
                        break;
                    }
                    case 14:
                    {
                        if (incoming is not null || groupIncoming is not null) throw new InvalidDataException("已有文件正在接收。");
                        V4GroupFileManifest manifest = V4GroupFileWire.DecodeManifest(payload);
                        if (!StringComparer.Ordinal.Equals(manifest.SenderFingerprint, PeerFingerprint))
                            throw new InvalidDataException("群文件发送者与已认证连接身份不匹配。");
                        bool accepted = false;
                        string reason = "declined";
                        if (GroupFileReceiving is not null && ReceiveDirectory is not null)
                        {
                            try { accepted = await GroupFileReceiving(manifest).WaitAsync(TimeSpan.FromMinutes(2), linked.Token); }
                            catch (TimeoutException) { accepted = false; }
                            catch (OperationCanceledException) when (!linked.IsCancellationRequested) { accepted = false; }
                            if (accepted)
                            {
                                try { sink = TransferFileSink.CreateForSession(ReceiveDirectory(), manifest.Metadata.FileName, checked((long)manifest.Metadata.Size)); }
                                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
                                { accepted = false; reason = "unavailable"; }
                            }
                        }
                        if (accepted)
                        {
                            groupIncoming = manifest;
                            GroupFileReceiveProgress?.Invoke(manifest, 0);
                        }
                        await SendGroupFileDecisionAsync(V4GroupFileWire.EncodeDecision(
                            new V4GroupFileDecision(manifest.GroupID, manifest.Metadata.TransferID, accepted, accepted ? null : reason)), linked.Token);
                        break;
                    }
                    case 2:
                    case 4:
                    case 10:
                    case 13:
                    case 15:
                    case 16:
                    {
                        Guid id = type switch
                        {
                            2 => V3Wire.Decode<Decision>(payload).TransferID,
                            4 => V3Wire.Decode<Digest>(payload).TransferID,
                            10 => V4Wire.Decode<Guid>(payload),
                            13 => V4GroupWire.DecodeGroupTextAck(payload).MessageID,
                            15 => V4GroupFileWire.DecodeDecision(payload).TransferID,
                            16 => V4GroupFileWire.DecodeReceipt(payload).TransferID,
                            _ => throw new InvalidDataException("回执类型无效。"),
                        };
                        var responseKey = (type, id);
                        if (_responseValidators.TryGetValue(responseKey, out Func<byte[], bool>? validate) && !validate(payload))
                            throw new InvalidDataException("回执内容与待处理群消息不匹配。");
                        if (!_responses.TryRemove(responseKey, out var waiter)) throw new InvalidDataException("收到未知回执。");
                        waiter.TrySetResult(payload);
                        break;
                    }
                    case 3:
                    {
                        if (groupIncoming is not null && sink is not null)
                        {
                            if (payload.Length <= 24) throw new InvalidDataException("群文件块长度无效。");
                            var groupTransferID = new Guid(payload.AsSpan(0, 16), bigEndian: true);
                            ulong groupOffset = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(16, 8));
                            if (groupTransferID != groupIncoming.Metadata.TransferID || groupOffset != (ulong)sink.Written)
                                throw new InvalidDataException("群文件块偏移或 ID 无效。");
                            await sink.WriteAsync(payload.AsMemory(24), linked.Token);
                            GroupFileReceiveProgress?.Invoke(groupIncoming, sink.Written);
                            break;
                        }
                        if (incoming is not null && resumable is not null)
                        {
                            if (payload.Length <= 24) throw new InvalidDataException("文件块长度无效。");
                            var resumeTransferID = new Guid(payload.AsSpan(0, 16), bigEndian: true);
                            ulong resumeOffset = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(16, 8));
                            if (resumeTransferID != incoming.TransferID || resumeOffset != (ulong)resumable.Written)
                                throw new InvalidDataException("文件块偏移或 ID 无效。");
                            await resumable.WriteAsync(payload.AsMemory(24), linked.Token);
                            ReceiveProgress?.Invoke(incoming, (long)(incomingFolder?.FinishedBytes ?? 0) + resumable.Written);
                            break;
                        }
                        if (incoming is null || sink is null)
                        {
                            await (GroupFileRecordReceived ?? throw new InvalidOperationException("未配置群文件块验证器。"))(type, payload);
                            break;
                        }
                        if (payload.Length <= 24) throw new InvalidDataException("文件块长度无效。");
                        var transferID = new Guid(payload.AsSpan(0, 16), bigEndian: true);
                        ulong offset = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(16, 8));
                        if (transferID != incoming.TransferID || offset != (ulong)sink.Written) throw new InvalidDataException("文件块偏移或 ID 无效。");
                        await sink.WriteAsync(payload.AsMemory(24), linked.Token);
                        ReceiveProgress?.Invoke(incoming, sink.Written);
                        break;
                    }
                    case 7:
                    {
                        if (groupIncoming is not null && sink is not null)
                        {
                            Digest groupFinish = V4Wire.Decode<Digest>(payload);
                            if (groupFinish.TransferID != groupIncoming.Metadata.TransferID || groupFinish.Sha256 is not { Length: 32 } ||
                                !CryptographicOperations.FixedTimeEquals(groupFinish.Sha256, groupIncoming.Sha256))
                                throw new InvalidDataException("群文件完成信息与 manifest 不匹配。");
                            string groupFinalPath = await sink.CommitAsync(groupFinish.Sha256, linked.Token);
                            GroupFileReceiveProgress?.Invoke(groupIncoming, sink.Written);
                            if (GroupFileReceived is not null) await GroupFileReceived(groupIncoming, groupFinalPath);
                            await SendGroupFileReceiptAsync(V4GroupFileWire.EncodeReceipt(
                                new V4GroupFileReceipt(groupIncoming.GroupID, groupFinish.TransferID, groupFinish.Sha256)), linked.Token);
                            await sink.DisposeAsync(); sink = null; groupIncoming = null;
                            break;
                        }
                        if (incoming is not null && resumable is not null)
                        {
                            var resumedFinish = V3Wire.Decode<Digest>(payload);
                            if (resumedFinish.TransferID != incoming.TransferID || resumedFinish.Sha256 is not { Length: 32 })
                                throw new InvalidDataException("文件完成信息无效。");
                            string resumedPath = await resumable.CommitAsync(resumedFinish.Sha256, linked.Token);
                            await resumable.DisposeAsync(); resumable = null;
                            if (incomingFolder is not null && incoming.ResumeID is Guid finishedID)
                            {
                                incomingFolder.MarkCompleted(finishedID);
                                incomingFolder.FinishedBytes += (ulong)incoming.Size;
                                ReceiveProgress?.Invoke(incoming, (long)incomingFolder.FinishedBytes);
                                await WriteAsync(4, V3Wire.Encode(resumedFinish), linked.Token);
                                await FinishFolderIfCompleteAsync(incomingFolder, incoming);
                            }
                            else
                            {
                                ReceiveProgress?.Invoke(incoming, incoming.Size);
                                if (FileReceived is not null) await FileReceived(incoming, resumedPath);
                                await WriteAsync(4, V3Wire.Encode(resumedFinish), linked.Token);
                            }
                            incoming = null; incomingFolder = null;
                            break;
                        }
                        if (incoming is null || sink is null)
                        {
                            await (GroupFileRecordReceived ?? throw new InvalidOperationException("未配置群文件完成验证器。"))(type, payload);
                            break;
                        }
                        var finish = V3Wire.Decode<Digest>(payload);
                        if (finish.TransferID != incoming.TransferID || finish.Sha256 is not { Length: 32 })
                            throw new InvalidDataException("文件完成信息无效。");
                        string finalPath = await sink.CommitAsync(finish.Sha256, linked.Token);
                        ReceiveProgress?.Invoke(incoming, sink.Written);
                        if (FileReceived is not null) await FileReceived(incoming, finalPath);
                        await WriteAsync(4, V3Wire.Encode(finish), linked.Token);
                        await sink.DisposeAsync(); sink = null; incoming = null;
                        break;
                    }
                    case 9:
                    {
                        var text = V4Wire.Decode<V3Text>(payload); text.Validate();
                        await (TextReceived ?? throw new InvalidOperationException("未配置文字持久化处理器。"))(text);
                        await WriteAsync(10, V4Wire.Encode(text.Id), linked.Token);
                        break;
                    }
                    case 11:
                    case 12:
                        await (GroupRecordReceived ?? throw new InvalidOperationException("未配置群组记录验证器。"))(type, payload);
                        break;
                    default:
                        throw new InvalidDataException("此 v4 连接暂不支持该记录类型。");
                }
            }
        }
        finally
        {
            if (sink is not null) await sink.DisposeAsync();
            if (resumable is not null) await resumable.DisposeAsync();
            Close();
        }
    }

    /// <summary>Reports a folder once, with its root directory, after its last declared file.</summary>
    private async Task FinishFolderIfCompleteAsync(FolderReceiveState folder, V3File last)
    {
        if (folder.AcceptedFiles != folder.Info.FileCount) return;
        folder.RemoveState();
        _folders.Remove(folder.Info.FolderID);
        if (FileReceived is not null) await FileReceived(last, folder.RootPath);
    }

    private async Task<byte[]> RequestAsync(byte type, byte[] payload, byte responseType, Guid id, TimeSpan timeout,
        CancellationToken ct, Func<byte[], bool>? responseValidator = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = (responseType, id);
        if (!_responses.TryAdd(key, completion)) throw new InvalidOperationException("消息已在发送。");
        if (responseValidator is not null && !_responseValidators.TryAdd(key, responseValidator))
        {
            _responses.TryRemove(key, out _);
            throw new InvalidOperationException("消息校验状态已存在。");
        }
        try
        {
            await WriteAsync(type, payload, linked.Token);
            return await completion.Task.WaitAsync(timeout, linked.Token);
        }
        catch (OperationCanceledException exception) when (_closed.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IOException("连接已关闭，未收到响应。", exception);
        }
        finally
        {
            _responses.TryRemove(key, out _);
            _responseValidators.TryRemove(key, out _);
        }
    }

    private async Task WriteAsync(byte type, byte[] payload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        await _writer.WaitAsync(linked.Token);
        try { await WriteFrameAsync(_stream, _send.Seal(type, payload), linked.Token); }
        finally { _writer.Release(); }
    }

    private async Task<(byte Type, byte[] Payload)> ReadRecordAsync(TimeSpan timeout, CancellationToken ct)
        => _receive.Open(await ReadFrameAsync(_stream, V4Wire.MaxFrameLength, timeout, ct));

    private static V4Offer CreateOffer(ushort version, byte role, string name, IdentityKey identity, byte[] ephemeral)
    {
        var offer = new V4Offer(version, role, name, identity.SubjectPublicKeyInfo.ToArray(),
            X25519KeyAgreement.GetPublicKey(ephemeral), RandomNumberGenerator.GetBytes(32), "");
        offer.Validate();
        return offer;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        if (payload.Length > V4Wire.MaxFrameLength) throw new InvalidDataException("帧长度无效。");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, deadline.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await stream.WriteAsync(payload, deadline.Token);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maximum, TimeSpan timeout, CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        TimeSpan frameTimeout = timeout;
        await ReadExactlyAsync(stream, prefix, frameTimeout, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 1 || length > maximum) throw new InvalidDataException("帧长度无效。");
        byte[] bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, frameTimeout, ct);
        return bytes;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, TimeSpan timeout, CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            int read = await stream.ReadAsync(bytes, deadline.Token);
            if (read == 0) throw new EndOfStreamException("连接已断开。");
            bytes = bytes[read..];
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel();
        _stream.Dispose();
        _send.Dispose();
        _receive.Dispose();
        CryptographicOperations.ZeroMemory(_transcriptDigest);
        foreach (var waiter in _responses.Values)
        {
            waiter.TrySetException(new IOException("连接已断开。"));
            _ = waiter.Task.Exception;
        }
        _responses.Clear();
        _responseValidators.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}

public sealed record FolderSendResult(string RootName, int FileCount, long TotalBytes, int SkippedFiles);
