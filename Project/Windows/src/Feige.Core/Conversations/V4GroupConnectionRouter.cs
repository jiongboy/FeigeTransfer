using System.Runtime.CompilerServices;

namespace Feige.Core.Conversations;

/// <summary>
/// Attaches one authenticated v4 channel to durable group state and routes its incoming
/// group controls, text, and file transfers. This type has no UI dependencies.
/// </summary>
public sealed class V4GroupConnectionRouter
{
    private sealed class FileMessageLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class FileMessageLockLease(V4GroupConnectionRouter router,
        (Guid GroupID, Guid MessageID) key, FileMessageLockEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                router.ReleaseFileMessageLock(key, entry);
        }
    }

    private sealed record ReadLoopRegistration(V4GroupConnectionRouter Router, Task Completion);

    private static readonly ConditionalWeakTable<V4Connection, ReadLoopRegistration> ReadLoops = new();
    private static readonly object ReadLoopLock = new();

    private readonly V4GroupStore _store;
    private readonly string _localFingerprint;
    private readonly object _fileMessageLockSync = new();
    private readonly Dictionary<(Guid GroupID, Guid MessageID), FileMessageLockEntry> _fileMessageLocks = new();

    public V4GroupConnectionRouter(V4GroupStore store, string localFingerprint)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localFingerprint = V4GroupWire.NormalizeFingerprint(localFingerprint);
    }

    /// <summary>Called before an incoming invite is passed to <see cref="V4GroupStore.AcceptInvitation"/>.</summary>
    public Func<V4Connection, GroupControlMessage, CancellationToken, Task<bool>>? InvitationApproval { get; set; }

    /// <summary>Called after the requester and current group owner are authenticated.</summary>
    public Func<V4Connection, GroupControlMessage, CancellationToken, Task<bool>>? LeaveApproval { get; set; }

    /// <summary>Called only after the manifest sender, group, membership, and revision are validated.</summary>
    public Func<V4Connection, V4GroupFileManifest, CancellationToken, Task<bool>>? GroupFileApproval { get; set; }

    /// <summary>Called after a committed membership roster has been stored and acknowledged.</summary>
    public Func<V4Connection, GroupConversation, Task>? MembershipCommitted { get; set; }

    /// <summary>Called after a new group text envelope has been durably stored.</summary>
    public Func<V4Connection, GroupMessageEnvelope, Task>? TextReceived { get; set; }

    /// <summary>Called after a received file is complete and its message envelope is durably stored.</summary>
    public Func<V4Connection, GroupMessageEnvelope, string, Task>? FileReceived { get; set; }

    /// <summary>Called for validated group file receive progress reported by the v4 transport.</summary>
    public Action<V4Connection, V4GroupFileManifest, long>? GroupFileReceiveProgress { get; set; }

    /// <summary>Called for typed invitation and membership replies, including rejected replies.</summary>
    public Func<V4Connection, string, GroupControlMessage, Task>? ControlReplyReceived { get; set; }

    /// <summary>
    /// Installs this router's handlers and starts the connection's sole read loop. Repeated calls
    /// for the same connection and router return the original loop task; another router is rejected.
    /// </summary>
    public Task RunAsync(V4Connection connection, Func<string> receiveDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(receiveDirectory);

        lock (ReadLoopLock)
        {
            if (ReadLoops.TryGetValue(connection, out ReadLoopRegistration? registration))
            {
                if (ReferenceEquals(registration.Router, this)) return registration.Completion;
                throw new InvalidOperationException("该 v4 连接已由另一个群路由器接管。");
            }

            connection.GroupRecordReceived = (type, payload) => RouteGroupRecordAsync(connection, type, payload, cancellationToken);
            connection.GroupFileReceiving = manifest => ApproveGroupFileAsync(connection, manifest, cancellationToken);
            connection.GroupFileReceived = (manifest, path) => ReceiveGroupFileAsync(connection, manifest, path);
            connection.GroupFileReceiveProgress = (manifest, bytesReceived) =>
                ForwardGroupFileProgress(connection, manifest, bytesReceived);
            connection.GroupFileRecordReceived = (_, _) =>
                Task.FromException(new InvalidDataException("没有对应 manifest 的群文件记录。"));

            Task loop = connection.RunAsync(receiveDirectory, cancellationToken);
            ReadLoops.Add(connection, new ReadLoopRegistration(this, loop));
            return loop;
        }
    }

    private async Task RouteGroupRecordAsync(V4Connection connection, byte type, byte[] payload,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case 11:
            {
                GroupControlMessage control = V4GroupWire.DecodeControl(payload);
                EnsureCanonical(control, payload);
                await RouteControlAsync(connection, control, cancellationToken).ConfigureAwait(false);
                break;
            }
            case 12:
                await ReceiveGroupTextAsync(connection, payload).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("群路由器收到不支持的记录类型。");
        }
    }

    private async Task RouteControlAsync(V4Connection connection, GroupControlMessage control,
        CancellationToken cancellationToken)
    {
        if ((control.Kind is GroupControlKind.MembershipAbort or GroupControlKind.MembershipAbortReply) &&
            !connection.SupportsDurableAbort)
            throw new InvalidDataException("membershipAbort controls require an authenticated v5 connection.");
        switch (control.Kind)
        {
            case GroupControlKind.Invite:
            {
                EnsureAuthenticatedOwner(connection, control);
                bool accepted = false;
                Func<V4Connection, GroupControlMessage, CancellationToken, Task<bool>>? approval = InvitationApproval;
                if (approval is not null)
                {
                    try
                    {
                        accepted = await approval(connection, control, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { accepted = false; }
                }
                GroupControlMessage reply = _store.AcceptInvitation(control, _localFingerprint, accepted);
                await SendControlAsync(connection, reply, cancellationToken).ConfigureAwait(false);
                break;
            }
            case GroupControlKind.MembershipPrepare:
            {
                EnsureAuthenticatedOwner(connection, control);
                GroupControlMessage reply = _store.PrepareMembership(control, _localFingerprint);
                await SendControlAsync(connection, reply, cancellationToken).ConfigureAwait(false);
                break;
            }
            case GroupControlKind.MembershipCommit:
            {
                EnsureAuthenticatedOwner(connection, control);
                GroupControlMessage reply = _store.CommitMembership(control, _localFingerprint);
                await SendControlAsync(connection, reply, cancellationToken).ConfigureAwait(false);
                if (reply.Accepted == true && _store.GetGroup(control.GroupID) is { } committedGroup && MembershipCommitted is { } committed)
                    await committed(connection, committedGroup).ConfigureAwait(false);
                break;
            }
            case GroupControlKind.MembershipAbort:
            {
                EnsureAuthenticatedOwner(connection, control);
                bool accepted = _store.AbortMembership(control, _localFingerprint);
                var reply = new GroupControlMessage(GroupControlKind.MembershipAbortReply, control.GroupID,
                    control.OwnerFingerprint, control.Epoch, control.EventID,
                    targetFingerprint: _localFingerprint, accepted: accepted);
                await SendControlAsync(connection, reply, cancellationToken).ConfigureAwait(false);
                break;
            }
            case GroupControlKind.Leave:
            {
                EnsureLeaveRequest(connection, control);
                bool accepted = false;
                Func<V4Connection, GroupControlMessage, CancellationToken, Task<bool>>? approval = LeaveApproval;
                if (approval is not null)
                {
                    try { accepted = await approval(connection, control, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { accepted = false; }
                }
                var reply = new GroupControlMessage(GroupControlKind.LeaveReply, control.GroupID,
                    _localFingerprint, control.Epoch, control.EventID,
                    targetFingerprint: connection.PeerFingerprint, accepted: accepted);
                await SendControlAsync(connection, reply, cancellationToken).ConfigureAwait(false);
                break;
            }
            case GroupControlKind.LeaveReply:
                EnsureLeaveReplyFromOwner(connection, control);
                await NotifyControlReplyAsync(connection, control).ConfigureAwait(false);
                break;
            case GroupControlKind.InviteReply:
                EnsureReplyFromTarget(connection, control);
                await NotifyControlReplyAsync(connection, control).ConfigureAwait(false);
                break;
            case GroupControlKind.MembershipPrepareReply:
                EnsureReplyFromTarget(connection, control);
                _store.RecordPrepareReply(control, connection.PeerFingerprint);
                await NotifyControlReplyAsync(connection, control).ConfigureAwait(false);
                break;
            case GroupControlKind.MembershipAbortReply:
                EnsureReplyFromTarget(connection, control);
                if (control.Accepted == true) _store.RecordAbortReply(control, connection.PeerFingerprint);
                await NotifyControlReplyAsync(connection, control).ConfigureAwait(false);
                break;
            case GroupControlKind.MembershipCommitReply:
                EnsureReplyFromTarget(connection, control);
                if (control.Accepted == true)
                    _store.RecordCommitReply(control, connection.PeerFingerprint);
                await NotifyControlReplyAsync(connection, control).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("该群控制消息类型不能在此连接上路由。");
        }
    }

    private async Task ReceiveGroupTextAsync(V4Connection connection, byte[] payload)
    {
        GroupMessageEnvelope message = V4GroupWire.DecodeGroupText(payload);
        EnsureCanonical(message, payload);

        string? rejection = ValidateSenderAndGroup(connection, message.GroupID, message.SenderFingerprint,
            message.MemberRevision);
        bool added = false;
        if (rejection is null)
        {
            try { added = _store.AddIncomingMessage(message); }
            catch (V4GroupMembershipChangePendingException) { rejection = "membership_change_pending"; }
            catch (InvalidDataException) { rejection = "message_conflict"; }
            catch (IOException) { rejection = "persistence_failed"; }
        }

        var acknowledgement = new GroupTextAck(message.GroupID, message.Id, message.MemberRevision,
            accepted: rejection is null, reason: rejection);
        await connection.SendGroupTextAckAsync(V4GroupWire.EncodeCanonical(acknowledgement)).ConfigureAwait(false);

        if (rejection is null && added)
        {
            Func<V4Connection, GroupMessageEnvelope, Task>? callback = TextReceived;
            if (callback is not null)
            {
                try { await callback(connection, message).ConfigureAwait(false); }
                catch { /* Persistence and the wire acknowledgement remain authoritative. */ }
            }
        }
    }

    private async Task<bool> ApproveGroupFileAsync(V4Connection connection, V4GroupFileManifest manifest,
        CancellationToken cancellationToken)
    {
        if (ValidateSenderAndGroup(connection, manifest.GroupID, manifest.SenderFingerprint,
                manifest.MemberRevision) is not null)
            return false;

        Func<V4Connection, V4GroupFileManifest, CancellationToken, Task<bool>>? callback = GroupFileApproval;
        if (callback is null) return false;
        try { return await callback(connection, manifest, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task ReceiveGroupFileAsync(V4Connection connection, V4GroupFileManifest manifest, string path)
    {
        try
        {
            if (ValidateSenderAndGroup(connection, manifest.GroupID, manifest.SenderFingerprint,
                    manifest.MemberRevision) is not null)
                throw new InvalidDataException("群文件接收期间群成员、revision 或成员事务状态已变化。");

            using (await AcquireFileMessageLockAsync((manifest.GroupID, manifest.MessageID)).ConfigureAwait(false))
            {
                IReadOnlyList<GroupMessageEnvelope> history = _store.GetMessages(manifest.GroupID);
                GroupMessageEnvelope? existing = history.FirstOrDefault(message =>
                    message.Id == manifest.MessageID && message.SenderFingerprint == manifest.SenderFingerprint);
                GroupMessageKind kind = manifest.Metadata.Kind == V4GroupFileKind.Image
                    ? GroupMessageKind.Image
                    : GroupMessageKind.File;
                var fileMetadata = new GroupFileMetadata(manifest.Metadata.TransferID, manifest.Metadata.FileName,
                    manifest.Metadata.Size, sha256: manifest.Sha256);
                var envelope = new GroupMessageEnvelope(manifest.MessageID, manifest.GroupID,
                    manifest.SenderFingerprint, manifest.MemberRevision, existing?.Date ?? DateTimeOffset.UtcNow,
                    kind, fileMetadata: fileMetadata);
                _store.AddIncomingMessage(envelope);

                Func<V4Connection, GroupMessageEnvelope, string, Task>? callback = FileReceived;
                if (callback is not null)
                {
                    try { await callback(connection, envelope, path).ConfigureAwait(false); }
                    catch { /* Persistence and the verified file remain authoritative. */ }
                }
            }
        }
        catch
        {
            try { File.Delete(path); }
            catch { /* Preserve the receive error if best-effort cleanup also fails. */ }
            throw;
        }
    }

    private string? ValidateSenderAndGroup(V4Connection connection, Guid groupID, string senderFingerprint,
        ulong memberRevision)
    {
        string peer = V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint);
        string sender = V4GroupWire.NormalizeFingerprint(senderFingerprint);
        if (!StringComparer.Ordinal.Equals(peer, sender)) return "sender_mismatch";

        GroupConversation? group = _store.GetGroup(groupID);
        if (group is null) return "unknown_group";
        if (_store.HasPendingMembershipChange(groupID)) return "membership_change_pending";
        if (!group.Members.Any(member => StringComparer.Ordinal.Equals(member.Fingerprint, peer)))
            return "sender_not_member";
        if (group.Revision != memberRevision) return "revision_mismatch";
        return null;
    }

    private void EnsureAuthenticatedOwner(V4Connection connection, GroupControlMessage control)
    {
        if (!StringComparer.Ordinal.Equals(
                V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint), control.OwnerFingerprint))
            throw new InvalidDataException("群控制消息 owner 与已认证连接身份不匹配。");
    }

    private void EnsureLeaveRequest(V4Connection connection, GroupControlMessage request)
    {
        string peer = V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint);
        if (request.OwnerFingerprint != _localFingerprint || request.TargetFingerprint != peer)
            throw new InvalidDataException("群退出请求与群主或已认证成员身份不匹配。");
    }

    private void EnsureLeaveReplyFromOwner(V4Connection connection, GroupControlMessage reply)
    {
        string peer = V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint);
        if (reply.OwnerFingerprint != peer || reply.TargetFingerprint != _localFingerprint)
            throw new InvalidDataException("群退出回执与已认证群主或本机身份不匹配。");
    }

    private void EnsureReplyFromTarget(V4Connection connection, GroupControlMessage reply)
    {
        if (reply.OwnerFingerprint != _localFingerprint || reply.TargetFingerprint is null ||
            !StringComparer.Ordinal.Equals(
                V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint), reply.TargetFingerprint))
            throw new InvalidDataException("群控制回复与本机或已认证连接身份不匹配。");
    }

    private static async Task SendControlAsync(V4Connection connection, GroupControlMessage reply,
        CancellationToken cancellationToken) =>
        await connection.SendGroupControlAsync(V4GroupWire.EncodeCanonical(reply), cancellationToken)
            .ConfigureAwait(false);

    private async Task<IDisposable> AcquireFileMessageLockAsync((Guid GroupID, Guid MessageID) key)
    {
        FileMessageLockEntry entry;
        lock (_fileMessageLockSync)
        {
            if (!_fileMessageLocks.TryGetValue(key, out entry!))
                _fileMessageLocks.Add(key, entry = new FileMessageLockEntry());
            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync().ConfigureAwait(false);
            return new FileMessageLockLease(this, key, entry);
        }
        catch
        {
            DropFileMessageLockReference(key, entry);
            throw;
        }
    }

    private void ReleaseFileMessageLock((Guid GroupID, Guid MessageID) key, FileMessageLockEntry entry)
    {
        entry.Semaphore.Release();
        DropFileMessageLockReference(key, entry);
    }

    private void DropFileMessageLockReference((Guid GroupID, Guid MessageID) key, FileMessageLockEntry entry)
    {
        lock (_fileMessageLockSync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 && _fileMessageLocks.TryGetValue(key, out FileMessageLockEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _fileMessageLocks.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private void ForwardGroupFileProgress(V4Connection connection, V4GroupFileManifest manifest,
        long bytesReceived)
    {
        Action<V4Connection, V4GroupFileManifest, long>? callback = GroupFileReceiveProgress;
        if (callback is null) return;
        try { callback(connection, manifest, bytesReceived); }
        catch { /* UI progress observers cannot interrupt the authenticated transfer. */ }
    }

    private async Task NotifyControlReplyAsync(V4Connection connection, GroupControlMessage reply)
    {
        Func<V4Connection, string, GroupControlMessage, Task>? callback = ControlReplyReceived;
        if (callback is not null)
            await callback(connection, V4GroupWire.NormalizeFingerprint(connection.PeerFingerprint), reply)
                .ConfigureAwait(false);
    }

    private static void EnsureCanonical<T>(T value, ReadOnlySpan<byte> payload)
    {
        if (!V4GroupWire.EncodeCanonical(value).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("群组记录不是规范 JSON 编码。");
    }
}
