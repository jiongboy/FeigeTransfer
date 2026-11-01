using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Conversations;
using Feige.Core.Crypto;

namespace Feige.Core.Tests;

public sealed class V4GroupConnectionRouterTests : IDisposable
{
    private const string MemberC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v4-router-" + Guid.NewGuid().ToString("N"));
    private string ArchivePath => Path.Combine(_root, "groups.json");

    [Fact]
    public async Task InvitationAndMembershipControls_UseApprovalAndSendTypedReplies()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint);
        Guid groupID = Guid.NewGuid();
        Guid eventID = Guid.NewGuid();
        GroupMember[] roster = [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)];
        var invite = new GroupControlMessage(GroupControlKind.Invite, groupID, pair.InitiatorFingerprint,
            epoch: 0, eventID, targetFingerprint: pair.ResponderFingerprint, name: "router-test", roster: roster);
        var replies = new System.Collections.Concurrent.ConcurrentQueue<GroupControlMessage>();
        var replySignal = new SemaphoreSlim(0);
        var approvalCheckedBeforeStoreWrite = false;
        router.InvitationApproval = (_, _, _) =>
        {
            approvalCheckedBeforeStoreWrite = store.AcceptedInvitations.Count == 0;
            return Task.FromResult(true);
        };
        pair.Initiator.GroupRecordReceived = (_, payload) =>
        {
            replies.Enqueue(V4GroupWire.DecodeControl(payload));
            replySignal.Release();
            return Task.CompletedTask;
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Assert.Same(responderLoop, router.RunAsync(pair.Responder, () => _root));
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(invite));
            GroupControlMessage inviteReply = await NextReplyAsync(replies, replySignal);
            Assert.True(approvalCheckedBeforeStoreWrite);
            Assert.Equal(GroupControlKind.InviteReply, inviteReply.Kind);
            Assert.True(inviteReply.Accepted);
            Assert.Empty(store.Groups);

            var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, groupID,
                pair.InitiatorFingerprint, epoch: 0, eventID, targetFingerprint: pair.ResponderFingerprint,
                name: "router-test", roster: roster);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(prepare));
            GroupControlMessage prepareReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.MembershipPrepareReply, prepareReply.Kind);
            Assert.True(prepareReply.Accepted);
            Assert.Contains(store.PendingMembershipPrepares, item => item.EventID == eventID);

            var commit = new GroupControlMessage(GroupControlKind.MembershipCommit, groupID,
                pair.InitiatorFingerprint, epoch: 0, eventID, targetFingerprint: pair.ResponderFingerprint,
                name: "router-test", roster: roster);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(commit));
            GroupControlMessage commitReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.MembershipCommitReply, commitReply.Kind);
            Assert.True(commitReply.Accepted);
            Assert.Equal(roster.Length, Assert.Single(store.Groups).Members.Count);
            Assert.Equal(0UL, Assert.Single(store.Groups).Revision);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task NewMember_RequiresAcceptedInviteBeforeRoutedPrepareAndCommit()
    {
        var pair = await EstablishPair();
        Guid groupID = Guid.NewGuid();
        var ownerStore = new V4GroupStore(Path.Combine(_root, "owner-groups.json"));
        var newMemberStore = new V4GroupStore(Path.Combine(_root, "new-member-groups.json"));
        GroupConversation current = new(groupID, "addition-router-test", pair.InitiatorFingerprint, 0,
            [new(pair.InitiatorFingerprint), new(MemberC)]);
        ownerStore.AddGroup(current);
        GroupMember[] proposedRoster = [new(pair.InitiatorFingerprint), new(MemberC), new(pair.ResponderFingerprint)];
        V4PendingGroupTransaction transaction = ownerStore.BeginMembershipChange(groupID,
            pair.InitiatorFingerprint, proposedRoster);
        V4GroupTransactionTarget target = transaction.Targets.Single(item =>
            item.PeerFingerprint == pair.ResponderFingerprint);
        var router = new V4GroupConnectionRouter(newMemberStore, pair.ResponderFingerprint)
        {
            InvitationApproval = (_, _, _) => Task.FromResult(true)
        };
        var replies = new System.Collections.Concurrent.ConcurrentQueue<GroupControlMessage>();
        var replySignal = new SemaphoreSlim(0);
        pair.Initiator.GroupRecordReceived = (_, payload) =>
        {
            replies.Enqueue(V4GroupWire.DecodeControl(payload));
            replySignal.Release();
            return Task.CompletedTask;
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            GroupControlMessage invite = new(GroupControlKind.Invite, groupID, pair.InitiatorFingerprint,
                transaction.ProposedGroup.Revision, transaction.EventID, targetFingerprint: pair.ResponderFingerprint,
                name: transaction.ProposedGroup.Name, roster: transaction.ProposedGroup.Members);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(invite));
            GroupControlMessage inviteReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.InviteReply, inviteReply.Kind);
            Assert.True(inviteReply.Accepted);

            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(target.Prepare));
            GroupControlMessage prepareReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.MembershipPrepareReply, prepareReply.Kind);
            Assert.True(prepareReply.Accepted);
            Assert.Single(newMemberStore.PendingMembershipPrepares);

            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(target.Commit));
            GroupControlMessage commitReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.MembershipCommitReply, commitReply.Kind);
            Assert.True(commitReply.Accepted);
            GroupConversation joined = Assert.Single(newMemberStore.Groups);
            Assert.Equal(transaction.ProposedGroup.Revision, joined.Revision);
            Assert.Equal(proposedRoster.Select(member => member.Fingerprint), joined.Members.Select(member => member.Fingerprint));
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupText_IsPersistedAndDeduplicatedBeforeAcknowledging()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "text-test", pair.InitiatorFingerprint, 5,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint);
        int callbackCount = 0;
        router.TextReceived = (_, message) =>
        {
            Assert.Contains(store.GetMessages(groupID), stored => stored.Id == message.Id);
            Interlocked.Increment(ref callbackCount);
            return Task.CompletedTask;
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            var message = new GroupMessageEnvelope(Guid.NewGuid(), groupID, pair.InitiatorFingerprint, 5,
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), GroupMessageKind.Text, "hello group");
            byte[] canonical = V4GroupWire.EncodeCanonical(message);
            GroupTextAck first = V4GroupWire.DecodeGroupTextAck(
                await pair.Initiator.SendGroupTextAsync(canonical, message.Id));
            GroupTextAck replay = V4GroupWire.DecodeGroupTextAck(
                await pair.Initiator.SendGroupTextAsync(canonical, message.Id));

            Assert.True(first.Accepted);
            Assert.True(replay.Accepted);
            Assert.Null(first.Reason);
            Assert.Single(store.GetMessages(groupID));
            Assert.Equal(1, callbackCount);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupText_FromStaleRevision_IsRejectedWithoutPersistence()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "stale-test", pair.InitiatorFingerprint, 5,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        int callbackCount = 0;
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            TextReceived = (_, _) => { Interlocked.Increment(ref callbackCount); return Task.CompletedTask; }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            var message = new GroupMessageEnvelope(Guid.NewGuid(), groupID, pair.InitiatorFingerprint, 4,
                DateTimeOffset.UtcNow, GroupMessageKind.Text, "stale");
            await Assert.ThrowsAnyAsync<Exception>(() => pair.Initiator.SendGroupTextAsync(
                V4GroupWire.EncodeCanonical(message), message.Id));
            Assert.Empty(store.GetMessages(groupID));
            Assert.Equal(0, callbackCount);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupText_DuringPendingMembershipPrepare_IsRejectedWithoutPersistence()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "pending-text-test", pair.InitiatorFingerprint, 5,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, groupID,
            pair.InitiatorFingerprint, epoch: 6, Guid.NewGuid(), baseEpoch: 5,
            targetFingerprint: pair.ResponderFingerprint, name: "pending-text-test",
            roster: [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint), new(MemberC)]);
        store.PrepareMembership(prepare, pair.ResponderFingerprint);
        int callbackCount = 0;
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            TextReceived = (_, _) => { Interlocked.Increment(ref callbackCount); return Task.CompletedTask; }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            var message = new GroupMessageEnvelope(Guid.NewGuid(), groupID, pair.InitiatorFingerprint, 5,
                DateTimeOffset.UtcNow, GroupMessageKind.Text, "must be rejected during prepare");
            IOException error = await Assert.ThrowsAsync<IOException>(() => pair.Initiator.SendGroupTextAsync(
                V4GroupWire.EncodeCanonical(message), message.Id));

            Assert.Contains("membership_change_pending", error.Message, StringComparison.Ordinal);
            Assert.Empty(store.GetMessages(groupID));
            Assert.Equal(0, callbackCount);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupFileManifest_DuringPendingMembershipPrepare_IsDeclinedBeforeApproval()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "pending-file-test", pair.InitiatorFingerprint, 8,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, groupID,
            pair.InitiatorFingerprint, epoch: 9, Guid.NewGuid(), baseEpoch: 8,
            targetFingerprint: pair.ResponderFingerprint, name: "pending-file-test",
            roster: [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint), new(MemberC)]);
        store.PrepareMembership(prepare, pair.ResponderFingerprint);
        int approvalCount = 0;
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            GroupFileApproval = (_, _, _) =>
            {
                Interlocked.Increment(ref approvalCount);
                return Task.FromResult(true);
            }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            Guid transferID = Guid.NewGuid();
            var manifest = new V4GroupFileManifest(groupID, Guid.NewGuid(), pair.InitiatorFingerprint, 8,
                new V4GroupFileMetadata(transferID, "pending.bin", 0), SHA256.HashData(Array.Empty<byte>()));
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(await pair.Initiator
                .SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID));

            Assert.False(decision.Accepted);
            Assert.Equal("declined", decision.Reason);
            Assert.Equal(0, approvalCount);
            Assert.Empty(store.GetMessages(groupID));
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupFile_BecomingPendingDuringTransfer_IsDiscardedWithoutMessage()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "pending-mid-file-test", pair.InitiatorFingerprint, 8,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        string receiveDirectory = Path.Combine(_root, "pending-received");
        Directory.CreateDirectory(receiveDirectory);
        byte[] contents = RandomNumberGenerator.GetBytes(4096);
        int fileCallbackCount = 0;
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            GroupFileApproval = (_, _, _) =>
            {
                var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, groupID,
                    pair.InitiatorFingerprint, epoch: 9, Guid.NewGuid(), baseEpoch: 8,
                    targetFingerprint: pair.ResponderFingerprint, name: "pending-mid-file-test",
                    roster: [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint), new(MemberC)]);
                store.PrepareMembership(prepare, pair.ResponderFingerprint);
                return Task.FromResult(true);
            },
            FileReceived = (_, _, _) =>
            {
                Interlocked.Increment(ref fileCallbackCount);
                return Task.CompletedTask;
            }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => receiveDirectory);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            Guid transferID = Guid.NewGuid();
            byte[] digest = SHA256.HashData(contents);
            var manifest = new V4GroupFileManifest(groupID, Guid.NewGuid(), pair.InitiatorFingerprint, 8,
                new V4GroupFileMetadata(transferID, "pending-mid-transfer.bin", (ulong)contents.Length), digest);
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(await pair.Initiator
                .SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID));
            Assert.True(decision.Accepted);

            await pair.Initiator.SendGroupFileChunkAsync(transferID, 0, contents);
            byte[] finish = V4Wire.Encode(new { TransferID = transferID, Sha256 = digest });
            await Assert.ThrowsAnyAsync<Exception>(() => pair.Initiator.SendGroupFileFinishAsync(finish, transferID));

            Assert.True(store.HasPendingMembershipChange(groupID));
            Assert.Empty(store.GetMessages(groupID));
            Assert.Equal(0, fileCallbackCount);
            Assert.Empty(Directory.GetFiles(receiveDirectory));
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupFile_ValidatesBeforeApprovalAndPersistsEnvelopeBeforePathCallback()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "file-test", pair.InitiatorFingerprint, 8,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        string receiveDirectory = Path.Combine(_root, "received");
        Directory.CreateDirectory(receiveDirectory);
        byte[] contents = RandomNumberGenerator.GetBytes(48_000);
        string source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, contents);
        Guid messageID = Guid.NewGuid();
        Guid transferID = Guid.NewGuid();
        var manifest = new V4GroupFileManifest(groupID, messageID, pair.InitiatorFingerprint, 8,
            new V4GroupFileMetadata(transferID, Path.GetFileName(source), (ulong)contents.Length,
                V4GroupFileKind.File), SHA256.HashData(contents));
        int approvalCount = 0;
        var received = new TaskCompletionSource<(GroupMessageEnvelope Message, string Path)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            GroupFileReceiveProgress = (_, _, bytesReceived) => progress.Enqueue(bytesReceived),
            GroupFileApproval = (_, value, _) =>
            {
                Assert.Equal(pair.InitiatorFingerprint, value.SenderFingerprint);
                Assert.Equal(8UL, value.MemberRevision);
                Interlocked.Increment(ref approvalCount);
                return Task.FromResult(true);
            },
            FileReceived = (_, message, path) =>
            {
                Assert.Contains(store.GetMessages(groupID), stored => stored.Id == messageID);
                received.TrySetResult((message, path));
                return Task.CompletedTask;
            }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => receiveDirectory);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(await pair.Initiator
                .SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID));
            Assert.True(decision.Accepted);

            ulong offset = 0;
            while (offset < (ulong)contents.Length)
            {
                int count = (int)Math.Min(V4Wire.MaxPlaintext - 24, (ulong)contents.Length - offset);
                await pair.Initiator.SendGroupFileChunkAsync(transferID, offset,
                    contents.AsMemory((int)offset, count));
                offset += (ulong)count;
            }
            byte[] finish = V4Wire.Encode(new { TransferID = transferID, Sha256 = manifest.Sha256 });
            _ = await pair.Initiator.SendGroupFileFinishAsync(finish, transferID);

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, approvalCount);
            Assert.Equal(messageID, result.Message.Id);
            Assert.Equal(GroupMessageKind.File, result.Message.Kind);
            Assert.Equal(transferID, result.Message.FileMetadata!.TransferID);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(contents, await File.ReadAllBytesAsync(result.Path));
            Assert.Contains(0L, progress);
            Assert.Contains((long)contents.Length, progress);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task InviteReply_IsExposedWithAuthenticatedPeerFingerprint()
    {
        var pair = await EstablishPair();
        var router = new V4GroupConnectionRouter(new V4GroupStore(ArchivePath), pair.ResponderFingerprint);
        var observed = new TaskCompletionSource<(string Peer, GroupControlMessage Reply)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        router.ControlReplyReceived = (_, peerFingerprint, reply) =>
        {
            observed.TrySetResult((peerFingerprint, reply));
            return Task.CompletedTask;
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            var reply = new GroupControlMessage(GroupControlKind.InviteReply, Guid.NewGuid(),
                pair.ResponderFingerprint, epoch: 2, targetFingerprint: pair.InitiatorFingerprint, accepted: false);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(reply));
            var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new GroupMember(pair.InitiatorFingerprint).Fingerprint, result.Peer);
            Assert.Equal(reply, result.Reply);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task MembershipAbort_PersistsPrepareTombstoneBeforeAcknowledging()
    {
        var pair = await EstablishPair(V4Offer.DurableAbortVersion);
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "abort-test", pair.InitiatorFingerprint, 4,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint);
        var replies = new System.Collections.Concurrent.ConcurrentQueue<GroupControlMessage>();
        var replySignal = new SemaphoreSlim(0);
        pair.Initiator.GroupRecordReceived = (_, payload) =>
        {
            replies.Enqueue(V4GroupWire.DecodeControl(payload));
            replySignal.Release();
            return Task.CompletedTask;
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            Guid eventID = Guid.NewGuid();
            GroupMember[] roster = [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint), new(MemberC)];
            var prepare = new GroupControlMessage(GroupControlKind.MembershipPrepare, groupID,
                pair.InitiatorFingerprint, epoch: 5, eventID, baseEpoch: 4,
                targetFingerprint: pair.ResponderFingerprint, name: "abort-test", roster: roster);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(prepare));
            Assert.Equal(GroupControlKind.MembershipPrepareReply,
                (await NextReplyAsync(replies, replySignal)).Kind);
            Assert.Single(store.PendingMembershipPrepares);

            var abort = new GroupControlMessage(GroupControlKind.MembershipAbort, groupID,
                pair.InitiatorFingerprint, epoch: 5, eventID, baseEpoch: 4,
                targetFingerprint: pair.ResponderFingerprint);
            await pair.Initiator.SendGroupControlAsync(V4GroupWire.EncodeCanonical(abort));
            GroupControlMessage abortReply = await NextReplyAsync(replies, replySignal);
            Assert.Equal(GroupControlKind.MembershipAbortReply, abortReply.Kind);
            Assert.True(abortReply.Accepted);
            Assert.Empty(store.PendingMembershipPrepares);
            Assert.Single(store.AbortTombstones);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    [Fact]
    public async Task GroupFileManifestWithStaleRevision_IsDeclinedBeforeApprovalCallback()
    {
        var pair = await EstablishPair();
        var store = new V4GroupStore(ArchivePath);
        Guid groupID = Guid.NewGuid();
        store.AddGroup(new GroupConversation(groupID, "stale-file-test", pair.InitiatorFingerprint, 8,
            [new(pair.InitiatorFingerprint), new(pair.ResponderFingerprint)]));
        int approvalCount = 0;
        var router = new V4GroupConnectionRouter(store, pair.ResponderFingerprint)
        {
            GroupFileApproval = (_, _, _) =>
            {
                Interlocked.Increment(ref approvalCount);
                return Task.FromResult(true);
            }
        };
        Task responderLoop = router.RunAsync(pair.Responder, () => _root);
        Task initiatorLoop = Task.Run(() => pair.Initiator.RunAsync());
        try
        {
            Guid transferID = Guid.NewGuid();
            var manifest = new V4GroupFileManifest(groupID, Guid.NewGuid(), pair.InitiatorFingerprint, 7,
                new V4GroupFileMetadata(transferID, "stale.bin", 0), new byte[32]);
            V4GroupFileDecision decision = V4GroupFileWire.DecodeDecision(await pair.Initiator
                .SendGroupFileManifestAsync(V4GroupFileWire.EncodeManifest(manifest), transferID));
            Assert.False(decision.Accepted);
            Assert.Equal("declined", decision.Reason);
            Assert.Equal(0, approvalCount);
        }
        finally { await ClosePair(pair, responderLoop, initiatorLoop); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task<GroupControlMessage> NextReplyAsync(
        System.Collections.Concurrent.ConcurrentQueue<GroupControlMessage> replies, SemaphoreSlim signal)
    {
        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(replies.TryDequeue(out GroupControlMessage? reply));
        return reply!;
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

        Task<V4Connection> responderTask = V4Connection.EstablishAsync(accepted.GetStream(), responderIdentity,
            initiator: false, "Win-B", (_, _) => Task.FromResult(true), protocolVersion: protocolVersion);
        V4Connection initiator = await V4Connection.EstablishAsync(client.GetStream(), initiatorIdentity,
            initiator: true, "Mac-A", (_, _) => throw new InvalidOperationException("initiator must not request consent"),
            protocolVersion: protocolVersion);
        V4Connection responder = await responderTask;
        return new Pair(initiator, responder, client, accepted, initiatorIdentity.Fingerprint,
            responderIdentity.Fingerprint);
    }

    private static async Task ClosePair(Pair pair, params Task[] loops)
    {
        pair.Initiator.Close();
        pair.Responder.Close();
        await Task.WhenAll(loops.Select(Observe));
        await pair.Initiator.DisposeAsync();
        await pair.Responder.DisposeAsync();
        pair.Client.Dispose();
        pair.Accepted.Dispose();
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private sealed record Pair(V4Connection Initiator, V4Connection Responder, TcpClient Client,
        TcpClient Accepted, string InitiatorFingerprint, string ResponderFingerprint);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
