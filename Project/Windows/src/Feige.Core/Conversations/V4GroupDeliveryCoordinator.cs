namespace Feige.Core.Conversations;

public enum V4GroupDeliveryStatus
{
    Local,
    Delivered,
    Undelivered,
    Declined,
    Failed
}

public sealed record V4GroupMemberDeliveryResult(string MemberFingerprint, V4GroupDeliveryStatus Status,
    string? Error = null);

public sealed record V4GroupDeliveryProgress(string MemberFingerprint, ulong BytesSent, ulong TotalBytes);

/// <summary>Persists group messages and fans them out independently over the currently registered v4 channels.</summary>
public sealed class V4GroupDeliveryCoordinator
{
    private const int FileChunkSize = V4Wire.MaxPlaintext - 24;
    private readonly V4GroupStore _store;
    private readonly PeerConnectionRegistry<V4Connection> _connections;
    private readonly string _localFingerprint;

    public V4GroupDeliveryCoordinator(V4GroupStore store,
        PeerConnectionRegistry<V4Connection> connections, string localFingerprint)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _localFingerprint = V4GroupWire.NormalizeFingerprint(localFingerprint);
    }

    /// <summary>Stores one text envelope durably, then sends it to each other member independently.</summary>
    public async Task<IReadOnlyList<V4GroupMemberDeliveryResult>> SendTextAsync(Guid groupID, string text,
        CancellationToken cancellationToken = default)
    {
        GroupConversation group = RequireLocalGroup(groupID);
        var message = new GroupMessageEnvelope(Guid.NewGuid(), group.Id, _localFingerprint, group.Revision,
            DateTimeOffset.UtcNow, GroupMessageKind.Text, text: text);
        EnsureNoPendingMembershipChange(groupID);
        _store.AddOutgoingMessage(message);

        GroupConversation? current = _store.GetGroup(groupID);
        if (HasPendingMembershipChange(groupID) || !HasSameRoster(group, current))
            return MembershipChangedResults(group, "群成员变更已开始或成员名单已变化，消息未发送。");

        byte[] payload = V4GroupWire.EncodeCanonical(message);
        IReadOnlyDictionary<string, V4Connection> connections = _connections.Snapshot();
        Task<V4GroupMemberDeliveryResult>[] sends = group.Members
            .Where(member => member.Fingerprint != _localFingerprint)
            .OrderBy(member => member.Fingerprint, StringComparer.Ordinal)
            .Select(member => SendTextToMemberAsync(member.Fingerprint, connections, payload, message.Id,
                cancellationToken))
            .ToArray();
        V4GroupMemberDeliveryResult[] results = await Task.WhenAll(sends).ConfigureAwait(false);
        return WithLocalResult(results);
    }

    /// <summary>Hashes and stores one file envelope, then transfers it independently to each other member.</summary>
    public async Task<IReadOnlyList<V4GroupMemberDeliveryResult>> SendFileAsync(Guid groupID, string path,
        V4GroupFileKind kind = V4GroupFileKind.File,
        Action<V4GroupDeliveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GroupConversation group = RequireLocalGroup(groupID);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (kind is not (V4GroupFileKind.File or V4GroupFileKind.Image))
            throw new ArgumentOutOfRangeException(nameof(kind));
        await using var sourceLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ulong size = checked((ulong)sourceLock.Length);
        byte[] digest = await System.Security.Cryptography.SHA256.HashDataAsync(sourceLock, cancellationToken)
            .ConfigureAwait(false);
        if (sourceLock.Length != checked((long)size))
            throw new IOException("文件长度在计算摘要期间发生变化。");

        Guid messageID = Guid.NewGuid();
        Guid localTransferID = Guid.NewGuid();
        string fileName = Path.GetFileName(path);
        var storedMetadata = new GroupFileMetadata(localTransferID, fileName, size, sha256: digest);
        fileName = storedMetadata.FileName;
        GroupMessageKind messageKind = kind == V4GroupFileKind.Image ? GroupMessageKind.Image : GroupMessageKind.File;
        var message = new GroupMessageEnvelope(messageID, group.Id, _localFingerprint, group.Revision,
            DateTimeOffset.UtcNow, messageKind, fileMetadata: storedMetadata);
        EnsureNoPendingMembershipChange(groupID);
        _store.AddOutgoingMessage(message);

        GroupConversation? current = _store.GetGroup(groupID);
        if (HasPendingMembershipChange(groupID) || !HasSameRoster(group, current))
            return MembershipChangedResults(group, "群成员变更已开始或成员名单已变化，消息未发送。");

        IReadOnlyDictionary<string, V4Connection> connections = _connections.Snapshot();
        Task<V4GroupMemberDeliveryResult>[] sends = group.Members
            .Where(member => member.Fingerprint != _localFingerprint)
            .OrderBy(member => member.Fingerprint, StringComparer.Ordinal)
            .Select(member =>
            {
                Guid recipientTransferID = Guid.NewGuid();
                var recipientMetadata = new V4GroupFileMetadata(recipientTransferID, fileName, size, kind);
                var manifest = new V4GroupFileManifest(group.Id, messageID, _localFingerprint, group.Revision,
                    recipientMetadata, digest);
                byte[] manifestBytes = V4GroupFileWire.EncodeManifest(manifest);
                byte[] finishBytes = V4Wire.Encode(new GroupFileFinish(recipientTransferID, digest));
                return SendFileToMemberAsync(member.Fingerprint, connections, path, manifestBytes,
                    finishBytes, group.Id, recipientTransferID, digest, size, progress, cancellationToken);
            })
            .ToArray();
        V4GroupMemberDeliveryResult[] results = await Task.WhenAll(sends).ConfigureAwait(false);
        return WithLocalResult(results);
    }

    private async Task<V4GroupMemberDeliveryResult> SendTextToMemberAsync(string fingerprint,
        IReadOnlyDictionary<string, V4Connection> connections, byte[] payload, Guid messageID,
        CancellationToken cancellationToken)
    {
        if (!TryGetAvailableConnection(fingerprint, connections, out V4Connection? connection))
            return Unavailable(fingerprint);
        try
        {
            await connection!.SendGroupTextAsync(payload, messageID, cancellationToken).ConfigureAwait(false);
            return new(fingerprint, V4GroupDeliveryStatus.Delivered);
        }
        catch (Exception exception)
        {
            return Failed(fingerprint, exception);
        }
    }

    private static async Task<V4GroupMemberDeliveryResult> SendFileToMemberAsync(string fingerprint,
        IReadOnlyDictionary<string, V4Connection> connections, string path, byte[] manifestBytes,
        byte[] finishBytes, Guid groupID, Guid transferID, byte[] digest, ulong size,
        Action<V4GroupDeliveryProgress>? progress, CancellationToken cancellationToken)
    {
        if (!TryGetAvailableConnection(fingerprint, connections, out V4Connection? connection))
            return Unavailable(fingerprint);

        try
        {
            byte[] decisionBytes = await connection!.SendGroupFileManifestAsync(manifestBytes, transferID,
                cancellationToken).ConfigureAwait(false);
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(decisionBytes);
            if (decision.GroupID != groupID || decision.TransferID != transferID)
                throw new InvalidDataException("群文件接收决定与 manifest 不匹配。");
            if (!decision.Accepted)
            {
                return StringComparer.Ordinal.Equals(decision.Reason, "declined")
                    ? new(fingerprint, V4GroupDeliveryStatus.Declined, decision.Reason)
                    : new(fingerprint, V4GroupDeliveryStatus.Failed, decision.Reason);
            }

            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[FileChunkSize];
            ulong offset = 0;
            ReportProgress(progress, fingerprint, 0, size);
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await connection!.SendGroupFileChunkAsync(transferID, offset, buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                offset = checked(offset + (ulong)read);
                ReportProgress(progress, fingerprint, offset, size);
            }
            if (offset != size || source.Length != checked((long)size))
                throw new IOException("文件长度在发送期间发生变化。");

            byte[] receiptBytes = await connection!.SendGroupFileFinishAsync(finishBytes, transferID,
                cancellationToken).ConfigureAwait(false);
            V4GroupFileReceipt receipt = V4GroupFileWire.DecodeReceipt(receiptBytes);
            if (receipt.GroupID != groupID || receipt.TransferID != transferID ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(receipt.Sha256, digest))
                throw new InvalidDataException("群文件完成回执与 manifest 不匹配。");
            return new(fingerprint, V4GroupDeliveryStatus.Delivered);
        }
        catch (Exception exception)
        {
            return Failed(fingerprint, exception);
        }
    }

    private GroupConversation RequireLocalGroup(Guid groupID)
    {
        GroupConversation group = _store.GetGroup(groupID)
            ?? throw new InvalidOperationException("群组不存在。");
        if (!group.Members.Any(member => member.Fingerprint == _localFingerprint))
            throw new InvalidOperationException("本机不是当前群组成员。");
        EnsureNoPendingMembershipChange(groupID);
        return group;
    }

    private void EnsureNoPendingMembershipChange(Guid groupID)
    {
        if (HasPendingMembershipChange(groupID))
            throw new InvalidOperationException("群成员变更正在处理中，暂不能发送群消息。");
    }

    private bool HasPendingMembershipChange(Guid groupID)
        => _store.GetPendingTransaction(groupID) is not null ||
           _store.PendingMembershipPrepares.Any(prepare => prepare.GroupID == groupID);

    private static bool TryGetAvailableConnection(string fingerprint,
        IReadOnlyDictionary<string, V4Connection> connections, out V4Connection? connection)
    {
        string registryFingerprint = PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint);
        if (connections.TryGetValue(registryFingerprint, out connection) && !connection.IsClosed &&
            connection.ProtocolVersion >= 4)
            return true;
        connection = null;
        return false;
    }

    private IReadOnlyList<V4GroupMemberDeliveryResult> MembershipChangedResults(GroupConversation group, string error)
    {
        V4GroupMemberDeliveryResult[] remoteResults = group.Members
            .Where(member => member.Fingerprint != _localFingerprint)
            .OrderBy(member => member.Fingerprint, StringComparer.Ordinal)
            .Select(member => new V4GroupMemberDeliveryResult(member.Fingerprint,
                V4GroupDeliveryStatus.Undelivered, error))
            .ToArray();
        return WithLocalResult(remoteResults);
    }

    private IReadOnlyList<V4GroupMemberDeliveryResult> WithLocalResult(
        IEnumerable<V4GroupMemberDeliveryResult> remoteResults)
        => Array.AsReadOnly(new[] { new V4GroupMemberDeliveryResult(_localFingerprint, V4GroupDeliveryStatus.Local) }
            .Concat(remoteResults).ToArray());

    private static bool HasSameRoster(GroupConversation original, GroupConversation? current)
        => current is not null && current.Revision == original.Revision &&
           current.Members.Select(member => member.Fingerprint).Order(StringComparer.Ordinal)
               .SequenceEqual(original.Members.Select(member => member.Fingerprint).Order(StringComparer.Ordinal),
                   StringComparer.Ordinal);

    private static V4GroupMemberDeliveryResult Unavailable(string fingerprint)
        => new(fingerprint, V4GroupDeliveryStatus.Undelivered, "当前没有可用的 v4 连接。");

    private static V4GroupMemberDeliveryResult Failed(string fingerprint, Exception exception)
        => new(fingerprint, V4GroupDeliveryStatus.Failed, exception.Message);

    private static void ReportProgress(Action<V4GroupDeliveryProgress>? callback, string fingerprint,
        ulong bytesSent, ulong totalBytes)
    {
        try { callback?.Invoke(new(fingerprint, bytesSent, totalBytes)); }
        catch { /* Progress observers cannot interrupt a transfer. */ }
    }

    private sealed record GroupFileFinish(Guid TransferID, byte[] Sha256);
}
