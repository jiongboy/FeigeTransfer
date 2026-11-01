using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Conversations;

namespace Feige.Core.Conversations;

public enum V4GroupMembershipStage
{
    Preparing,
    Committing,
    Aborting
}

public sealed record V4GroupTransactionTarget(
    string PeerFingerprint,
    GroupControlMessage Prepare,
    GroupControlMessage Commit,
    bool Prepared = false,
    bool Committed = false,
    bool Aborted = false,
    bool PrepareRejected = false);

public sealed record V4PendingGroupTransaction(
    GroupConversation ProposedGroup,
    string OwnerFingerprint,
    ulong BaseEpoch,
    Guid EventID,
    V4GroupMembershipStage Stage,
    IReadOnlyList<V4GroupTransactionTarget> Targets);

public sealed record V4CompletedGroupTransaction(
    Guid GroupID,
    Guid EventID,
    ulong Epoch,
    string OwnerFingerprint,
    GroupConversation CommittedGroup,
    IReadOnlyList<V4GroupTransactionTarget> Targets,
    bool Aborted = false);

public sealed record V4GroupAbortTombstone(GroupControlMessage Abort, bool Accepted);

public sealed record V4GroupOwnerLeaveResult(
    bool Dissolved,
    GroupConversation? ProposedGroup,
    string? NewOwnerFingerprint,
    IReadOnlyList<GroupControlMessage> PrepareMessages);

internal sealed class V4GroupMembershipChangePendingException : InvalidOperationException
{
    public V4GroupMembershipChangePendingException()
        : base("群组成员变更期间不能接收消息。")
    {
    }
}

/// <summary>
/// Thread-safe, crash-safe persistence for v4 group membership and message history.
/// Each mutation is serialized per archive path, reloads the latest disk snapshot,
/// writes a same-directory temporary file, flushes it, and atomically replaces the archive.
/// </summary>
public sealed class V4GroupStore
{
    private const int ArchiveVersion = 2;
    private const int MaximumCompletedTransactions = 256;
    private const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions ArchiveJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;
    private readonly object _instanceLock = new();
    private readonly object _pathLock;
    private StoreState _state;

    public V4GroupStore(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        _path = Path.GetFullPath(archivePath);
        _pathLock = PathLocks.GetOrAdd(_path, static _ => new object());
        lock (_pathLock) _state = ReadState(_path);
    }

    public IReadOnlyList<GroupConversation> Groups => Read(state =>
        Array.AsReadOnly(state.Groups.Values.OrderBy(group => group.Id).ToArray()));

    public IReadOnlyList<GroupMessageEnvelope> Messages => Read(state => Array.AsReadOnly(state.Messages.ToArray()));

    public IReadOnlyList<GroupControlMessage> PendingMembershipPrepares => Read(state =>
        Array.AsReadOnly(state.PendingIncoming.Values.OrderBy(message => message.GroupID).ToArray()));

    public IReadOnlyList<GroupControlMessage> AcceptedInvitations => Read(state =>
        Array.AsReadOnly(state.AcceptedInvitations.Values.OrderBy(message => message.EventID).ToArray()));

    public IReadOnlyList<V4GroupAbortTombstone> AbortTombstones => Read(state =>
        Array.AsReadOnly(state.AbortTombstones.ToArray()));

    public IReadOnlyList<V4PendingGroupTransaction> PendingTransactions => Read(state =>
        Array.AsReadOnly(state.PendingOutgoing.Values.OrderBy(transaction => transaction.ProposedGroup.Id).ToArray()));

    public bool HasPendingMembershipChange(Guid groupID) => Read(state =>
        state.PendingOutgoing.ContainsKey(groupID) || state.PendingIncoming.ContainsKey(groupID));

    public GroupConversation? GetGroup(Guid groupID) => Read(state =>
        state.Groups.TryGetValue(groupID, out GroupConversation? group) ? group : null);

    public IReadOnlyList<GroupMessageEnvelope> GetMessages(Guid groupID) => Read(state =>
        Array.AsReadOnly(state.Messages.Where(message => message.GroupID == groupID).ToArray()));

    public V4PendingGroupTransaction? GetPendingTransaction(Guid groupID) => Read(state =>
        state.PendingOutgoing.TryGetValue(groupID, out V4PendingGroupTransaction? transaction) ? transaction : null);

    public void AddGroup(GroupConversation group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Mutate(state =>
        {
            if (state.Groups.TryGetValue(group.Id, out GroupConversation? existing))
            {
                if (!SameGroup(existing, group)) throw Invalid("群组 ID 已对应不同的成员状态。");
                return (0, false);
            }
            state.Groups.Add(group.Id, group);
            return (0, true);
        });
    }

    /// <summary>Atomically persists a new owner-only group and its initial membership transaction.</summary>
    public V4PendingGroupTransaction CreateGroupAndBeginMembershipChange(GroupConversation initialGroup,
        string ownerFingerprint, IReadOnlyList<GroupMember> proposedMembers)
    {
        ArgumentNullException.ThrowIfNull(initialGroup);
        ArgumentNullException.ThrowIfNull(proposedMembers);
        string owner = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        return Mutate(state =>
        {
            if (initialGroup.OwnerFingerprint != owner || initialGroup.Revision != 0 ||
                initialGroup.Members.Count != 1 || initialGroup.Members[0].Fingerprint != owner)
                throw Invalid("初始群组必须是本机群主的空白群组状态。");
            if (state.Groups.ContainsKey(initialGroup.Id) || state.PendingOutgoing.ContainsKey(initialGroup.Id) ||
                state.PendingIncoming.ContainsKey(initialGroup.Id))
                throw Invalid("新群组 ID 已存在或有待处理事务。");

            V4PendingGroupTransaction transaction = BuildTransaction(initialGroup, owner, proposedMembers, null);
            state.Groups.Add(initialGroup.Id, initialGroup);
            state.PendingOutgoing.Add(initialGroup.Id, transaction);
            return (transaction, true);
        });
    }

    /// <summary>Persists an accepted invitation so an invitee can safely prepare and commit its roster.</summary>
    public GroupControlMessage AcceptInvitation(GroupControlMessage invite, string localFingerprint, bool accepted = true)
    {
        ArgumentNullException.ThrowIfNull(invite);
        string local = V4GroupWire.NormalizeFingerprint(localFingerprint);
        if (invite.Kind != GroupControlKind.Invite || invite.TargetFingerprint != local || invite.Roster is null ||
            invite.Name is null || !invite.Roster.Any(member => member.Fingerprint == local))
            throw Invalid("群邀请目标或名单无效。");

        if (accepted)
        {
            Mutate(state =>
            {
                if (state.Groups.ContainsKey(invite.GroupID))
                {
                    if (TryGetCompletedTarget(state, invite.EventID, local, out V4GroupTransactionTarget? completed) &&
                        MatchesInitialInvitation(invite, completed!.Prepare, local))
                        return (0, false);
                    throw Invalid("该群已存在，不能再次接受初始邀请。");
                }
                if (state.AcceptedInvitations.TryGetValue(invite.EventID, out GroupControlMessage? existing))
                {
                    if (!SameControl(existing, invite)) throw Invalid("重复邀请事件携带了不同内容。");
                    return (0, false);
                }
                state.AcceptedInvitations.Add(invite.EventID, invite);
                return (0, true);
            });
        }

        return new GroupControlMessage(GroupControlKind.InviteReply, invite.GroupID,
            invite.OwnerFingerprint, invite.Epoch, invite.EventID, targetFingerprint: local, accepted: accepted);
    }

    /// <summary>Adds a message once, requiring the sender and epoch to match the committed roster.</summary>
    public bool AddMessage(GroupMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Mutate(state => AddMessageToState(state, message));
    }

    /// <summary>Adds an incoming message only when no membership transaction is pending.</summary>
    public bool AddIncomingMessage(GroupMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Mutate(state =>
        {
            if (state.PendingOutgoing.ContainsKey(message.GroupID) || state.PendingIncoming.ContainsKey(message.GroupID))
                throw new V4GroupMembershipChangePendingException();
            return AddMessageToState(state, message);
        });
    }

    /// <summary>Adds a local outgoing message only when no membership transaction is pending.</summary>
    public bool AddOutgoingMessage(GroupMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Mutate(state =>
        {
            if (state.PendingOutgoing.ContainsKey(message.GroupID) || state.PendingIncoming.ContainsKey(message.GroupID))
                throw Invalid("群组成员变更期间不能发送消息。");
            return AddMessageToState(state, message);
        });
    }

    private static (bool Result, bool Changed) AddMessageToState(StoreState state, GroupMessageEnvelope message)
    {
        if (!state.Groups.TryGetValue(message.GroupID, out GroupConversation? group) ||
            group.Revision != message.MemberRevision ||
            !group.Members.Any(member => member.Fingerprint == message.SenderFingerprint))
            throw Invalid("群消息不匹配当前群组成员名单或 epoch。");

        int existingIndex = state.Messages.FindIndex(candidate =>
            candidate.GroupID == message.GroupID && candidate.Id == message.Id &&
            candidate.SenderFingerprint == message.SenderFingerprint);
        if (existingIndex >= 0)
        {
            if (!SameMessage(state.Messages[existingIndex], message))
                throw Invalid("重复群消息 ID 携带了不同内容。");
            return (false, false);
        }
        state.Messages.Add(message);
        return (true, true);
    }

    /// <summary>
    /// Persists an incoming prepare before accepting it. Replaying the same prepare returns
    /// the same accepted reply; a conflicting event for that group is rejected.
    /// </summary>
    public GroupControlMessage PrepareMembership(GroupControlMessage prepare, string localFingerprint)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        string local = V4GroupWire.NormalizeFingerprint(localFingerprint);
        bool accepted = Mutate(state =>
        {
            ValidateIncomingTarget(prepare, GroupControlKind.MembershipPrepare, local);
            V4GroupAbortTombstone? tombstone = state.AbortTombstones.SingleOrDefault(item => item.Abort.EventID == prepare.EventID);
            if (tombstone is not null)
            {
                if (!MatchesAbort(prepare, tombstone.Abort)) throw Invalid("prepare 与已记录 abort tombstone 不匹配。");
                return (false, false);
            }
            if (TryGetCompletedTarget(state, prepare.EventID, local, out V4GroupTransactionTarget? completedTarget))
            {
                if (!SameControl(completedTarget!.Prepare, prepare)) throw Invalid("已提交事务的重复 prepare 内容不一致。");
                V4CompletedGroupTransaction? completed = state.Completed.SingleOrDefault(item => item.EventID == prepare.EventID);
                return (completed?.Aborted != true, false);
            }

            if (state.Groups.TryGetValue(prepare.GroupID, out GroupConversation? current))
            {
                if (!current.Members.Any(member => member.Fingerprint == local))
                    throw Invalid("本机不是当前成员，不能接受该成员变更。");
                ValidateNextRoster(current, prepare);
            }
            else if (!state.AcceptedInvitations.TryGetValue(prepare.EventID, out GroupControlMessage? invite) ||
                     !MatchesInitialInvitation(invite, prepare, local))
            {
                throw Invalid("初始 membershipPrepare 必须对应本机已接受的邀请。");
            }

            if (state.PendingIncoming.TryGetValue(prepare.GroupID, out GroupControlMessage? pending))
            {
                if (!SameControl(pending, prepare)) throw Invalid("该群已有内容不同的成员变更 prepare。");
                return (true, false);
            }
            if (state.PendingOutgoing.ContainsKey(prepare.GroupID))
                throw Invalid("该群当前已有本地成员变更事务。");
            state.PendingIncoming.Add(prepare.GroupID, prepare);
            return (true, true);
        });

        return new GroupControlMessage(GroupControlKind.MembershipPrepareReply, prepare.GroupID,
            prepare.OwnerFingerprint, prepare.Epoch, prepare.EventID, targetFingerprint: local, accepted: accepted);
    }

    /// <summary>Commits a previously persisted prepare; exact commit replays are acknowledged idempotently.</summary>
    public GroupControlMessage CommitMembership(GroupControlMessage commit, string localFingerprint)
    {
        ArgumentNullException.ThrowIfNull(commit);
        string local = V4GroupWire.NormalizeFingerprint(localFingerprint);
        bool accepted = Mutate(state =>
        {
            ValidateIncomingTarget(commit, GroupControlKind.MembershipCommit, local);
            V4GroupAbortTombstone? tombstone = state.AbortTombstones.SingleOrDefault(item => item.Abort.EventID == commit.EventID);
            if (tombstone is not null)
            {
                if (!MatchesAbort(commit, tombstone.Abort)) throw Invalid("commit 与 abort tombstone 不匹配。");
                return (false, false);
            }
            if (TryGetCompletedTarget(state, commit.EventID, local, out V4GroupTransactionTarget? completedTarget))
            {
                if (!SameControl(completedTarget!.Commit, commit)) throw Invalid("已提交事务的重复 commit 内容不一致。");
                V4CompletedGroupTransaction? completed = state.Completed.SingleOrDefault(item => item.EventID == commit.EventID);
                return (completed?.Aborted != true, false);
            }

            if (!state.PendingIncoming.TryGetValue(commit.GroupID, out GroupControlMessage? prepare) ||
                prepare.EventID != commit.EventID || !IsMatchingTransaction(prepare, commit))
                throw Invalid("commit 缺少内容匹配的持久化 prepare。");
            if (state.Groups.TryGetValue(commit.GroupID, out GroupConversation? current))
            {
                ValidateNextRoster(current, commit);
            }
            else if (!state.AcceptedInvitations.TryGetValue(commit.EventID, out GroupControlMessage? invite) ||
                     !MatchesInitialInvitation(invite, commit, local))
            {
                throw Invalid("初始 membershipCommit 必须对应本机已接受的邀请。");
            }
            GroupConversation committed = CreateProposedGroup(commit);
            state.Groups[commit.GroupID] = committed;
            state.PendingIncoming.Remove(commit.GroupID);
            state.AcceptedInvitations.Remove(commit.EventID);
            var target = new V4GroupTransactionTarget(local, prepare, commit, Prepared: true, Committed: true);
            AddCompleted(state, new V4CompletedGroupTransaction(commit.GroupID, commit.EventID,
                commit.Epoch, commit.OwnerFingerprint, committed, [target]));
            return (true, true);
        });

        return new GroupControlMessage(GroupControlKind.MembershipCommitReply, commit.GroupID,
            commit.OwnerFingerprint, commit.Epoch, commit.EventID, targetFingerprint: local, accepted: accepted);
    }

    /// <summary>Persists the receiver's terminal abort decision before returning an acknowledgment.</summary>
    public bool AbortMembership(GroupControlMessage abort, string? localFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(abort);
        if (abort.Kind != GroupControlKind.MembershipAbort || abort.TargetFingerprint is null)
            throw Invalid("消息不是完整的 membershipAbort。");
        string target = V4GroupWire.NormalizeFingerprint(abort.TargetFingerprint);
        if (localFingerprint is not null && V4GroupWire.NormalizeFingerprint(localFingerprint) != target)
            throw Invalid("membershipAbort 目标与本机身份不匹配。");
        return Mutate(state =>
        {
            V4GroupAbortTombstone? prior = state.AbortTombstones.SingleOrDefault(item => item.Abort.EventID == abort.EventID);
            if (prior is not null)
            {
                if (!SameControl(prior.Abort, abort)) throw Invalid("重复 membershipAbort 内容不一致。");
                return (prior.Accepted, false);
            }
            V4CompletedGroupTransaction? completed = state.Completed.SingleOrDefault(item => item.EventID == abort.EventID);
            if (completed is not null)
            {
                V4GroupTransactionTarget? completedTarget = completed.Targets.SingleOrDefault(item => item.PeerFingerprint == target);
                if (completedTarget is null || !MatchesAbort(completedTarget.Prepare, abort))
                    throw Invalid("membershipAbort 与已完成事务不匹配。");
                if (!completed.Aborted)
                {
                    state.AbortTombstones.Add(new(abort, Accepted: false));
                    return (false, true);
                }
                state.AbortTombstones.Add(new(abort, Accepted: true));
                return (true, true);
            }
            if (state.PendingIncoming.TryGetValue(abort.GroupID, out GroupControlMessage? prepare))
            {
                if (!MatchesAbort(prepare, abort)) throw Invalid("membershipAbort 与待处理 prepare 不匹配。");
                state.PendingIncoming.Remove(abort.GroupID);
            }
            else if (state.AcceptedInvitations.TryGetValue(abort.EventID, out GroupControlMessage? invite))
            {
                if (invite.GroupID != abort.GroupID || invite.OwnerFingerprint != abort.OwnerFingerprint ||
                    invite.Epoch != abort.Epoch || invite.TargetFingerprint != target)
                    throw Invalid("membershipAbort 与 accepted invite 不匹配。");
            }
            else
            {
                throw Invalid("membershipAbort 没有可验证的持久 prepare 或 invitation。");
            }
            state.AcceptedInvitations.Remove(abort.EventID);
            state.AbortTombstones.Add(new(abort, Accepted: true));
            return (true, true);
        });
    }

    /// <summary>Begins an owner-authorized roster change and durably queues its prepare and commit controls.</summary>
    public V4PendingGroupTransaction BeginMembershipChange(Guid groupID, string ownerFingerprint,
        IReadOnlyList<GroupMember> proposedMembers, string? newOwnerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(proposedMembers);
        string owner = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        return Mutate(state =>
        {
            if (state.PendingOutgoing.ContainsKey(groupID) || state.PendingIncoming.ContainsKey(groupID))
                throw Invalid("该群已有待处理成员变更。");
            if (!state.Groups.TryGetValue(groupID, out GroupConversation? current) ||
                current.OwnerFingerprint != owner)
                throw Invalid("只有当前群主可以修改成员名单。");
            V4PendingGroupTransaction transaction = BuildTransaction(current, owner, proposedMembers, newOwnerFingerprint);
            state.PendingOutgoing.Add(groupID, transaction);
            return (transaction, true);
        });
    }

    /// <summary>
    /// Starts the selected owner's exit. A random remaining member becomes owner; with no
    /// remaining member, only the active group record is deleted and message history remains.
    /// </summary>
    /// <summary>Durably enters the abort phase only while the transaction is still preparing.</summary>
    public bool BeginAbortMembershipChange(Guid groupID, string ownerFingerprint)
    {
        string owner = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        return Mutate(state =>
        {
            if (!state.PendingOutgoing.TryGetValue(groupID, out V4PendingGroupTransaction? transaction) ||
                transaction.OwnerFingerprint != owner)
                throw Invalid("没有可由本机取消的成员变更事务。");
            if (transaction.Stage == V4GroupMembershipStage.Aborting) return (false, false);
            if (transaction.Stage != V4GroupMembershipStage.Preparing)
                throw Invalid("成员变更已进入提交阶段，不能取消。");
            state.PendingOutgoing[groupID] = transaction with { Stage = V4GroupMembershipStage.Aborting };
            return (true, true);
        });
    }

    public V4GroupOwnerLeaveResult LeaveAsOwner(Guid groupID, string ownerFingerprint)
    {
        string owner = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        return Mutate(state =>
        {
            if (!state.Groups.TryGetValue(groupID, out GroupConversation? current) ||
                current.OwnerFingerprint != owner)
                throw Invalid("只有当前群主可以退出并转让群主。");
            if (state.PendingOutgoing.ContainsKey(groupID) || state.PendingIncoming.ContainsKey(groupID))
                throw Invalid("群组成员变更尚未完成，暂时不能退出。");

            GroupMember[] remaining = current.Members.Where(member => member.Fingerprint != owner).ToArray();
            if (remaining.Length == 0)
            {
                state.Groups.Remove(groupID);
                return (new V4GroupOwnerLeaveResult(true, null, null, []), true);
            }

            GroupMember selected = remaining[RandomNumberGenerator.GetInt32(remaining.Length)];
            V4PendingGroupTransaction transaction = BuildTransaction(current, owner, remaining, selected.Fingerprint);
            state.PendingOutgoing.Add(groupID, transaction);
            GroupControlMessage[] prepares = transaction.Targets.Select(target => target.Prepare).ToArray();
            return (new V4GroupOwnerLeaveResult(false, transaction.ProposedGroup,
                selected.Fingerprint, Array.AsReadOnly(prepares)), true);
        });
    }

    /// <summary>Returns stable, unacknowledged controls for network retry after reconnect or reload.</summary>
    public IReadOnlyList<GroupControlMessage> GetPendingMessages(Guid groupID)
    {
        return Read(state =>
        {
            if (!state.PendingOutgoing.TryGetValue(groupID, out V4PendingGroupTransaction? transaction))
                return Array.AsReadOnly(Array.Empty<GroupControlMessage>());
            GroupControlMessage[] messages = transaction.Stage switch
            {
                V4GroupMembershipStage.Preparing => transaction.Targets.Where(target => !target.Prepared)
                    .Select(target => target.Prepare).ToArray(),
                V4GroupMembershipStage.Committing => transaction.Targets.Where(target => !target.Committed)
                    .Select(target => target.Commit).ToArray(),
                V4GroupMembershipStage.Aborting => transaction.Targets.Where(target => !target.Aborted)
                    .Select(target => new GroupControlMessage(GroupControlKind.MembershipAbort,
                        target.Prepare.GroupID, transaction.OwnerFingerprint, target.Prepare.Epoch,
                        transaction.EventID, target.Prepare.BaseEpoch, target.PeerFingerprint)).ToArray(),
                _ => throw Invalid("成员变更事务阶段无效。")
            };
            return Array.AsReadOnly(messages);
        });
    }

    /// <summary>Records a prepare reply. Once all members accept, the durable retry stage advances to commit.</summary>
    public bool RecordPrepareReply(GroupControlMessage reply, string peerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string peer = V4GroupWire.NormalizeFingerprint(peerFingerprint);
        ValidateReply(reply, GroupControlKind.MembershipPrepareReply, peer);
        return Mutate(state =>
        {
            if (!state.PendingOutgoing.TryGetValue(reply.GroupID, out V4PendingGroupTransaction? transaction))
            {
                ValidateCompletedReply(state, reply, peer, GroupControlKind.MembershipPrepareReply);
                return (false, false);
            }
            ValidateTransactionReply(transaction, reply, peer, GroupControlKind.MembershipPrepareReply);
            V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == peer);
            if (transaction.Stage == V4GroupMembershipStage.Aborting) return (false, false);
            if (reply.Accepted == true && target.Prepared) return (false, false);
            if (transaction.Stage != V4GroupMembershipStage.Preparing)
                throw Invalid("commit 阶段收到延迟 prepare 回执。");
            if (reply.Accepted == true)
            {
                if (target.PrepareRejected) throw Invalid("成员对同一 prepare 返回了冲突结果。");
                V4GroupTransactionTarget[] targets = transaction.Targets.Select(item =>
                    item.PeerFingerprint == peer ? item with { Prepared = true } : item).ToArray();
                V4GroupMembershipStage stage = targets.All(item => item.Prepared)
                    ? V4GroupMembershipStage.Committing
                    : V4GroupMembershipStage.Preparing;
                state.PendingOutgoing[reply.GroupID] = transaction with
                { Stage = stage, Targets = Array.AsReadOnly(targets) };
                return (stage == V4GroupMembershipStage.Committing, true);
            }
            if (target.Prepared) throw Invalid("已准备成员随后返回了拒绝结果。");
            if (target.PrepareRejected) return (false, false);
            V4GroupTransactionTarget[] rejectedTargets = transaction.Targets.Select(item =>
                item.PeerFingerprint == peer ? item with { PrepareRejected = true } : item).ToArray();
            state.PendingOutgoing[reply.GroupID] = transaction with
            { Stage = V4GroupMembershipStage.Aborting, Targets = Array.AsReadOnly(rejectedTargets) };
            return (true, true);
        });
    }

    /// <summary>Records a commit reply and applies the proposed roster only after every target acknowledges it.</summary>
    public bool RecordCommitReply(GroupControlMessage reply, string peerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string peer = V4GroupWire.NormalizeFingerprint(peerFingerprint);
        ValidateReply(reply, GroupControlKind.MembershipCommitReply, peer);
        if (reply.Accepted != true) throw Invalid("成员拒绝了 membershipCommit。");
        return Mutate(state =>
        {
            if (!state.PendingOutgoing.TryGetValue(reply.GroupID, out V4PendingGroupTransaction? transaction))
            {
                ValidateCompletedReply(state, reply, peer, GroupControlKind.MembershipCommitReply);
                return (false, false);
            }
            ValidateTransactionReply(transaction, reply, peer, GroupControlKind.MembershipCommitReply);
            if (transaction.Stage != V4GroupMembershipStage.Committing)
                throw Invalid("不能在全部 prepare 回执到达前记录 commit 回执。");
            V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == peer);
            if (target.Committed) return (false, false);

            V4GroupTransactionTarget[] targets = transaction.Targets.Select(item =>
                item.PeerFingerprint == peer ? item with { Committed = true } : item).ToArray();
            if (targets.All(item => item.Committed))
            {
                state.Groups[reply.GroupID] = transaction.ProposedGroup;
                state.PendingOutgoing.Remove(reply.GroupID);
                AddCompleted(state, new V4CompletedGroupTransaction(reply.GroupID, transaction.EventID,
                    transaction.ProposedGroup.Revision, transaction.OwnerFingerprint,
                    transaction.ProposedGroup, Array.AsReadOnly(targets)));
                return (true, true);
            }

            state.PendingOutgoing[reply.GroupID] = transaction with { Targets = Array.AsReadOnly(targets) };
            return (false, true);
        });
    }

    /// <summary>Records a durable abort acknowledgment and completes cancellation only after every original target agrees.</summary>
    public bool RecordAbortReply(GroupControlMessage reply, string peerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string peer = V4GroupWire.NormalizeFingerprint(peerFingerprint);
        ValidateReply(reply, GroupControlKind.MembershipAbortReply, peer);
        if (reply.Accepted != true) throw Invalid("成员尚未确认取消；事务仍需恢复。");
        return Mutate(state =>
        {
            if (!state.PendingOutgoing.TryGetValue(reply.GroupID, out V4PendingGroupTransaction? transaction))
            {
                ValidateCompletedReply(state, reply, peer, GroupControlKind.MembershipAbortReply);
                return (false, false);
            }
            ValidateTransactionReply(transaction, reply, peer, GroupControlKind.MembershipAbortReply);
            if (transaction.Stage != V4GroupMembershipStage.Aborting)
                throw Invalid("事务未进入安全取消阶段。");
            V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == peer);
            if (target.Aborted) return (false, false);
            V4GroupTransactionTarget[] targets = transaction.Targets.Select(item =>
                item.PeerFingerprint == peer ? item with { Aborted = true } : item).ToArray();
            if (targets.All(item => item.Aborted))
            {
                GroupConversation current = state.Groups[reply.GroupID];
                AddCompleted(state, new V4CompletedGroupTransaction(reply.GroupID, transaction.EventID,
                    transaction.ProposedGroup.Revision, transaction.OwnerFingerprint, current,
                    Array.AsReadOnly(targets), Aborted: true));
                state.PendingOutgoing.Remove(reply.GroupID);
                return (true, true);
            }
            state.PendingOutgoing[reply.GroupID] = transaction with { Targets = Array.AsReadOnly(targets) };
            return (false, true);
        });
    }

    private T Read<T>(Func<StoreState, T> read)
    {
        lock (_instanceLock)
        lock (_pathLock)
        {
            _state = ReadState(_path);
            return read(_state);
        }
    }

    private T Mutate<T>(Func<StoreState, (T Result, bool Changed)> mutation)
    {
        lock (_instanceLock)
        lock (_pathLock)
        {
            StoreState next = ReadState(_path).Clone();
            (T result, bool changed) = mutation(next);
            if (changed) Persist(next, _path);
            _state = changed ? next : ReadState(_path);
            return result;
        }
    }

    private static V4PendingGroupTransaction BuildTransaction(GroupConversation current, string owner,
        IReadOnlyList<GroupMember> proposedMembers, string? newOwnerFingerprint)
    {
        if (current.Revision == ulong.MaxValue) throw Invalid("群组 revision 已达到上限。");
        if (newOwnerFingerprint is not null && proposedMembers.Any(member => member.Fingerprint == owner))
            throw Invalid("群主转让时必须从新名单中移除原群主。");
        GroupConversation proposed = new(current.Id, current.Name,
            newOwnerFingerprint ?? current.OwnerFingerprint, current.Revision + 1, proposedMembers,
            newOwnerFingerprint is null ? null : owner);
        var peers = current.Members.Select(member => member.Fingerprint)
            .Concat(proposed.Members.Select(member => member.Fingerprint))
            .Where(fingerprint => fingerprint != owner)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (peers.Length == 0) throw Invalid("成员变更至少需要一名远端成员。");

        Guid eventID = Guid.NewGuid();
        V4GroupTransactionTarget[] targets = peers.Select(peer =>
        {
            var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, current.Id,
                owner, proposed.Revision, eventID, current.Revision, peer,
                newOwnerFingerprint, proposed.Name, proposed.Members);
            var commit = new GroupControlMessage(GroupControlKind.MembershipCommit, current.Id,
                owner, proposed.Revision, eventID, current.Revision, peer,
                newOwnerFingerprint, proposed.Name, proposed.Members);
            return new V4GroupTransactionTarget(peer, prepare, commit);
        }).ToArray();
        return new V4PendingGroupTransaction(proposed, owner, current.Revision, eventID,
            V4GroupMembershipStage.Preparing, Array.AsReadOnly(targets));
    }

    private static bool MatchesInitialInvitation(GroupControlMessage invite, GroupControlMessage membership,
        string localFingerprint) =>
        invite.Kind == GroupControlKind.Invite &&
        membership.Kind is GroupControlKind.MembershipPrepare or GroupControlKind.MembershipCommit &&
        invite.EventID == membership.EventID && invite.GroupID == membership.GroupID &&
        invite.OwnerFingerprint == membership.OwnerFingerprint && invite.Epoch == membership.Epoch &&
        invite.TargetFingerprint == localFingerprint && membership.TargetFingerprint == localFingerprint &&
        invite.Name == membership.Name && SameRoster(invite.Roster, membership.Roster) &&
        membership.NewOwnerFingerprint is null;

    private static void ValidateIncomingTarget(GroupControlMessage message, GroupControlKind expectedKind, string local)
    {
        if (message.Kind != expectedKind || message.TargetFingerprint != local ||
            message.Roster is null || message.Name is null)
            throw Invalid("成员变更目标或名单无效。");
    }

    private static void ValidateNextRoster(GroupConversation current, GroupControlMessage message)
    {
        if (message.OwnerFingerprint != current.OwnerFingerprint || message.BaseEpoch != current.Revision ||
            current.Revision == ulong.MaxValue || message.Epoch != current.Revision + 1 ||
            message.Name != current.Name || message.Roster is null || message.Name is null)
            throw Invalid("群主、名称或成员 epoch 与当前群状态不匹配。");
        if (message.NewOwnerFingerprint is not null &&
            message.Roster.Any(member => member.Fingerprint == message.OwnerFingerprint))
            throw Invalid("群主转让的新名单仍包含原群主。");
        _ = CreateProposedGroup(message);
    }

    private static GroupConversation CreateProposedGroup(GroupControlMessage message)
    {
        if (message.Roster is null || message.Name is null) throw Invalid("成员变更缺少 roster 或群名。");
        return new GroupConversation(message.GroupID, message.Name,
            message.NewOwnerFingerprint ?? message.OwnerFingerprint, message.Epoch, message.Roster,
            message.NewOwnerFingerprint is null ? null : message.OwnerFingerprint);
    }

    private static bool MatchesAbort(GroupControlMessage prepare, GroupControlMessage abort) =>
        (prepare.Kind is GroupControlKind.MembershipPrepare or GroupControlKind.MembershipCommit) && abort.Kind == GroupControlKind.MembershipAbort &&
        prepare.EventID == abort.EventID && prepare.GroupID == abort.GroupID &&
        prepare.OwnerFingerprint == abort.OwnerFingerprint && prepare.Epoch == abort.Epoch &&
        prepare.BaseEpoch == abort.BaseEpoch && prepare.TargetFingerprint == abort.TargetFingerprint;

    private static bool IsMatchingTransaction(GroupControlMessage prepare, GroupControlMessage commit) =>
        prepare.Kind == GroupControlKind.MembershipPrepare && commit.Kind == GroupControlKind.MembershipCommit &&
        prepare.EventID == commit.EventID && prepare.GroupID == commit.GroupID &&
        prepare.OwnerFingerprint == commit.OwnerFingerprint && prepare.Epoch == commit.Epoch &&
        prepare.BaseEpoch == commit.BaseEpoch && prepare.TargetFingerprint == commit.TargetFingerprint &&
        prepare.NewOwnerFingerprint == commit.NewOwnerFingerprint && prepare.Name == commit.Name &&
        SameRoster(prepare.Roster, commit.Roster);

    private static void ValidateReply(GroupControlMessage reply, GroupControlKind expectedKind, string peer)
    {
        if (reply.Kind != expectedKind || reply.TargetFingerprint != peer || reply.Accepted is null)
            throw Invalid("群成员回执类型或目标无效。");
    }

    private static void ValidateTransactionReply(V4PendingGroupTransaction transaction,
        GroupControlMessage reply, string peer, GroupControlKind replyKind)
    {
        if (transaction.EventID != reply.EventID || transaction.ProposedGroup.Id != reply.GroupID ||
            transaction.ProposedGroup.Revision != reply.Epoch || transaction.OwnerFingerprint != reply.OwnerFingerprint ||
            !transaction.Targets.Any(target => target.PeerFingerprint == peer))
            throw Invalid("群成员回执与待处理事务不匹配。");
        V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == peer);
        GroupControlMessage request = replyKind switch
        {
            GroupControlKind.MembershipPrepareReply => target.Prepare,
            GroupControlKind.MembershipCommitReply => target.Commit,
            GroupControlKind.MembershipAbortReply => new GroupControlMessage(GroupControlKind.MembershipAbort,
                target.Prepare.GroupID, transaction.OwnerFingerprint, target.Prepare.Epoch,
                transaction.EventID, target.Prepare.BaseEpoch, target.PeerFingerprint),
            _ => throw Invalid("成员回执类型无效。")
        };
        if (request.TargetFingerprint != reply.TargetFingerprint || request.EventID != reply.EventID ||
            request.GroupID != reply.GroupID || request.OwnerFingerprint != reply.OwnerFingerprint || request.Epoch != reply.Epoch)
            throw Invalid("群成员回执与对应请求不匹配。");
    }

    private static void ValidateCompletedReply(StoreState state, GroupControlMessage reply,
        string peer, GroupControlKind replyKind)
    {
        V4CompletedGroupTransaction? completed = state.Completed.FirstOrDefault(item => item.EventID == reply.EventID);
        if (completed is null || completed.GroupID != reply.GroupID || completed.Epoch != reply.Epoch ||
            completed.OwnerFingerprint != reply.OwnerFingerprint)
            throw Invalid("群成员回执没有对应的待处理或已完成事务。");
        V4GroupTransactionTarget? target = completed.Targets.FirstOrDefault(item => item.PeerFingerprint == peer);
        GroupControlMessage? expected = replyKind switch
        {
            GroupControlKind.MembershipPrepareReply => target?.Prepare,
            GroupControlKind.MembershipCommitReply when !completed.Aborted => target?.Commit,
            GroupControlKind.MembershipAbortReply when completed.Aborted && target is not null =>
                new GroupControlMessage(GroupControlKind.MembershipAbort, target.Prepare.GroupID,
                    completed.OwnerFingerprint, target.Prepare.Epoch, completed.EventID,
                    target.Prepare.BaseEpoch, target.PeerFingerprint),
            _ => null
        };
        if (expected is null || expected.TargetFingerprint != peer || reply.TargetFingerprint != peer || reply.Accepted != true)
            throw Invalid("重复群成员回执与已完成事务不匹配。");
    }

    private static bool TryGetCompletedTarget(StoreState state, Guid eventID, string local,
        out V4GroupTransactionTarget? target)
    {
        V4CompletedGroupTransaction? completed = state.Completed.FirstOrDefault(item => item.EventID == eventID);
        target = completed?.Targets.FirstOrDefault(item => item.PeerFingerprint == local);
        return target is not null;
    }

    private static void AddCompleted(StoreState state, V4CompletedGroupTransaction completed)
    {
        state.Completed.RemoveAll(item => item.EventID == completed.EventID);
        state.Completed.Add(completed);
        if (state.Completed.Count > MaximumCompletedTransactions)
            state.Completed.RemoveRange(0, state.Completed.Count - MaximumCompletedTransactions);
    }

    private static bool SameControl(GroupControlMessage left, GroupControlMessage right) =>
        V4GroupWire.EncodeCanonical(left).AsSpan().SequenceEqual(V4GroupWire.EncodeCanonical(right));

    private static bool SameGroup(GroupConversation left, GroupConversation right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, ArchiveJson).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, ArchiveJson));

    private static bool SameMessage(GroupMessageEnvelope left, GroupMessageEnvelope right) =>
        V4GroupWire.EncodeCanonical(left).AsSpan().SequenceEqual(V4GroupWire.EncodeCanonical(right));

    private static bool SameRoster(IReadOnlyList<GroupMember>? left, IReadOnlyList<GroupMember>? right)
    {
        if (left is null || right is null || left.Count != right.Count) return left is null && right is null;
        for (int index = 0; index < left.Count; index++)
            if (left[index] != right[index]) return false;
        return true;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private static StoreState ReadState(string path)
    {
        if (!File.Exists(path)) return new StoreState();
        var info = new FileInfo(path);
        if (info.Length > MaximumArchiveBytes) throw Invalid("群组归档超过允许大小，原文件已保留。");
        Archive archive;
        try
        {
            archive = JsonSerializer.Deserialize<Archive>(File.ReadAllBytes(path), ArchiveJson)
                ?? throw Invalid("群组归档内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("群组归档格式无效，原文件已保留。", exception);
        }
        if (archive.Version is not (1 or ArchiveVersion) || archive.Groups is null || archive.Messages is null ||
            archive.PendingIncoming is null || archive.AcceptedInvitations is null ||
            archive.PendingOutgoing is null || archive.Completed is null || archive.AbortTombstones is null)
            throw Invalid("群组归档版本或字段无效，原文件已保留。");

        var state = new StoreState();
        foreach (GroupConversation group in archive.Groups)
        {
            if (group is null || !state.Groups.TryAdd(group.Id, group))
                throw Invalid("群组归档包含空值或重复群 ID，原文件已保留。");
        }
        foreach (GroupMessageEnvelope message in archive.Messages)
        {
            if (message is null) throw Invalid("群组消息归档包含空值，原文件已保留。");
            // Message history may outlive the active group after the last member dissolves it.
            if (state.Messages.Any(existing => existing.GroupID == message.GroupID && existing.Id == message.Id &&
                                               existing.SenderFingerprint == message.SenderFingerprint))
                throw Invalid("群组消息归档包含重复消息键，原文件已保留。");
            state.Messages.Add(message);
        }
        foreach (GroupControlMessage prepare in archive.PendingIncoming)
        {
            if (prepare is null || prepare.Kind != GroupControlKind.MembershipPrepare ||
                !state.PendingIncoming.TryAdd(prepare.GroupID, prepare))
                throw Invalid("群组归档包含无效或重复的 pending prepare，原文件已保留。");
        }
        foreach (GroupControlMessage invite in archive.AcceptedInvitations)
        {
            if (invite is null || invite.Kind != GroupControlKind.Invite ||
                !state.AcceptedInvitations.TryAdd(invite.EventID, invite))
                throw Invalid("群组归档包含无效或重复的 accepted invite，原文件已保留。");
        }
        foreach (V4PendingGroupTransaction transaction in archive.PendingOutgoing)
        {
            ValidatePendingTransaction(transaction);
            if (!state.PendingOutgoing.TryAdd(transaction.ProposedGroup.Id, transaction))
                throw Invalid("群组归档包含重复的 pending transaction，原文件已保留。");
        }
        foreach (V4CompletedGroupTransaction transaction in archive.Completed)
        {
            if (transaction is null || transaction.Targets is null || transaction.Targets.Count == 0 ||
                state.Completed.Any(item => item.EventID == transaction.EventID))
                throw Invalid("群组归档包含无效的已完成事务记录，原文件已保留。");
            if (transaction.Aborted ? transaction.Targets.Any(target => !target.Aborted) : transaction.Targets.Any(target => !target.Committed))
                throw Invalid("已完成事务缺少全部目标的终态回执，原文件已保留。");
            state.Completed.Add(transaction);
        }
        foreach (V4GroupAbortTombstone tombstone in archive.AbortTombstones)
        {
            if (tombstone is null || tombstone.Abort is null ||
                tombstone.Abort.Kind != GroupControlKind.MembershipAbort || tombstone.Abort.TargetFingerprint is null ||
                state.AbortTombstones.Any(item => item.Abort.EventID == tombstone.Abort.EventID))
                throw Invalid("群组归档包含无效或重复的 abort tombstone，原文件已保留。");
            state.AbortTombstones.Add(tombstone);
        }
        if (state.Completed.Count > MaximumCompletedTransactions)
            state.Completed.RemoveRange(0, state.Completed.Count - MaximumCompletedTransactions);
        ValidatePendingAgainstGroups(state);
        return state;
    }

    private static void ValidatePendingTransaction(V4PendingGroupTransaction transaction)
    {
        if (transaction is null || transaction.Targets is null || transaction.Targets.Count == 0 ||
            transaction.EventID == Guid.Empty || transaction.ProposedGroup is null ||
            transaction.ProposedGroup.Revision == 0 || transaction.ProposedGroup.Revision - 1 != transaction.BaseEpoch ||
            transaction.OwnerFingerprint != transaction.Targets[0].Prepare.OwnerFingerprint ||
            transaction.Targets.Select(target => target.PeerFingerprint).Distinct(StringComparer.Ordinal).Count() != transaction.Targets.Count)
            throw Invalid("pending membership transaction 格式无效。");
        foreach (V4GroupTransactionTarget target in transaction.Targets)
        {
            if (target is null || target.PeerFingerprint != V4GroupWire.NormalizeFingerprint(target.PeerFingerprint) ||
                !IsMatchingTransaction(target.Prepare, target.Commit) ||
                target.Prepare.EventID != transaction.EventID || target.Prepare.GroupID != transaction.ProposedGroup.Id ||
                target.Prepare.OwnerFingerprint != transaction.OwnerFingerprint || target.Prepare.Epoch != transaction.ProposedGroup.Revision ||
                target.Prepare.BaseEpoch != transaction.BaseEpoch || target.Prepare.TargetFingerprint != target.PeerFingerprint ||
                target.Prepared != (transaction.Stage == V4GroupMembershipStage.Committing || target.Prepared) ||
                target.Committed && (!target.Prepared || transaction.Stage != V4GroupMembershipStage.Committing) ||
                 target.Aborted && transaction.Stage != V4GroupMembershipStage.Aborting ||
                 target.PrepareRejected && transaction.Stage != V4GroupMembershipStage.Aborting ||
                 target.Committed && target.Aborted)
                throw Invalid("pending membership target 与事务阶段不匹配。");
        }
        if (transaction.Stage == V4GroupMembershipStage.Committing && transaction.Targets.Any(target => !target.Prepared))
            throw Invalid("commit 阶段仍有成员未确认 prepare。");
    }

    private static void ValidatePendingAgainstGroups(StoreState state)
    {
        foreach ((Guid groupID, GroupControlMessage prepare) in state.PendingIncoming)
        {
            if (state.Groups.TryGetValue(groupID, out GroupConversation? group))
            {
                if (prepare.GroupID != groupID || prepare.OwnerFingerprint != group.OwnerFingerprint ||
                    group.Revision == ulong.MaxValue || prepare.BaseEpoch != group.Revision ||
                    prepare.Epoch != group.Revision + 1)
                    throw Invalid("pending prepare 与已提交群状态不匹配，原文件已保留。");
            }
            else if (!state.AcceptedInvitations.TryGetValue(prepare.EventID, out GroupControlMessage? invite) ||
                     prepare.TargetFingerprint is null || !MatchesInitialInvitation(invite, prepare, prepare.TargetFingerprint))
            {
                throw Invalid("初始 pending prepare 缺少匹配的 accepted invite，原文件已保留。");
            }
        }
        foreach ((Guid groupID, V4PendingGroupTransaction transaction) in state.PendingOutgoing)
        {
            if (!state.Groups.TryGetValue(groupID, out GroupConversation? group) ||
                transaction.OwnerFingerprint != group.OwnerFingerprint || transaction.BaseEpoch != group.Revision)
                throw Invalid("pending transaction 与已提交群状态不匹配，原文件已保留。");
        }
    }

    private static void Persist(StoreState state, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw Invalid("群组归档路径无效。");
        Directory.CreateDirectory(directory);
        var archive = new Archive
        {
            Version = ArchiveVersion,
            Groups = state.Groups.Values.OrderBy(group => group.Id).ToArray(),
            Messages = state.Messages.ToArray(),
            PendingIncoming = state.PendingIncoming.Values.OrderBy(message => message.GroupID).ToArray(),
            AcceptedInvitations = state.AcceptedInvitations.Values.OrderBy(message => message.EventID).ToArray(),
            PendingOutgoing = state.PendingOutgoing.Values.OrderBy(transaction => transaction.ProposedGroup.Id).ToArray(),
            Completed = state.Completed.ToArray(),
            AbortTombstones = state.AbortTombstones.ToArray()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(archive, ArchiveJson);
        if (bytes.Length > MaximumArchiveBytes) throw Invalid("群组归档超过允许大小。");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class StoreState
    {
        public Dictionary<Guid, GroupConversation> Groups { get; } = [];
        public List<GroupMessageEnvelope> Messages { get; } = [];
        public Dictionary<Guid, GroupControlMessage> PendingIncoming { get; } = [];
        public Dictionary<Guid, GroupControlMessage> AcceptedInvitations { get; } = [];
        public Dictionary<Guid, V4PendingGroupTransaction> PendingOutgoing { get; } = [];
        public List<V4CompletedGroupTransaction> Completed { get; } = [];
        public List<V4GroupAbortTombstone> AbortTombstones { get; } = [];

        public StoreState Clone()
        {
            var clone = new StoreState();
            foreach ((Guid id, GroupConversation group) in Groups) clone.Groups.Add(id, group);
            clone.Messages.AddRange(Messages);
            foreach ((Guid id, GroupControlMessage message) in PendingIncoming) clone.PendingIncoming.Add(id, message);
            foreach ((Guid id, GroupControlMessage message) in AcceptedInvitations) clone.AcceptedInvitations.Add(id, message);
            foreach ((Guid id, V4PendingGroupTransaction transaction) in PendingOutgoing) clone.PendingOutgoing.Add(id, transaction);
            clone.Completed.AddRange(Completed);
            clone.AbortTombstones.AddRange(AbortTombstones);
            return clone;
        }
    }

    private sealed class Archive
    {
        public int Version { get; init; }
        public GroupConversation[]? Groups { get; init; } = [];
        public GroupMessageEnvelope[]? Messages { get; init; } = [];
        public GroupControlMessage[]? PendingIncoming { get; init; } = [];
        public GroupControlMessage[]? AcceptedInvitations { get; init; } = [];
        public V4PendingGroupTransaction[]? PendingOutgoing { get; init; } = [];
        public V4CompletedGroupTransaction[]? Completed { get; init; } = [];
        public V4GroupAbortTombstone[]? AbortTombstones { get; init; } = [];
    }
}
