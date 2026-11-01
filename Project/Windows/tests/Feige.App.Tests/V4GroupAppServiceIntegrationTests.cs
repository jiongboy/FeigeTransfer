using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using Feige.App.Services;
using Feige.Core.Conversations;
using Feige.Core.Crypto;

namespace Feige.App.Tests;

public sealed class V4GroupAppServiceIntegrationTests
{
    [Theory]
    [InlineData(V4Offer.CurrentVersion)]
    [InlineData(V4Offer.DurableAbortVersion)]
    public async Task IsPeerConnected_AcceptsSupportedGroupProtocolVersions(ushort protocolVersion)
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-app-group-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using IdentityKey ownerIdentity = IdentityKey.Create();
        using IdentityKey peerIdentity = IdentityKey.Create();
        ConnectionPair? pair = null;
        try
        {
            pair = await EstablishPairAsync(ownerIdentity, peerIdentity, "group-protocol-peer", protocolVersion);
            var registry = new PeerConnectionRegistry<V4Connection>(ownerIdentity.Fingerprint);
            registry.Register(pair.Local, peerIdentity.Fingerprint, pair.Local.InitiatorFingerprint,
                pair.Local.TranscriptDigest, direct: false);
            var service = new V4GroupAppService(Path.Combine(root, "owner"), ownerIdentity.Fingerprint,
                peer => registry.TryGet(peer, out var connection) ? connection : null);
            service.SetRegistry(registry);

            Assert.True(service.IsPeerConnected(peerIdentity.Fingerprint));
            pair.Local.Close();
            Assert.False(service.IsPeerConnected(peerIdentity.Fingerprint));
        }
        finally
        {
            if (pair is not null) await ClosePairAsync(pair);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AddMemberThenSendText_InvitesBeforePrepareAndFansOutToBothMembers()
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-app-group-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using IdentityKey ownerIdentity = IdentityKey.Create();
        using IdentityKey existingIdentity = IdentityKey.Create();
        using IdentityKey addedIdentity = IdentityKey.Create();
        ConnectionPair? ownerExisting = null;
        ConnectionPair? ownerAdded = null;
        Task? ownerExistingLoop = null;
        Task? existingLoop = null;
        Task? ownerAddedLoop = null;
        Task? addedLoop = null;

        try
        {
            ownerExisting = await EstablishPairAsync(ownerIdentity, existingIdentity, "existing-member");
            ownerAdded = await EstablishPairAsync(ownerIdentity, addedIdentity, "added-member");
            var ownerConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(existingIdentity.Fingerprint)] = ownerExisting.Local,
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(addedIdentity.Fingerprint)] = ownerAdded.Local
            };
            var existingConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = ownerExisting.Remote
            };
            var addedConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = ownerAdded.Remote
            };
            static V4Connection? Lookup(Dictionary<string, V4Connection> connections, string fingerprint)
                => connections.GetValueOrDefault(PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint));
            static Task DispatchInline(Action action)
            {
                action();
                return Task.CompletedTask;
            }

            var ownerService = new V4GroupAppService(Path.Combine(root, "owner"), ownerIdentity.Fingerprint,
                peer => Lookup(ownerConnections, peer), DispatchInline);
            var existingService = new V4GroupAppService(Path.Combine(root, "existing"), existingIdentity.Fingerprint,
                peer => Lookup(existingConnections, peer), DispatchInline);
            var addedService = new V4GroupAppService(Path.Combine(root, "added"), addedIdentity.Fingerprint,
                peer => Lookup(addedConnections, peer), DispatchInline);

            var originalGroup = new GroupConversation(Guid.NewGuid(), "app add-member acceptance",
                ownerIdentity.Fingerprint, 0,
                [new(ownerIdentity.Fingerprint, "Windows owner"), new(existingIdentity.Fingerprint, "Existing member")]);
            ownerService.Store.AddGroup(originalGroup);
            existingService.Store.AddGroup(originalGroup);

            var registry = new PeerConnectionRegistry<V4Connection>(ownerIdentity.Fingerprint);
            registry.Register(ownerExisting.Local, existingIdentity.Fingerprint, ownerExisting.Local.InitiatorFingerprint,
                ownerExisting.Local.TranscriptDigest, direct: false);
            registry.Register(ownerAdded.Local, addedIdentity.Fingerprint, ownerAdded.Local.InitiatorFingerprint,
                ownerAdded.Local.TranscriptDigest, direct: false);
            ownerService.SetRegistry(registry);

            addedService.PendingApprovals.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null) return;
                foreach (GroupApprovalRequest request in args.NewItems)
                    Assert.True(addedService.Decide(request.Id, accept: true), "The new-member invite decision should be pending.");
            };

            string ownerFolder = Path.Combine(root, "owner-files");
            string existingFolder = Path.Combine(root, "existing-files");
            string addedFolder = Path.Combine(root, "added-files");
            Directory.CreateDirectory(ownerFolder);
            Directory.CreateDirectory(existingFolder);
            Directory.CreateDirectory(addedFolder);
            ownerExistingLoop = ownerService.CreateRouter().RunAsync(ownerExisting.Local, () => ownerFolder);
            existingLoop = existingService.CreateRouter().RunAsync(ownerExisting.Remote, () => existingFolder);
            ownerAddedLoop = ownerService.CreateRouter().RunAsync(ownerAdded.Local, () => ownerFolder);
            addedLoop = addedService.CreateRouter().RunAsync(ownerAdded.Remote, () => addedFolder);

            GroupMember[] proposedRoster =
            [
                new(ownerIdentity.Fingerprint, "Windows owner"),
                new(existingIdentity.Fingerprint, "Existing member"),
                new(addedIdentity.Fingerprint, "Added member")
            ];
            await ownerService.ChangeMembersAsync(originalGroup.Id, proposedRoster)
                .WaitAsync(TimeSpan.FromSeconds(45));

            AssertCommittedRoster(ownerService.Store, originalGroup.Id, proposedRoster);
            AssertCommittedRoster(existingService.Store, originalGroup.Id, proposedRoster);
            AssertCommittedRoster(addedService.Store, originalGroup.Id, proposedRoster);
            Assert.Empty(addedService.PendingApprovals);

            const string text = "windows-app-three-peer-fanout";
            IReadOnlyList<V4GroupMemberDeliveryResult> delivery = await ownerService.SendTextAsync(originalGroup.Id, text)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(3, delivery.Count);
            Assert.Single(delivery, item => item.Status == V4GroupDeliveryStatus.Local);
            Assert.Equal(2, delivery.Count(item => item.Status == V4GroupDeliveryStatus.Delivered));

            GroupMessageEnvelope sent = Assert.Single(ownerService.Store.Messages, item => item.Text == text);
            await WaitUntilAsync(() => existingService.Store.Messages.Any(item => item.Id == sent.Id) &&
                                       addedService.Store.Messages.Any(item => item.Id == sent.Id),
                TimeSpan.FromSeconds(10));
            Assert.Contains(existingService.Store.Messages, item => item.Id == sent.Id && item.Text == text);
            Assert.Contains(addedService.Store.Messages, item => item.Id == sent.Id && item.Text == text);
        }
        finally
        {
            if (ownerExisting is not null)
                await ClosePairAsync(ownerExisting, ownerExistingLoop, existingLoop);
            if (ownerAdded is not null)
                await ClosePairAsync(ownerAdded, ownerAddedLoop, addedLoop);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task NonOwnerLeaveRequest_RequiresOwnerApprovalAndCommitsRosterRemoval()
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-app-leave-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using IdentityKey ownerIdentity = IdentityKey.Create();
        using IdentityKey memberIdentity = IdentityKey.Create();
        ConnectionPair? pair = null;
        Task? ownerLoop = null;
        Task? memberLoop = null;
        try
        {
            pair = await EstablishPairAsync(ownerIdentity, memberIdentity, "leaving-member");
            var ownerConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(memberIdentity.Fingerprint)] = pair.Local
            };
            var memberConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = pair.Remote
            };
            static V4Connection? Lookup(Dictionary<string, V4Connection> connections, string fingerprint)
                => connections.GetValueOrDefault(PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint));
            static Task DispatchInline(Action action) { action(); return Task.CompletedTask; }

            var ownerService = new V4GroupAppService(Path.Combine(root, "owner"), ownerIdentity.Fingerprint,
                peer => Lookup(ownerConnections, peer), DispatchInline);
            var memberService = new V4GroupAppService(Path.Combine(root, "member"), memberIdentity.Fingerprint,
                peer => Lookup(memberConnections, peer), DispatchInline);
            var group = new GroupConversation(Guid.NewGuid(), "owner-approved leave", ownerIdentity.Fingerprint, 0,
                [new(ownerIdentity.Fingerprint, "Owner"), new(memberIdentity.Fingerprint, "Member")]);
            ownerService.Store.AddGroup(group);
            memberService.Store.AddGroup(group);

            var registry = new PeerConnectionRegistry<V4Connection>(ownerIdentity.Fingerprint);
            registry.Register(pair.Local, memberIdentity.Fingerprint, pair.Local.InitiatorFingerprint,
                pair.Local.TranscriptDigest, direct: false);
            ownerService.SetRegistry(registry);
            ownerService.PendingApprovals.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null) return;
                foreach (GroupApprovalRequest approval in args.NewItems)
                    if (approval.LeaveRequest is not null)
                        Assert.True(ownerService.Decide(approval.Id, accept: true), "The leave approval should be pending.");
            };

            string files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            ownerLoop = ownerService.CreateRouter().RunAsync(pair.Local, () => files);
            memberLoop = memberService.CreateRouter().RunAsync(pair.Remote, () => files);

            await memberService.RequestLeaveAsync(group.Id).WaitAsync(TimeSpan.FromSeconds(45));
            await WaitUntilAsync(() => ownerService.Store.GetGroup(group.Id)?.Revision == 1 &&
                                       memberService.Store.GetGroup(group.Id)?.Revision == 1 &&
                                       !memberService.Groups.Any(item => item.Id == group.Id),
                TimeSpan.FromSeconds(45));

            GroupConversation ownerRoster = Assert.IsType<GroupConversation>(ownerService.Store.GetGroup(group.Id));
            GroupConversation memberRoster = Assert.IsType<GroupConversation>(memberService.Store.GetGroup(group.Id));
            Assert.Equal(1UL, ownerRoster.Revision);
            Assert.Equal(1UL, memberRoster.Revision);
            Assert.Equal(new[] { ownerIdentity.Fingerprint }, ownerRoster.Members.Select(item => item.Fingerprint));
            Assert.Equal(new[] { ownerIdentity.Fingerprint }, memberRoster.Members.Select(item => item.Fingerprint));
            Assert.DoesNotContain(memberService.Groups, item => item.Id == group.Id);
            Assert.Contains(ownerService.Groups, item => item.Id == group.Id);
            Assert.Empty(ownerService.PendingApprovals);
        }
        finally
        {
            if (pair is not null) await ClosePairAsync(pair, ownerLoop, memberLoop);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task NonOwnerLeaveRequest_RejectionPreservesCommittedRoster()
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-app-leave-rejected-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using IdentityKey ownerIdentity = IdentityKey.Create();
        using IdentityKey memberIdentity = IdentityKey.Create();
        ConnectionPair? pair = null;
        Task? ownerLoop = null;
        Task? memberLoop = null;
        try
        {
            pair = await EstablishPairAsync(ownerIdentity, memberIdentity, "requesting-member");
            var ownerConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(memberIdentity.Fingerprint)] = pair.Local
            };
            var memberConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = pair.Remote
            };
            static V4Connection? Lookup(Dictionary<string, V4Connection> connections, string fingerprint)
                => connections.GetValueOrDefault(PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint));
            static Task DispatchInline(Action action) { action(); return Task.CompletedTask; }

            var ownerService = new V4GroupAppService(Path.Combine(root, "owner"), ownerIdentity.Fingerprint,
                peer => Lookup(ownerConnections, peer), DispatchInline);
            var memberService = new V4GroupAppService(Path.Combine(root, "member"), memberIdentity.Fingerprint,
                peer => Lookup(memberConnections, peer), DispatchInline);
            var group = new GroupConversation(Guid.NewGuid(), "rejected leave", ownerIdentity.Fingerprint, 0,
                [new(ownerIdentity.Fingerprint, "Owner"), new(memberIdentity.Fingerprint, "Member")]);
            ownerService.Store.AddGroup(group);
            memberService.Store.AddGroup(group);
            ownerService.PendingApprovals.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null) return;
                foreach (GroupApprovalRequest approval in args.NewItems)
                    if (approval.LeaveRequest is not null)
                        Assert.True(ownerService.Decide(approval.Id, accept: false), "The leave approval should be pending.");
            };

            string files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            ownerLoop = ownerService.CreateRouter().RunAsync(pair.Local, () => files);
            memberLoop = memberService.CreateRouter().RunAsync(pair.Remote, () => files);

            await Assert.ThrowsAsync<InvalidOperationException>(() => memberService.RequestLeaveAsync(group.Id)
                .WaitAsync(TimeSpan.FromSeconds(15)));

            foreach (V4GroupStore store in new[] { ownerService.Store, memberService.Store })
            {
                GroupConversation unchanged = Assert.IsType<GroupConversation>(store.GetGroup(group.Id));
                Assert.Equal(0UL, unchanged.Revision);
                Assert.Equal(2, unchanged.Members.Count);
            }
            Assert.Empty(ownerService.PendingApprovals);
        }
        finally
        {
            if (pair is not null) await ClosePairAsync(pair, ownerLoop, memberLoop);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CreateGroupThenAddMemberAndRemoveItPrefixesEachSideWithTheSameRoster()
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-app-group-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using IdentityKey ownerIdentity = IdentityKey.Create();
        using IdentityKey firstIdentity = IdentityKey.Create();
        using IdentityKey secondIdentity = IdentityKey.Create();
        ConnectionPair? ownerFirst = null;
        ConnectionPair? ownerSecond = null;
        Task? ownerFirstLoop = null;
        Task? firstLoop = null;
        Task? ownerSecondLoop = null;
        Task? secondLoop = null;
        try
        {
            ownerFirst = await EstablishPairAsync(ownerIdentity, firstIdentity, "first-member");
            ownerSecond = await EstablishPairAsync(ownerIdentity, secondIdentity, "second-member");
            var ownerConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(firstIdentity.Fingerprint)] = ownerFirst.Local,
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(secondIdentity.Fingerprint)] = ownerSecond.Local
            };
            var firstConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = ownerFirst.Remote
            };
            var secondConnections = new Dictionary<string, V4Connection>(StringComparer.Ordinal)
            {
                [PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(ownerIdentity.Fingerprint)] = ownerSecond.Remote
            };
            static V4Connection? Lookup(Dictionary<string, V4Connection> connections, string fingerprint)
                => connections.GetValueOrDefault(PeerConnectionRegistry<V4Connection>.NormalizeFingerprint(fingerprint));
            static Task DispatchInline(Action action) { action(); return Task.CompletedTask; }

            var ownerService = new V4GroupAppService(Path.Combine(root, "owner"), ownerIdentity.Fingerprint,
                peer => Lookup(ownerConnections, peer), DispatchInline);
            var firstService = new V4GroupAppService(Path.Combine(root, "first"), firstIdentity.Fingerprint,
                peer => Lookup(firstConnections, peer), DispatchInline);
            var secondService = new V4GroupAppService(Path.Combine(root, "second"), secondIdentity.Fingerprint,
                peer => Lookup(secondConnections, peer), DispatchInline);

            var registry = new PeerConnectionRegistry<V4Connection>(ownerIdentity.Fingerprint);
            registry.Register(ownerFirst.Local, firstIdentity.Fingerprint, ownerFirst.Local.InitiatorFingerprint,
                ownerFirst.Local.TranscriptDigest, direct: false);
            registry.Register(ownerSecond.Local, secondIdentity.Fingerprint, ownerSecond.Local.InitiatorFingerprint,
                ownerSecond.Local.TranscriptDigest, direct: false);
            ownerService.SetRegistry(registry);

            // Invitations are accepted by the invited runtime itself, exactly as the macOS client does.
            foreach ((V4GroupAppService service, string label) in new[] { (firstService, "first"), (secondService, "second") })
            {
                V4GroupAppService observed = service;
                observed.PendingApprovals.CollectionChanged += (_, args) =>
                {
                    if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null) return;
                    foreach (GroupApprovalRequest request in args.NewItems)
                        if (request.Invitation is not null)
                            Assert.True(observed.Decide(request.Id, accept: true), $"{label} should accept its invitation.");
                };
            }

            string files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            ownerFirstLoop = ownerService.CreateRouter().RunAsync(ownerFirst.Local, () => files);
            firstLoop = firstService.CreateRouter().RunAsync(ownerFirst.Remote, () => files);
            ownerSecondLoop = ownerService.CreateRouter().RunAsync(ownerSecond.Local, () => files);
            secondLoop = secondService.CreateRouter().RunAsync(ownerSecond.Remote, () => files);

            await ownerService.CreateGroupAsync("界面成员管理验收", [
                new GroupMember(ownerIdentity.Fingerprint, "Windows owner"),
                new GroupMember(firstIdentity.Fingerprint, "First member")
            ]).WaitAsync(TimeSpan.FromSeconds(45));
            Guid groupId = Assert.Single(ownerService.Groups, group => group.Name == "界面成员管理验收").Id;
            AssertRoster(ownerService.Store, groupId, 2);
            AssertRoster(firstService.Store, groupId, 2);
            Assert.Null(secondService.Store.GetGroup(groupId));

            await ownerService.ChangeMembersAsync(groupId, [
                new GroupMember(ownerIdentity.Fingerprint, "Windows owner"),
                new GroupMember(firstIdentity.Fingerprint, "First member"),
                new GroupMember(secondIdentity.Fingerprint, "Second member")
            ]).WaitAsync(TimeSpan.FromSeconds(45));
            AssertRoster(ownerService.Store, groupId, 3);
            AssertRoster(firstService.Store, groupId, 3);
            AssertRoster(secondService.Store, groupId, 3);

            await ownerService.ChangeMembersAsync(groupId, [
                new GroupMember(ownerIdentity.Fingerprint, "Windows owner"),
                new GroupMember(firstIdentity.Fingerprint, "First member")
            ]).WaitAsync(TimeSpan.FromSeconds(45));
            AssertRoster(ownerService.Store, groupId, 2);
            AssertRoster(firstService.Store, groupId, 2);
            GroupConversation remaining = Assert.IsType<GroupConversation>(secondService.Store.GetGroup(groupId));
            Assert.Equal(2, remaining.Members.Count);
            Assert.DoesNotContain(remaining.Members, member => member.Fingerprint == secondIdentity.Fingerprint);
            GroupConversation ownerView = Assert.IsType<GroupConversation>(ownerService.Store.GetGroup(groupId));
            Assert.Equal(3UL, ownerView.Revision);
            Assert.Equal(ownerIdentity.Fingerprint, ownerView.OwnerFingerprint);
        }
        finally
        {
            if (ownerFirst is not null) await ClosePairAsync(ownerFirst, ownerFirstLoop, firstLoop);
            if (ownerSecond is not null) await ClosePairAsync(ownerSecond, ownerSecondLoop, secondLoop);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void AssertRoster(V4GroupStore store, Guid groupID, int expectedCount)
    {
        GroupConversation group = Assert.IsType<GroupConversation>(store.GetGroup(groupID));
        Assert.Equal(expectedCount, group.Members.Count);
        Assert.Single(group.Members, member => member.Fingerprint == group.OwnerFingerprint);
    }

    private static void AssertCommittedRoster(V4GroupStore store, Guid groupID, IEnumerable<GroupMember> expected)
    {
        GroupConversation group = Assert.IsType<GroupConversation>(store.GetGroup(groupID));
        Assert.Equal(1UL, group.Revision);
        Assert.Equal(expected.Select(member => member.Fingerprint).Order(StringComparer.Ordinal),
            group.Members.Select(member => member.Fingerprint).Order(StringComparer.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }

    private static async Task<ConnectionPair> EstablishPairAsync(IdentityKey localIdentity,
        IdentityKey remoteIdentity, string remoteName, ushort protocolVersion = V4Offer.CurrentVersion)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient(AddressFamily.InterNetwork);
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        Task<V4Connection> remoteTask = V4Connection.EstablishAsync(accepted.GetStream(), remoteIdentity,
            initiator: false, remoteName, (_, _) => Task.FromResult(true), protocolVersion: protocolVersion);
        V4Connection local = await V4Connection.EstablishAsync(client.GetStream(), localIdentity,
            initiator: true, "Windows app owner", (_, _) => throw new InvalidOperationException("The initiator must not request consent."),
            protocolVersion: protocolVersion);
        V4Connection remote = await remoteTask;
        return new(local, remote, client, accepted);
    }

    private static async Task ClosePairAsync(ConnectionPair pair, params Task?[] loops)
    {
        pair.Local.Close();
        pair.Remote.Close();
        await Task.WhenAll(loops.Where(loop => loop is not null).Select(loop => ObserveAsync(loop!)));
        await pair.Local.DisposeAsync();
        await pair.Remote.DisposeAsync();
        pair.LocalSocket.Dispose();
        pair.RemoteSocket.Dispose();
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private sealed record ConnectionPair(V4Connection Local, V4Connection Remote,
        TcpClient LocalSocket, TcpClient RemoteSocket);
}
