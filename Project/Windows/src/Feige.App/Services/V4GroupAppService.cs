using System.Collections.ObjectModel;
using Feige.Core.Conversations;

namespace Feige.App.Services;

public sealed record GroupApprovalRequest(Guid Id, string PeerFingerprint, string PeerName,
    GroupControlMessage? Invitation, V4GroupFileManifest? File, DateTimeOffset RequestedAt,
    GroupControlMessage? LeaveRequest = null);
public sealed record GroupEvent(string Kind, Guid GroupID, string PeerFingerprint, string Detail,
    GroupMessageEnvelope? Message = null, string? FilePath = null);
public sealed record GroupOperationState(Guid GroupID, bool Pending, string? Error, V4GroupMembershipStage? Stage);

/// <summary>App-facing durable v4 group state and explicit approval bridge.</summary>
public sealed class V4GroupAppService
{
    private readonly V4GroupStore _store;
    private readonly Func<string, V4Connection?> _connectionFor;
    private readonly string _localFingerprint;
    private readonly Func<Action, Task> _onUi;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _decisions = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid EventID, string Peer), TaskCompletionSource<bool>> _inviteReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<GroupControlMessage>> _leaveReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _leaveEventsByGroup = new();
    private readonly TimeSpan _approvalTimeout = TimeSpan.FromMinutes(2);
    private PeerConnectionRegistry<V4Connection>? _registry;
    public ObservableCollection<GroupConversation> Groups { get; } = [];
    public ObservableCollection<GroupMessageEnvelope> Messages { get; } = [];
    public ObservableCollection<GroupApprovalRequest> PendingApprovals { get; } = [];
    public ObservableCollection<GroupOperationState> Operations { get; } = [];
    private readonly Dictionary<Guid, string> _operationErrors = [];
    private readonly object _operationGate = new();
    public event EventHandler<GroupEvent>? EventReceived;
    public event EventHandler<string>? TransactionFailed;
    public V4GroupStore Store => _store;
    public bool IsPeerConnected(string fingerprint)
    {
        var connection = _connectionFor(PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint));
        return connection is { IsClosed: false, ProtocolVersion: >= V4Offer.CurrentVersion };
    }

    public V4GroupAppService(string appDataFolder, string localFingerprint, Func<string, V4Connection?> connectionFor,
        Func<Action, Task>? onUi = null)
    {
        _localFingerprint = new GroupMember(localFingerprint).Fingerprint;
        _connectionFor = connectionFor;
        _onUi = onUi ?? (action => AppSession.Current.InvokeOnUiAsync(action));
        _store = new V4GroupStore(Path.Combine(appDataFolder, "v4-groups.json"));
        foreach (var group in _store.Groups.Where(group => group.Members.Any(member => member.Fingerprint == _localFingerprint)))
            Groups.Add(group);
        foreach (var message in _store.Messages) Messages.Add(message);
        _ = RefreshOperationsAsync();
    }

    public void SetRegistry(PeerConnectionRegistry<V4Connection> registry) => _registry = registry;

    public V4GroupConnectionRouter CreateRouter() => new(_store, _localFingerprint)
    {
        InvitationApproval = ApproveInvitationAsync,
        LeaveApproval = ApproveLeaveAsync,
        GroupFileApproval = ApproveFileAsync,
        TextReceived = async (connection, message) => await OnUiAsync(() =>
        {
            if (!Messages.Any(m => m.Id == message.Id)) Messages.Add(message);
            EventReceived?.Invoke(this, new("text", message.GroupID, connection.PeerFingerprint, "收到群文字", message));
        }),
        FileReceived = async (connection, message, path) => await OnUiAsync(() =>
        {
            if (!Messages.Any(m => m.Id == message.Id)) Messages.Add(message);
            EventReceived?.Invoke(this, new("file", message.GroupID, connection.PeerFingerprint, "收到群文件", message, path));
        }),
        MembershipCommitted = async (connection, group) =>
        {
            await RefreshGroupsAsync().ConfigureAwait(false);
            await RefreshOperationsAsync().ConfigureAwait(false);
            await OnUiAsync(() => EventReceived?.Invoke(this, new("MembershipCommitted", group.Id,
                connection.PeerFingerprint, "群成员名单已更新"))).ConfigureAwait(false);
        },
        ControlReplyReceived = async (connection, authenticatedPeer, message) =>
        {
            GroupControlKind controlKind = message.Kind;
            if (controlKind == GroupControlKind.LeaveReply)
            {
                if (_leaveReplies.TryGetValue(message.EventID, out var leaveWaiter))
                    leaveWaiter.TrySetResult(message);
                await OnUiAsync(() => EventReceived?.Invoke(this, new(controlKind.ToString(), message.GroupID,
                    connection.PeerFingerprint, message.Accepted == true ? "退出申请已批准" : "退出申请未批准")));
                return;
            }
            if (controlKind == GroupControlKind.InviteReply && message.Accepted.HasValue)
            {
                var key = (message.EventID, PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(connection.PeerFingerprint));
                if (_inviteReplies.TryGetValue(key, out var waiter)) waiter.TrySetResult(message.Accepted.Value);
            }
            if (message.Accepted == true && controlKind == GroupControlKind.MembershipPrepareReply)
                _store.RecordPrepareReply(message, connection.PeerFingerprint);
            else if (message.Accepted == true && controlKind == GroupControlKind.MembershipCommitReply)
                _store.RecordCommitReply(message, connection.PeerFingerprint);
            await OnUiAsync(() => EventReceived?.Invoke(this, new(controlKind.ToString(), message.GroupID, connection.PeerFingerprint,
                message.Accepted == true ? "已确认" : "已拒绝")));
            if (message.Accepted != true)
            {
                bool aborting = controlKind == GroupControlKind.MembershipPrepareReply &&
                    _store.GetPendingTransaction(message.GroupID)?.Stage == V4GroupMembershipStage.Aborting;
                if (!aborting)
                {
                    SetOperationError(message.GroupID, $"{connection.PeerFingerprint} 未确认 {controlKind}");
                    await OnUiAsync(() => TransactionFailed?.Invoke(this,
                        controlKind == GroupControlKind.MembershipAbortReply
                            ? "尚未收到所有成员的持久取消确认；群成员事务仍保留，稍后可重试。"
                            : $"群成员事务未完成：{connection.PeerFingerprint} {controlKind}"));
                }
                await RefreshOperationsAsync();
            }
            else if (_store.GetPendingTransaction(message.GroupID) is null)
            {
                ClearOperationError(message.GroupID);
                await RefreshOperationsAsync();
            }
            RefreshGroups();
        }
    };

    private Task<bool> ApproveInvitationAsync(V4Connection connection, GroupControlMessage invite, CancellationToken ct)
        => RequestDecisionAsync(connection, invite, null, null, ct);
    private Task<bool> ApproveFileAsync(V4Connection connection, V4GroupFileManifest file, CancellationToken ct)
        => RequestDecisionAsync(connection, null, file, null, ct);

    private async Task<bool> ApproveLeaveAsync(V4Connection connection, GroupControlMessage request, CancellationToken ct)
    {
        GroupConversation? group = _store.GetGroup(request.GroupID);
        string requester = new GroupMember(connection.PeerFingerprint).Fingerprint;
        if (group is null || group.OwnerFingerprint != _localFingerprint ||
            group.Revision != request.Epoch || request.OwnerFingerprint != _localFingerprint ||
            request.TargetFingerprint != requester || !group.Members.Any(member => member.Fingerprint == requester) ||
            _store.GetPendingTransaction(group.Id) is not null ||
            _store.PendingMembershipPrepares.Any(prepare => prepare.GroupID == group.Id))
            return false;

        if (!await RequestDecisionAsync(connection, null, null, request, ct).ConfigureAwait(false)) return false;
        try
        {
            GroupMember[] roster = group.Members.Where(member => member.Fingerprint != requester).ToArray();
            Task transaction = ChangeMembersAsync(group.Id, roster, CancellationToken.None);
            V4PendingGroupTransaction? pending = _store.GetPendingTransaction(group.Id);
            if (pending is not null && !pending.ProposedGroup.Members.Any(member => member.Fingerprint == requester))
            {
                _ = ObserveLeaveTransactionAsync(transaction);
                return true;
            }
            await transaction.ConfigureAwait(false);
            return !_store.GetGroup(group.Id)!.Members.Any(member => member.Fingerprint == requester);
        }
        catch
        {
            return false;
        }
    }

    private static async Task ObserveLeaveTransactionAsync(Task transaction)
    {
        try { await transaction.ConfigureAwait(false); }
        catch { /* ChangeMembersAsync persists and reports its own retryable failure state. */ }
    }

    private async Task<bool> RequestDecisionAsync(V4Connection connection, GroupControlMessage? invitation,
        V4GroupFileManifest? file, GroupControlMessage? leaveRequest, CancellationToken ct)
    {
        Guid id = invitation?.EventID ?? leaveRequest?.EventID ?? file!.Metadata.TransferID;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) if (!_decisions.TryAdd(id, decision)) return false;
        try
        {
            AppLog.Write($"group approval queued id={id} kind={(invitation is not null ? "invite:" + invitation.Name : file is not null ? "file" : "leave")}");
            await OnUiAsync(() => PendingApprovals.Add(new(id, connection.PeerFingerprint, connection.PeerName,
                invitation, file, DateTimeOffset.Now, leaveRequest)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_approvalTimeout);
            try { return await decision.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        finally
        {
            lock (_gate) _decisions.Remove(id);
            await OnUiAsync(() => { var item = PendingApprovals.FirstOrDefault(p => p.Id == id); if (item is not null) PendingApprovals.Remove(item); });
        }
    }

    public bool Decide(Guid requestId, bool accept)
    {
        AppLog.Write($"group decide id={requestId} accept={accept}");
        lock (_gate) return _decisions.TryGetValue(requestId, out var decision) && decision.TrySetResult(accept);
    }

    public async Task CreateGroupAsync(string name, IEnumerable<GroupMember> members, CancellationToken ct = default)
    {
        GroupMember[] roster = members.Select(m => new GroupMember(m.Fingerprint, m.DisplayName)).ToArray();
        if (!roster.Any(m => m.Fingerprint == _localFingerprint)) roster = roster.Append(new GroupMember(_localFingerprint)).ToArray();
        if (!roster.Any(m => m.Fingerprint != _localFingerprint)) throw new InvalidOperationException("创建群组至少需要一名已连接的远端成员。");
        EnsurePeersConnected(roster.Select(m => m.Fingerprint));
        Guid groupId = Guid.NewGuid();
        _ = new GroupConversation(groupId, name, _localFingerprint, 1, roster);
        var localOnly = new GroupConversation(groupId, name, _localFingerprint, 0, [new GroupMember(_localFingerprint)]);
        var tx = _store.CreateGroupAndBeginMembershipChange(localOnly, _localFingerprint, roster);
        RefreshGroups(); await RefreshOperationsAsync();
        try
        {
            await InviteNewMembersAndWaitAsync(localOnly, tx, ct);
            await DriveTransactionAsync(localOnly.Id, ct);
            RefreshGroups();
        }
        catch (Exception ex)
        {
            SetOperationError(localOnly.Id, ex.Message); await RefreshOperationsAsync();
            await OnUiAsync(() => TransactionFailed?.Invoke(this, "群创建仍有待处理事务，已保留状态且未宣称回滚：" + ex.Message));
            throw;
        }
    }

    internal void AddLocalGroup(GroupConversation group)
    {
        if (group.OwnerFingerprint != _localFingerprint || !group.Members.Any(m => m.Fingerprint == _localFingerprint))
            throw new InvalidOperationException("本机必须是新群组的群主和成员。");
        EnsurePeersConnected(group.Members.Select(m => m.Fingerprint));
        _store.AddGroup(group); RefreshGroups();
    }

    public async Task<IReadOnlyList<V4GroupMemberDeliveryResult>> SendTextAsync(Guid groupId, string text, CancellationToken ct = default)
    {
        var result = await Coordinator().SendTextAsync(groupId, text, ct);
        await RefreshMessagesAsync();
        return result;
    }

    public async Task<IReadOnlyList<V4GroupMemberDeliveryResult>> SendFileAsync(Guid groupId, string path,
        V4GroupFileKind kind = V4GroupFileKind.File, Action<V4GroupDeliveryProgress>? progress = null, CancellationToken ct = default)
    {
        var result = await Coordinator().SendFileAsync(groupId, path, kind, progress, ct);
        await RefreshMessagesAsync();
        return result;
    }

    public async Task ChangeMembersAsync(Guid groupId, IEnumerable<GroupMember> members, CancellationToken ct = default)
    {
        GroupConversation current = _store.GetGroup(groupId) ?? throw new InvalidOperationException("群组不存在。");
        if (current.OwnerFingerprint != _localFingerprint) throw new InvalidOperationException("只有群主可以变更成员。");
        GroupMember[] roster = members.ToArray(); EnsurePeersConnected(current.Members.Concat(roster).Select(m => m.Fingerprint));
        if (_store.GetPendingTransaction(groupId) is not null) throw new InvalidOperationException("群成员事务仍在处理中；不能开始另一项操作。");
        var tx = _store.BeginMembershipChange(groupId, _localFingerprint, roster);
        try
        {
            await InviteNewMembersAndWaitAsync(current, tx, ct);
            await DriveTransactionAsync(groupId, ct);
            RefreshGroups();
        }
        catch (Exception ex) { SetOperationError(groupId, ex.Message); await RefreshOperationsAsync(); await OnUiAsync(() => TransactionFailed?.Invoke(this, "群成员事务失败，状态仍待恢复或确认；未宣称已回滚：" + ex.Message)); throw; }
    }

    public async Task RequestLeaveAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupConversation group = _store.GetGroup(groupId) ?? throw new InvalidOperationException("群组不存在。");
        if (group.OwnerFingerprint == _localFingerprint ||
            !group.Members.Any(member => member.Fingerprint == _localFingerprint))
            throw new InvalidOperationException("只有当前非群主成员可以申请退出群组。");
        if (_store.GetPendingTransaction(groupId) is not null ||
            _store.PendingMembershipPrepares.Any(prepare => prepare.GroupID == groupId))
            throw new InvalidOperationException("群成员事务仍在处理中；请完成后再申请退出。");
        V4Connection ownerConnection = _connectionFor(group.OwnerFingerprint)
            ?? throw new IOException("群主没有已连接的 v4 通道，暂时无法申请退出。");
        Guid eventID = Guid.NewGuid();
        if (!_leaveEventsByGroup.TryAdd(groupId, eventID))
            throw new InvalidOperationException("该群已有退出申请正在等待回复。");
        var waiter = new TaskCompletionSource<GroupControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_leaveReplies.TryAdd(eventID, waiter))
        {
            _leaveEventsByGroup.TryRemove(groupId, out _);
            throw new InvalidOperationException("退出申请事件 ID 冲突。");
        }

        try
        {
            var request = new GroupControlMessage(GroupControlKind.Leave, group.Id, group.OwnerFingerprint,
                group.Revision, eventID, targetFingerprint: _localFingerprint);
            await ownerConnection.SendGroupControlAsync(V4GroupWire.EncodeCanonical(request), ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_approvalTimeout);
            GroupControlMessage reply;
            try { reply = await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("等待群主处理退出申请超时；可以稍后重新申请。"); }
            if (reply.GroupID != group.Id || reply.OwnerFingerprint != group.OwnerFingerprint ||
                reply.TargetFingerprint != _localFingerprint || reply.Epoch != group.Revision)
                throw new InvalidDataException("退出申请回执与本次请求不匹配。");
            if (reply.Accepted != true) throw new InvalidOperationException("群主暂未批准退出申请。");
        }
        finally
        {
            _leaveReplies.TryRemove(eventID, out _);
            _leaveEventsByGroup.TryRemove(groupId, out _);
        }
    }

    public async Task LeaveAsOwnerAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = _store.GetGroup(groupId) ?? throw new InvalidOperationException("群组不存在。");
        if (group.OwnerFingerprint != _localFingerprint) throw new InvalidOperationException("本机不是群主。");
        var leave = _store.LeaveAsOwner(groupId, _localFingerprint);
        if (leave.Dissolved) { RefreshGroups(); return; }
        try { await DriveTransactionAsync(groupId, ct); RefreshGroups(); }
        catch (Exception ex) { SetOperationError(groupId, ex.Message); await RefreshOperationsAsync(); await OnUiAsync(() => TransactionFailed?.Invoke(this, "群主离开事务失败，状态仍待恢复或确认；未宣称已回滚：" + ex.Message)); throw; }
    }

    private async Task InviteNewMembersAndWaitAsync(GroupConversation current, V4PendingGroupTransaction tx, CancellationToken ct)
    {
        var additions = tx.Targets.Where(target => !current.Members.Any(member => member.Fingerprint == target.PeerFingerprint)).ToArray();
        if (additions.Length == 0) return;
        var waiters = new List<((Guid EventID, string Peer) Key, TaskCompletionSource<bool> Source)>();
        try
        {
            foreach (var target in additions)
            {
                var key = (tx.EventID, PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(target.PeerFingerprint));
                var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_inviteReplies.TryAdd(key, source)) throw new InvalidOperationException("该邀请事件正在等待回复。");
                waiters.Add((key, source));
            }
            foreach (var target in additions)
            {
                var connection = _connectionFor(target.PeerFingerprint) ?? throw new IOException("目标成员没有已连接的 v4 通道：" + target.PeerFingerprint);
                var invitation = new GroupControlMessage(GroupControlKind.Invite, current.Id, _localFingerprint,
                    target.Prepare.Epoch, tx.EventID, targetFingerprint: target.PeerFingerprint,
                    name: target.Prepare.Name, roster: target.Prepare.Roster);
                await connection.SendGroupControlAsync(V4GroupWire.EncodeCanonical(invitation), ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_approvalTimeout);
            foreach (var waiter in waiters)
            {
                bool accepted;
                try { accepted = await waiter.Source.Task.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("等待群邀请决定超时；已保留待处理事务。"); }
                if (!accepted) throw new InvalidOperationException("成员拒绝群邀请；已保留待处理事务。");
            }
        }
        finally
        {
            foreach (var waiter in waiters) _inviteReplies.TryRemove(waiter.Key, out _);
        }
    }

    private async Task DriveTransactionAsync(Guid groupId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (GetOperationError(groupId) is string error)
                throw new InvalidOperationException("群成员事务收到拒绝；待处理状态已保留，需由用户显式重试。 " + error);
            var tx = _store.GetPendingTransaction(groupId);
            if (tx is null) { ClearOperationError(groupId); await RefreshOperationsAsync(); return; }
            await RefreshOperationsAsync();
            foreach (var control in _store.GetPendingMessages(groupId))
            {
                if (GetOperationError(groupId) is string replyError)
                    throw new InvalidOperationException("群成员事务收到拒绝；待处理状态已保留，需由用户显式重试。 " + replyError);
                var connection = _connectionFor(control.TargetFingerprint!);
                if (connection is null) continue;
                if (control.Kind == GroupControlKind.MembershipAbort && !connection.SupportsDurableAbort)
                    continue;
                await connection.SendGroupControlAsync(V4GroupWire.EncodeCanonical(control), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException("群成员事务仍未完成，已保留待处理状态；稍后可使用相同事务重试。");
    }

    private async Task RefreshOperationsAsync()
    {
        Dictionary<Guid, string> errors;
        lock (_operationGate) errors = new(_operationErrors);
        await OnUiAsync(() =>
        {
            Operations.Clear();
            foreach (var group in _store.Groups)
            {
                var pending = _store.GetPendingTransaction(group.Id);
                if (pending is not null || errors.ContainsKey(group.Id))
                    Operations.Add(new(group.Id, pending is not null, errors.GetValueOrDefault(group.Id), pending?.Stage));
            }
        });
    }

    private void SetOperationError(Guid groupId, string error) { lock (_operationGate) _operationErrors[groupId] = error; }
    private void ClearOperationError(Guid groupId) { lock (_operationGate) _operationErrors.Remove(groupId); }
    private string? GetOperationError(Guid groupId) { lock (_operationGate) return _operationErrors.GetValueOrDefault(groupId); }

    public async Task CancelPendingTransactionAsync(Guid groupId, CancellationToken ct = default)
    {
        V4PendingGroupTransaction pending = _store.GetPendingTransaction(groupId)
            ?? throw new InvalidOperationException("该群没有待处理事务。");
        if (pending.Stage == V4GroupMembershipStage.Committing)
            throw new InvalidOperationException("提交可能已经开始，不能安全取消；请继续重试提交。");
        if (pending.Stage == V4GroupMembershipStage.Preparing && !_store.BeginAbortMembershipChange(groupId, _localFingerprint))
            throw new InvalidOperationException("事务状态已变化，未能写入取消意图。");
        ClearOperationError(groupId);
        await RefreshOperationsAsync();
        try
        {
            await DriveTransactionAsync(groupId, ct);
            ClearOperationError(groupId);
            RefreshGroups();
        }
        catch (Exception ex)
        {
            SetOperationError(groupId, ex.Message);
            await RefreshOperationsAsync();
            await OnUiAsync(() => TransactionFailed?.Invoke(this,
                "取消意图已持久化；仍需全部原目标的持久确认，稍后可重试：" + ex.Message));
            throw;
        }
    }

    public async Task RetryPendingTransactionAsync(Guid groupId, CancellationToken ct = default)
    {
        if (_store.GetPendingTransaction(groupId) is null) throw new InvalidOperationException("该群没有待处理事务。");
        ClearOperationError(groupId); await RefreshOperationsAsync();
        try
        {
            var pending = _store.GetPendingTransaction(groupId)!;
            var current = _store.GetGroup(groupId)!;
            if (pending.Stage == V4GroupMembershipStage.Preparing &&
                pending.Targets.Any(target => !current.Members.Any(member => member.Fingerprint == target.PeerFingerprint)))
                await InviteNewMembersAndWaitAsync(current, pending, ct);
            await DriveTransactionAsync(groupId, ct);
            ClearOperationError(groupId); RefreshGroups();
        }
        catch (Exception ex) { SetOperationError(groupId, ex.Message); await RefreshOperationsAsync(); await OnUiAsync(() => TransactionFailed?.Invoke(this, "群成员事务仍未完成，待处理状态已保留：" + ex.Message)); throw; }
    }

    private V4GroupDeliveryCoordinator Coordinator() => new(_store,
        _registry ?? throw new InvalidOperationException("v4 peer registry尚未可用。"), _localFingerprint);
    private void EnsurePeersConnected(IEnumerable<string> fingerprints)
    {
        foreach (var fingerprint in fingerprints.Where(f => f != _localFingerprint))
            if (_connectionFor(fingerprint) is null) throw new InvalidOperationException("目标成员没有已连接的 v4 通道：" + fingerprint);
    }
    private async Task RefreshMessagesAsync()
    {
        await OnUiAsync(() =>
        {
            Messages.Clear();
            foreach (var message in _store.Messages) Messages.Add(message);
        });
    }

    private void RefreshGroups() => _ = RefreshGroupsAsync();
    private Task RefreshGroupsAsync() => OnUiAsync(() =>
    {
        GroupConversation[] desired = _store.Groups
            .Where(group => group.Members.Any(member => member.Fingerprint == _localFingerprint))
            .ToArray();
        for (int index = Groups.Count - 1; index >= 0; index--)
        {
            GroupConversation? updated = desired.FirstOrDefault(group => group.Id == Groups[index].Id);
            if (updated is null) Groups.RemoveAt(index);
            else Groups[index] = updated;
        }
        foreach (GroupConversation group in desired)
            if (!Groups.Any(current => current.Id == group.Id)) Groups.Add(group);
    });
    private Task OnUiAsync(Action action) => _onUi(action);
}
