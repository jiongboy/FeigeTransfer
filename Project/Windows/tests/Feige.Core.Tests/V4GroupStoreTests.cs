using System.Collections.Concurrent;
using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class V4GroupStoreTests : IDisposable
{
    private const string Owner = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string MemberB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string MemberC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string MemberD = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v4-group-store", Guid.NewGuid().ToString("N"));
    private string ArchivePath => Path.Combine(_root, "groups.json");

    [Fact]
    public void ConcurrentUpdatesAcrossStoreInstances_DoNotLoseWrites()
    {
        var first = new V4GroupStore(ArchivePath);
        first.AddGroup(Group());
        var second = new V4GroupStore(ArchivePath);
        var messages = Enumerable.Range(0, 48).Select(index => Message(index)).ToArray();
        var failures = new ConcurrentQueue<Exception>();

        Parallel.ForEach(messages, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (message, state) =>
        {
            try
            {
                if ((message.Id.GetHashCode() & 1) == 0) first.AddMessage(message);
                else second.AddMessage(message);
            }
            catch (Exception exception) { failures.Enqueue(exception); }
        });

        Assert.Empty(failures);
        Assert.Equal(messages.Length, new V4GroupStore(ArchivePath).GetMessages(GroupId).Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void AddOutgoingMessage_RejectsPendingMembershipTransactionWithoutPersisting()
    {
        var store = new V4GroupStore(ArchivePath);
        store.AddGroup(Group());
        store.BeginMembershipChange(GroupId, Owner, [Member(Owner), Member(MemberB), Member(MemberC)]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => store.AddOutgoingMessage(Message(101)));

        Assert.Contains("成员变更", error.Message, StringComparison.Ordinal);
        Assert.Empty(new V4GroupStore(ArchivePath).GetMessages(GroupId));
    }

    [Fact]
    public void AddIncomingMessage_RejectsPendingMembershipTransactionWithoutPersisting()
    {
        var store = new V4GroupStore(ArchivePath);
        store.AddGroup(Group());
        store.BeginMembershipChange(GroupId, Owner, [Member(Owner), Member(MemberB), Member(MemberC)]);

        InvalidOperationException error = Assert.ThrowsAny<InvalidOperationException>(
            () => store.AddIncomingMessage(Message(102)));

        Assert.Contains("成员变更", error.Message, StringComparison.Ordinal);
        Assert.Empty(new V4GroupStore(ArchivePath).GetMessages(GroupId));
    }

    [Fact]
    public void CreateGroupAndBeginMembershipChange_PersistsGroupAndPendingTransactionTogether()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation initial = new(GroupId, "new group", Owner, 0, [Member(Owner)]);

        V4PendingGroupTransaction transaction = store.CreateGroupAndBeginMembershipChange(initial, Owner,
            [Member(Owner), Member(MemberB)]);

        var reloaded = new V4GroupStore(ArchivePath);
        GroupConversation saved = Assert.IsType<GroupConversation>(reloaded.GetGroup(GroupId));
        Assert.Equal(initial.Id, saved.Id);
        Assert.Equal(initial.Name, saved.Name);
        Assert.Equal(initial.OwnerFingerprint, saved.OwnerFingerprint);
        Assert.Equal(initial.Revision, saved.Revision);
        Assert.Equal(initial.Members.Select(member => member.Fingerprint), saved.Members.Select(member => member.Fingerprint));
        V4PendingGroupTransaction pending = Assert.IsType<V4PendingGroupTransaction>(reloaded.GetPendingTransaction(GroupId));
        Assert.Equal(transaction.EventID, pending.EventID);
        Assert.Equal(Member(MemberB).Fingerprint, Assert.Single(pending.Targets).PeerFingerprint);
    }

    [Fact]
    public void CreateGroupAndBeginMembershipChange_InvalidRosterDoesNotPersistOrphanGroup()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation initial = new(GroupId, "new group", Owner, 0, [Member(Owner)]);

        Assert.Throws<InvalidDataException>(() => store.CreateGroupAndBeginMembershipChange(initial, Owner,
            [Member(Owner), Member(MemberB), Member(MemberB)]));

        var reloaded = new V4GroupStore(ArchivePath);
        Assert.Null(reloaded.GetGroup(GroupId));
        Assert.Null(reloaded.GetPendingTransaction(GroupId));
    }

    [Fact]
    public void IncomingPrepareAndCommit_ArePersistedAndIdempotentAcrossReload()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        store.AddGroup(original);
        GroupControlMessage prepare = MembershipControl(GroupControlKind.MembershipPrepare, original,
            [Member(Owner), Member(MemberB)], MemberB);

        GroupControlMessage prepareReply = store.PrepareMembership(prepare, MemberB);
        Assert.True(prepareReply.Accepted);
        Assert.Equal(prepare.EventID, Assert.Single(new V4GroupStore(ArchivePath).PendingMembershipPrepares).EventID);
        Assert.Equal(prepareReply, new V4GroupStore(ArchivePath).PrepareMembership(prepare, MemberB));

        var reloaded = new V4GroupStore(ArchivePath);
        GroupControlMessage commit = MembershipControl(GroupControlKind.MembershipCommit, original,
            [Member(Owner), Member(MemberB)], MemberB, prepare.EventID);
        GroupControlMessage commitReply = reloaded.CommitMembership(commit, MemberB);
        Assert.True(commitReply.Accepted);
        Assert.Equal(1UL, new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Revision);
        Assert.Empty(new V4GroupStore(ArchivePath).PendingMembershipPrepares);
        Assert.Equal(commitReply, new V4GroupStore(ArchivePath).CommitMembership(commit, MemberB));
        Assert.Equal(1UL, new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Revision);
    }

    [Fact]
    public void OutgoingPrepareAndCommit_RetrySameControlsAfterReloadAndApplyAfterAllAcks()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        store.AddGroup(original);
        store.BeginMembershipChange(GroupId, Owner, [Member(Owner), Member(MemberB), Member(MemberC)]);
        var reloaded = new V4GroupStore(ArchivePath);
        V4PendingGroupTransaction pending = reloaded.GetPendingTransaction(GroupId)!;
        GroupControlMessage[] firstPrepare = reloaded.GetPendingMessages(GroupId).ToArray();
        Assert.Equal(2, firstPrepare.Length);
        Assert.Equal(firstPrepare.Select(ControlHex), new V4GroupStore(ArchivePath).GetPendingMessages(GroupId).Select(ControlHex));

        foreach (GroupControlMessage prepare in firstPrepare)
        {
            GroupControlMessage reply = Reply(prepare, GroupControlKind.MembershipPrepareReply);
            reloaded.RecordPrepareReply(reply, prepare.TargetFingerprint!);
            Assert.False(reloaded.RecordPrepareReply(reply, prepare.TargetFingerprint!));
        }
        Assert.Equal(V4GroupMembershipStage.Committing, reloaded.GetPendingTransaction(GroupId)!.Stage);
        GroupControlMessage[] commits = reloaded.GetPendingMessages(GroupId).ToArray();
        Assert.Equal(firstPrepare.Select(message => message.EventID), commits.Select(message => message.EventID));

        GroupControlMessage finalReply = Reply(commits[0], GroupControlKind.MembershipCommitReply);
        Assert.False(reloaded.RecordCommitReply(finalReply, commits[0].TargetFingerprint!));
        var afterPartialAck = new V4GroupStore(ArchivePath);
        Assert.Equal(0UL, afterPartialAck.GetGroup(GroupId)!.Revision);
        Assert.Single(afterPartialAck.GetPendingMessages(GroupId));
        Assert.True(afterPartialAck.RecordCommitReply(Reply(commits[1], GroupControlKind.MembershipCommitReply),
            commits[1].TargetFingerprint!));
        Assert.Equal(1UL, new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Revision);
        Assert.False(new V4GroupStore(ArchivePath).RecordCommitReply(finalReply, commits[0].TargetFingerprint!));
    }

    [Fact]
    public void LeaveAsOwner_SelectsRemainingMemberAndKeepsPrepareForRetry()
    {
        var store = new V4GroupStore(ArchivePath);
        store.AddGroup(Group(Member(Owner), Member(MemberB), Member(MemberC)));
        V4GroupOwnerLeaveResult result = store.LeaveAsOwner(GroupId, Owner);

        Assert.False(result.Dissolved);
        Assert.NotNull(result.NewOwnerFingerprint);
        Assert.DoesNotContain(result.ProposedGroup!.Members, member => member.Fingerprint == "AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA");
        Assert.Contains(result.ProposedGroup.Members, member => member.Fingerprint == result.NewOwnerFingerprint);
        Assert.Equal(result.PrepareMessages.Select(ControlHex), new V4GroupStore(ArchivePath).GetPendingMessages(GroupId).Select(ControlHex));
        Assert.All(result.PrepareMessages, message => Assert.Equal(result.NewOwnerFingerprint, message.NewOwnerFingerprint));
    }

    [Fact]
    public void LastOwnerLeave_DissolvesOnlyActiveGroupAndRetainsMessageHistory()
    {
        var store = new V4GroupStore(ArchivePath);
        store.AddGroup(Group(Member(Owner)));
        GroupMessageEnvelope history = Message(1);
        store.AddMessage(history);

        V4GroupOwnerLeaveResult result = store.LeaveAsOwner(GroupId, Owner);
        var reloaded = new V4GroupStore(ArchivePath);
        Assert.True(result.Dissolved);
        Assert.Null(reloaded.GetGroup(GroupId));
        Assert.Equal(history, Assert.Single(reloaded.GetMessages(GroupId)));
    }

    [Fact]
    public void AcceptedInvite_SeedsInitialPrepareAndCommitAcrossReload()
    {
        var store = new V4GroupStore(ArchivePath);
        Guid eventID = Guid.NewGuid();
        GroupMember[] roster = [Member(Owner), Member(MemberB), Member(MemberC)];
        GroupControlMessage invite = new(GroupControlKind.Invite, GroupId, Owner, 0, eventID,
            targetFingerprint: MemberC, name: "test", roster: roster);

        Assert.True(store.AcceptInvitation(invite, MemberC).Accepted);
        var afterInvite = new V4GroupStore(ArchivePath);
        Assert.Equal(ControlHex(invite), ControlHex(Assert.Single(afterInvite.AcceptedInvitations)));
        GroupControlMessage prepare = new(GroupControlKind.MembershipPrepare, GroupId, Owner, 0, eventID,
            targetFingerprint: MemberC, name: "test", roster: roster);
        Assert.True(afterInvite.PrepareMembership(prepare, MemberC).Accepted);
        Assert.True(new V4GroupStore(ArchivePath).PrepareMembership(prepare, MemberC).Accepted);

        GroupControlMessage commit = new(GroupControlKind.MembershipCommit, GroupId, Owner, 0, eventID,
            targetFingerprint: MemberC, name: "test", roster: roster);
        var afterPrepare = new V4GroupStore(ArchivePath);
        Assert.True(afterPrepare.CommitMembership(commit, MemberC).Accepted);
        Assert.Equal(roster, new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Members);
        Assert.Empty(new V4GroupStore(ArchivePath).AcceptedInvitations);
        Assert.True(new V4GroupStore(ArchivePath).CommitMembership(commit, MemberC).Accepted);
        Assert.True(new V4GroupStore(ArchivePath).AcceptInvitation(invite, MemberC).Accepted);
    }

    [Fact]
    public void AddedMember_MustAcceptInvitationBeforePrepareAndCommitsUpdatedRoster()
    {
        var ownerStore = new V4GroupStore(Path.Combine(_root, "owner.json"));
        var existingMemberStore = new V4GroupStore(Path.Combine(_root, "existing-member.json"));
        var newMemberStore = new V4GroupStore(Path.Combine(_root, "new-member.json"));
        GroupConversation original = Group(Member(Owner), Member(MemberB));
        ownerStore.AddGroup(original);
        existingMemberStore.AddGroup(original);

        GroupMember[] roster = [Member(Owner), Member(MemberB), Member(MemberC)];
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(GroupId, Owner, roster);
        V4GroupTransactionTarget newMemberTarget = transaction.Targets.Single(target =>
            target.PeerFingerprint == Member(MemberC).Fingerprint);

        Assert.Throws<InvalidDataException>(() =>
            newMemberStore.PrepareMembership(newMemberTarget.Prepare, MemberC));
        GroupControlMessage invitation = new(GroupControlKind.Invite, GroupId, Owner,
            transaction.ProposedGroup.Revision, transaction.EventID,
            targetFingerprint: MemberC, name: transaction.ProposedGroup.Name, roster: transaction.ProposedGroup.Members);
        Assert.True(newMemberStore.AcceptInvitation(invitation, MemberC).Accepted);

        foreach (V4GroupTransactionTarget target in transaction.Targets)
        {
            V4GroupStore peerStore = target.PeerFingerprint == Member(MemberB).Fingerprint
                ? existingMemberStore
                : newMemberStore;
            GroupControlMessage reply = peerStore.PrepareMembership(target.Prepare, target.PeerFingerprint);
            ownerStore.RecordPrepareReply(reply, target.PeerFingerprint);
        }

        Assert.Equal(V4GroupMembershipStage.Committing, ownerStore.GetPendingTransaction(GroupId)!.Stage);
        foreach (GroupControlMessage commit in ownerStore.GetPendingMessages(GroupId).ToArray())
        {
            V4GroupStore peerStore = commit.TargetFingerprint == Member(MemberB).Fingerprint
                ? existingMemberStore
                : newMemberStore;
            GroupControlMessage reply = peerStore.CommitMembership(commit, commit.TargetFingerprint!);
            ownerStore.RecordCommitReply(reply, commit.TargetFingerprint!);
        }

        Assert.Null(ownerStore.GetPendingTransaction(GroupId));
        Assert.Equal(roster, ownerStore.GetGroup(GroupId)!.Members);
        Assert.Equal(roster, existingMemberStore.GetGroup(GroupId)!.Members);
        Assert.Equal(roster, newMemberStore.GetGroup(GroupId)!.Members);
    }

    [Fact]
    public void TwentyMemberGroup_CreatesAndRemovesOneMemberAcrossAllDurableStores()
    {
        var ownerStore = new V4GroupStore(Path.Combine(_root, "owner.json"));
        GroupConversation initial = new(GroupId, "max group", Owner, 0, [Member(Owner)]);
        string[] remoteFingerprints = Enumerable.Range(1, GroupConversation.MaximumMembers - 1)
            .Select(index => index.ToString("X64")).ToArray();
        GroupMember[] fullRoster = [Member(Owner), .. remoteFingerprints.Select(Member)];
        var peerStores = remoteFingerprints.ToDictionary(fingerprint => Member(fingerprint).Fingerprint,
            fingerprint => new V4GroupStore(Path.Combine(_root, $"member-{fingerprint}.json")),
            StringComparer.Ordinal);

        Assert.Throws<InvalidDataException>(() => new GroupConversation(GroupId, "too many", Owner, 0,
            [Member(Owner), .. Enumerable.Range(1, GroupConversation.MaximumMembers).Select(index => Member(index.ToString("X64")))]));

        V4PendingGroupTransaction creation = ownerStore.CreateGroupAndBeginMembershipChange(initial, Owner, fullRoster);
        Assert.Equal(GroupConversation.MaximumMembers - 1, creation.Targets.Count);
        foreach (V4GroupTransactionTarget target in creation.Targets)
        {
            V4GroupStore peerStore = peerStores[target.PeerFingerprint];
            var invite = new GroupControlMessage(GroupControlKind.Invite, GroupId, Owner,
                creation.ProposedGroup.Revision, creation.EventID,
                targetFingerprint: target.PeerFingerprint, name: creation.ProposedGroup.Name,
                roster: creation.ProposedGroup.Members);
            Assert.True(peerStore.AcceptInvitation(invite, target.PeerFingerprint).Accepted);
            GroupControlMessage prepareReply = peerStore.PrepareMembership(target.Prepare, target.PeerFingerprint);
            ownerStore.RecordPrepareReply(prepareReply, target.PeerFingerprint);
        }

        Assert.Equal(V4GroupMembershipStage.Committing, ownerStore.GetPendingTransaction(GroupId)!.Stage);
        foreach (GroupControlMessage commit in ownerStore.GetPendingMessages(GroupId).ToArray())
        {
            V4GroupStore peerStore = peerStores[commit.TargetFingerprint!];
            GroupControlMessage commitReply = peerStore.CommitMembership(commit, commit.TargetFingerprint!);
            ownerStore.RecordCommitReply(commitReply, commit.TargetFingerprint!);
        }

        Assert.Equal(GroupConversation.MaximumMembers, ownerStore.GetGroup(GroupId)!.Members.Count);
        Assert.All(peerStores.Values, store => Assert.Equal(GroupConversation.MaximumMembers, store.GetGroup(GroupId)!.Members.Count));

        string removedFingerprint = remoteFingerprints[^1];
        GroupMember[] reducedRoster = fullRoster.Where(member => member.Fingerprint != Member(removedFingerprint).Fingerprint).ToArray();
        V4PendingGroupTransaction removal = ownerStore.BeginMembershipChange(GroupId, Owner, reducedRoster);
        Assert.Equal(GroupConversation.MaximumMembers - 1, removal.Targets.Count);
        foreach (V4GroupTransactionTarget target in removal.Targets)
        {
            GroupControlMessage prepareReply = peerStores[target.PeerFingerprint]
                .PrepareMembership(target.Prepare, target.PeerFingerprint);
            ownerStore.RecordPrepareReply(prepareReply, target.PeerFingerprint);
        }
        foreach (GroupControlMessage commit in ownerStore.GetPendingMessages(GroupId).ToArray())
        {
            GroupControlMessage commitReply = peerStores[commit.TargetFingerprint!]
                .CommitMembership(commit, commit.TargetFingerprint!);
            ownerStore.RecordCommitReply(commitReply, commit.TargetFingerprint!);
        }

        Assert.Equal(GroupConversation.MaximumMembers - 1, ownerStore.GetGroup(GroupId)!.Members.Count);
        Assert.All(peerStores.Values, store =>
            Assert.Equal(reducedRoster.Select(member => member.Fingerprint),
                store.GetGroup(GroupId)!.Members.Select(member => member.Fingerprint)));
    }

    [Fact]
    public void RemovedMember_CanCommitTheRosterThatRemovesItself()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation original = Group(Member(Owner), Member(MemberB), Member(MemberC));
        store.AddGroup(original);
        Guid eventID = Guid.NewGuid();
        GroupMember[] roster = [Member(Owner), Member(MemberB)];
        GroupControlMessage prepare = MembershipControl(GroupControlKind.MembershipPrepare,
            original, roster, MemberC, eventID);
        store.PrepareMembership(prepare, MemberC);
        GroupControlMessage commit = MembershipControl(GroupControlKind.MembershipCommit,
            original, roster, MemberC, eventID);

        Assert.True(new V4GroupStore(ArchivePath).CommitMembership(commit, MemberC).Accepted);
        Assert.DoesNotContain(new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Members,
            member => member.Fingerprint == Member(MemberC).Fingerprint);
    }

    [Fact]
    public void AbortMembership_BeforeCommitClearsOnlyMatchingPreparedChange()
    {
        var ownerStore = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        ownerStore.AddGroup(original);
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(GroupId, Owner,
            [Member(Owner), Member(MemberB), Member(MemberC)]);
        V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == Member(MemberB).Fingerprint);

        string recipientArchive = Path.Combine(_root, "member-b.json");
        var recipient = new V4GroupStore(recipientArchive);
        recipient.AddGroup(original);
        recipient.PrepareMembership(target.Prepare, MemberB);
        GroupControlMessage abort = new(GroupControlKind.MembershipAbort, GroupId, Owner,
            transaction.ProposedGroup.Revision, transaction.EventID, transaction.BaseEpoch, target.PeerFingerprint);

        Assert.True(recipient.AbortMembership(abort));
        var reloaded = new V4GroupStore(recipientArchive);
        GroupConversation saved = Assert.IsType<GroupConversation>(reloaded.GetGroup(GroupId));
        Assert.Equal(original.Revision, saved.Revision);
        Assert.Equal(original.Members.Select(member => member.Fingerprint), saved.Members.Select(member => member.Fingerprint));
        Assert.Empty(reloaded.PendingMembershipPrepares);
        Assert.False(reloaded.PrepareMembership(target.Prepare, MemberB).Accepted);
        Assert.False(reloaded.CommitMembership(target.Commit, MemberB).Accepted);
    }

    [Fact]
    public void MembershipCancellation_PersistsIntentAndWaitsForEveryOriginalTarget()
    {
        var ownerStore = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        ownerStore.AddGroup(original);
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(GroupId, Owner,
            [Member(Owner), Member(MemberB), Member(MemberC)]);
        Assert.True(ownerStore.BeginAbortMembershipChange(GroupId, Owner));
        Assert.Equal(V4GroupMembershipStage.Aborting, ownerStore.GetPendingTransaction(GroupId)!.Stage);

        IReadOnlyList<GroupControlMessage> aborts = ownerStore.GetPendingMessages(GroupId);
        Assert.Equal(2, aborts.Count);
        Assert.All(aborts, abort => Assert.Equal(GroupControlKind.MembershipAbort, abort.Kind));
        GroupControlMessage firstReply = Reply(aborts[0], GroupControlKind.MembershipAbortReply);
        Assert.False(ownerStore.RecordAbortReply(firstReply, aborts[0].TargetFingerprint!));
        Assert.Single(new V4GroupStore(ArchivePath).GetPendingMessages(GroupId));

        var reloaded = new V4GroupStore(ArchivePath);
        GroupControlMessage secondAbort = Assert.Single(reloaded.GetPendingMessages(GroupId));
        Assert.True(reloaded.RecordAbortReply(Reply(secondAbort, GroupControlKind.MembershipAbortReply), secondAbort.TargetFingerprint!));
        Assert.Null(new V4GroupStore(ArchivePath).GetPendingTransaction(GroupId));
        Assert.Equal(original.Revision, new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Revision);
        Assert.Equal(original.Members.Select(member => member.Fingerprint),
            new V4GroupStore(ArchivePath).GetGroup(GroupId)!.Members.Select(member => member.Fingerprint));
        Assert.Throws<InvalidDataException>(() => new V4GroupStore(ArchivePath).RecordCommitReply(
            Reply(transaction.Targets[0].Commit, GroupControlKind.MembershipCommitReply), transaction.Targets[0].PeerFingerprint));
    }

    [Fact]
    public void MembershipAbort_PersistsTombstoneAndReplaysDurableAcknowledgment()
    {
        var ownerStore = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        ownerStore.AddGroup(original);
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(GroupId, Owner,
            [Member(Owner), Member(MemberB), Member(MemberC)]);
        V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == Member(MemberB).Fingerprint);
        string recipientArchive = Path.Combine(_root, "member-b.json");
        var recipient = new V4GroupStore(recipientArchive);
        recipient.AddGroup(original);
        recipient.PrepareMembership(target.Prepare, MemberB);
        GroupControlMessage abort = new(GroupControlKind.MembershipAbort, GroupId, Owner,
            transaction.ProposedGroup.Revision, transaction.EventID, transaction.BaseEpoch, target.PeerFingerprint);

        Assert.True(recipient.AbortMembership(abort, MemberB));
        var restarted = new V4GroupStore(recipientArchive);
        Assert.True(restarted.AbortMembership(abort, MemberB));
        Assert.False(restarted.PrepareMembership(target.Prepare, MemberB).Accepted);
        Assert.False(restarted.CommitMembership(target.Commit, MemberB).Accepted);
        Assert.Single(restarted.AbortTombstones);
        Assert.Equal(original.Revision, restarted.GetGroup(GroupId)!.Revision);
        Assert.Equal(original.Members.Select(member => member.Fingerprint),
            restarted.GetGroup(GroupId)!.Members.Select(member => member.Fingerprint));
    }

    [Fact]
    public void AbortAfterCommit_IsDurablyRefusedAndDoesNotRollbackRoster()
    {
        var ownerStore = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        ownerStore.AddGroup(original);
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(GroupId, Owner,
            [Member(Owner), Member(MemberB), Member(MemberC)]);
        V4GroupTransactionTarget target = transaction.Targets.Single(item => item.PeerFingerprint == Member(MemberB).Fingerprint);

        string recipientArchive = Path.Combine(_root, "member-b.json");
        var recipient = new V4GroupStore(recipientArchive);
        recipient.AddGroup(original);
        recipient.PrepareMembership(target.Prepare, MemberB);
        recipient.CommitMembership(target.Commit, MemberB);
        GroupControlMessage abort = new(GroupControlKind.MembershipAbort, GroupId, Owner,
            transaction.ProposedGroup.Revision, transaction.EventID, transaction.BaseEpoch, target.PeerFingerprint);

        Assert.False(recipient.AbortMembership(abort));
        var reloaded = new V4GroupStore(recipientArchive);
        GroupConversation saved = Assert.IsType<GroupConversation>(reloaded.GetGroup(GroupId));
        Assert.Equal(transaction.ProposedGroup.Revision, saved.Revision);
        Assert.Equal(transaction.ProposedGroup.Members.Select(member => member.Fingerprint),
            saved.Members.Select(member => member.Fingerprint));
        Assert.Empty(reloaded.PendingMembershipPrepares);
    }

    [Fact]
    public void Prepare_RejectsStaleEpochAndConflictingRetry()
    {
        var store = new V4GroupStore(ArchivePath);
        GroupConversation original = Group();
        store.AddGroup(original);
        GroupControlMessage valid = MembershipControl(GroupControlKind.MembershipPrepare, original,
            [Member(Owner), Member(MemberB)], MemberB);
        store.PrepareMembership(valid, MemberB);

        GroupControlMessage conflict = MembershipControl(GroupControlKind.MembershipPrepare, original,
            [Member(Owner), Member(MemberC)], MemberC, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => store.PrepareMembership(conflict, MemberC));
        GroupControlMessage stale = new(GroupControlKind.MembershipPrepare, GroupId, Owner, 2,
            baseEpoch: 1, targetFingerprint: MemberB, name: "test", roster: [Member(Owner), Member(MemberB)]);
        Assert.Throws<InvalidDataException>(() => store.PrepareMembership(stale, MemberB));
        Assert.Single(new V4GroupStore(ArchivePath).PendingMembershipPrepares);
    }

    private static readonly Guid GroupId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static GroupMember Member(string fingerprint) => new(fingerprint);

    private static GroupConversation Group(params GroupMember[] members) =>
        new(GroupId, "test", Owner, 0, members.Length == 0 ? [Member(Owner), Member(MemberB)] : members);

    private static GroupMessageEnvelope Message(int index) => new(
        Guid.Parse($"20000000-0000-0000-0000-{index + 1:000000000000}"), GroupId, Owner, 0,
        DateTimeOffset.UnixEpoch.AddSeconds(index), GroupMessageKind.Text, $"message-{index}");

    private static GroupControlMessage MembershipControl(GroupControlKind kind, GroupConversation current,
        IReadOnlyList<GroupMember> roster, string target, Guid? eventID = null) =>
        new(kind, current.Id, current.OwnerFingerprint, current.Revision + 1,
            eventID ?? Guid.NewGuid(), current.Revision, target, name: current.Name, roster: roster);

    private static string ControlHex(GroupControlMessage message) =>
        Convert.ToHexString(V4GroupWire.EncodeCanonical(message));

    private static GroupControlMessage Reply(GroupControlMessage request, GroupControlKind kind) =>
        new(kind, request.GroupID, request.OwnerFingerprint, request.Epoch, request.EventID,
            targetFingerprint: request.TargetFingerprint, accepted: true);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
