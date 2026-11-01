using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;
using Feige.Core.Crypto;

namespace Feige.Core.Tests;

public sealed class V4GroupDeliveryCoordinatorTests : IAsyncLifetime
{
    private const string OfflineMember = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
    private const string TestLocal = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TestPeer = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string TestNewPeer = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v4-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly List<Pair> _pairs = [];
    private readonly List<Task> _loops = [];

    [Fact]
    public async Task SendMethods_RejectPendingMembershipChangeBeforePersisting()
    {
        Guid groupID = Guid.NewGuid();
        var store = new V4GroupStore(Path.Combine(_root, "pending-groups.json"));
        var group = new GroupConversation(groupID, "pending-change", TestLocal, 4,
            [new(TestLocal), new(TestPeer)]);
        store.AddGroup(group);
        store.BeginMembershipChange(groupID, TestLocal, [new(TestLocal), new(TestPeer), new(TestNewPeer)]);
        var registry = new PeerConnectionRegistry<V4Connection>(TestLocal);
        var coordinator = new V4GroupDeliveryCoordinator(store, registry, TestLocal);
        string source = Path.Combine(_root, "pending.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        InvalidOperationException textError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SendTextAsync(groupID, "blocked"));
        Assert.Contains("成员变更", textError.Message, StringComparison.Ordinal);
        Assert.Empty(store.GetMessages(groupID));

        InvalidOperationException fileError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SendFileAsync(groupID, source));
        Assert.Contains("成员变更", fileError.Message, StringComparison.Ordinal);
        Assert.Empty(store.GetMessages(groupID));
    }

    [Fact]
    public async Task SendText_PersistsBeforeFanoutAndReportsOfflineAndFailedMembersIndependently()
    {
        using var localIdentity = IdentityKey.Create();
        Pair pair = await EstablishPair(localIdentity);
        Pair failing = await EstablishPair(localIdentity);
        _pairs.Add(pair);
        _pairs.Add(failing);
        Guid groupID = Guid.NewGuid();
        GroupConversation group = CreateGroup(groupID, pair.InitiatorFingerprint, pair.ResponderFingerprint,
            failing.ResponderFingerprint, OfflineMember);
        var senderStore = new V4GroupStore(Path.Combine(_root, "sender-groups.json"));
        senderStore.AddGroup(group);
        var receiverStore = new V4GroupStore(Path.Combine(_root, "receiver-groups.json"));
        receiverStore.AddGroup(group);
        bool senderWasPersistedBeforeReceiverCallback = false;
        var received = new TaskCompletionSource<GroupMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new V4GroupConnectionRouter(receiverStore, pair.ResponderFingerprint)
        {
            TextReceived = (_, message) =>
            {
                senderWasPersistedBeforeReceiverCallback = senderStore.GetMessages(groupID)
                    .Any(saved => saved.Id == message.Id && saved.Text == message.Text);
                received.TrySetResult(message);
                return Task.CompletedTask;
            }
        };
        StartPair(pair, router);
        failing.Responder.GroupRecordReceived = async (_, payload) =>
        {
            GroupMessageEnvelope rejected = V4GroupWire.DecodeGroupText(payload);
            await failing.Responder.SendGroupTextAckAsync(V4GroupWire.EncodeCanonical(
                new GroupTextAck(rejected.GroupID, rejected.Id, rejected.MemberRevision,
                    accepted: false, reason: "stale_revision")));
        };
        _loops.Add(Task.Run(() => failing.Responder.RunAsync()));
        _loops.Add(Task.Run(() => failing.Initiator.RunAsync()));
        var connections = CreateRegistry(pair);
        connections.Register(failing.Initiator, failing.ResponderFingerprint, localIdentity.Fingerprint,
            failing.Initiator.TranscriptDigest, direct: false);
        var coordinator = new V4GroupDeliveryCoordinator(senderStore, connections, pair.InitiatorFingerprint);

        IReadOnlyList<V4GroupMemberDeliveryResult> results = await coordinator.SendTextAsync(groupID, "group hello");
        Assert.Equal(V4GroupDeliveryStatus.Delivered, ResultFor(results, pair.ResponderFingerprint).Status);
        GroupMessageEnvelope delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(senderWasPersistedBeforeReceiverCallback);
        Assert.Equal("group hello", delivered.Text);
        Assert.Single(senderStore.GetMessages(groupID));
        Assert.Equal(V4GroupDeliveryStatus.Local, ResultFor(results, pair.InitiatorFingerprint).Status);
        Assert.Equal(V4GroupDeliveryStatus.Delivered, ResultFor(results, pair.ResponderFingerprint).Status);
        Assert.Equal(V4GroupDeliveryStatus.Failed, ResultFor(results, failing.ResponderFingerprint).Status);
        Assert.False(failing.Initiator.IsClosed);
        Assert.False(failing.Responder.IsClosed);
        Assert.Equal(V4GroupDeliveryStatus.Undelivered, ResultFor(results, OfflineMember).Status);
    }

    [Fact]
    public async Task SendFile_TransfersToAcceptingPeerAndKeepsDeclineAndOfflineResultsIsolated()
    {
        using var localIdentity = IdentityKey.Create();
        Pair accepting = await EstablishPair(localIdentity);
        Pair declining = await EstablishPair(localIdentity);
        _pairs.Add(accepting);
        _pairs.Add(declining);
        Guid groupID = Guid.NewGuid();
        GroupConversation group = CreateGroup(groupID, accepting.InitiatorFingerprint,
            accepting.ResponderFingerprint, declining.ResponderFingerprint, OfflineMember);
        var senderStore = new V4GroupStore(Path.Combine(_root, "sender-groups.json"));
        senderStore.AddGroup(group);
        var acceptingStore = new V4GroupStore(Path.Combine(_root, "accepting-groups.json"));
        acceptingStore.AddGroup(group);
        var decliningStore = new V4GroupStore(Path.Combine(_root, "declining-groups.json"));
        decliningStore.AddGroup(group);

        string receivedPath = Path.Combine(_root, "received");
        Directory.CreateDirectory(receivedPath);
        byte[] contents = Enumerable.Range(0, 100_000).Select(index => (byte)(index % 251)).ToArray();
        string source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, contents);
        var receivedFile = new TaskCompletionSource<(GroupMessageEnvelope Message, string Path)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool senderFileWasPersistedBeforeApproval = false;
        var receivedTransferIDs = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var acceptingRouter = new V4GroupConnectionRouter(acceptingStore, accepting.ResponderFingerprint)
        {
            GroupFileApproval = (_, manifest, _) =>
            {
                senderFileWasPersistedBeforeApproval = senderStore.GetMessages(groupID)
                    .Any(saved => saved.Id == manifest.MessageID && saved.FileMetadata is not null);
                receivedTransferIDs.Add(manifest.Metadata.TransferID);
                return Task.FromResult(true);
            },
            FileReceived = (_, message, path) =>
            {
                receivedFile.TrySetResult((message, path));
                return Task.CompletedTask;
            }
        };
        var decliningRouter = new V4GroupConnectionRouter(decliningStore, declining.ResponderFingerprint)
        {
            GroupFileApproval = (_, manifest, _) =>
            {
                receivedTransferIDs.Add(manifest.Metadata.TransferID);
                return Task.FromResult(false);
            }
        };
        StartPair(accepting, acceptingRouter, receivedPath);
        StartPair(declining, decliningRouter, receivedPath);
        var connections = CreateRegistry(accepting);
        connections.Register(declining.Initiator, declining.ResponderFingerprint,
            accepting.InitiatorFingerprint, declining.Initiator.TranscriptDigest, direct: false);
        var coordinator = new V4GroupDeliveryCoordinator(senderStore, connections, accepting.InitiatorFingerprint);

        IReadOnlyList<V4GroupMemberDeliveryResult> results = await coordinator.SendFileAsync(groupID, source);
        (GroupMessageEnvelope receivedMessage, string finalPath) = await receivedFile.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(contents, await File.ReadAllBytesAsync(finalPath));
        Assert.True(senderFileWasPersistedBeforeApproval);
        Assert.Equal(GroupMessageKind.File, receivedMessage.Kind);
        Assert.Equal(V4GroupDeliveryStatus.Local, ResultFor(results, accepting.InitiatorFingerprint).Status);
        Assert.Equal(V4GroupDeliveryStatus.Delivered, ResultFor(results, accepting.ResponderFingerprint).Status);
        Assert.True(ResultFor(results, declining.ResponderFingerprint).Status == V4GroupDeliveryStatus.Declined,
            ResultFor(results, declining.ResponderFingerprint).Error);
        Assert.Equal(V4GroupDeliveryStatus.Undelivered, ResultFor(results, OfflineMember).Status);
        Assert.False(declining.Initiator.IsClosed);
        Assert.False(declining.Responder.IsClosed);

        GroupMessageEnvelope persisted = Assert.Single(senderStore.GetMessages(groupID));
        Assert.Equal(receivedMessage.Id, persisted.Id);
        Guid[] recipientTransferIDs = receivedTransferIDs.ToArray();
        Assert.Equal(2, recipientTransferIDs.Length);
        Assert.NotEqual(recipientTransferIDs[0], recipientTransferIDs[1]);
        Assert.Contains(receivedMessage.FileMetadata!.TransferID, recipientTransferIDs);
    }

    private void StartPair(Pair pair, V4GroupConnectionRouter router, string? receiveDirectory = null)
    {
        _loops.Add(router.RunAsync(pair.Responder, () => receiveDirectory ?? _root));
        _loops.Add(Task.Run(() => pair.Initiator.RunAsync()));
    }

    private static PeerConnectionRegistry<V4Connection> CreateRegistry(Pair pair)
    {
        var registry = new PeerConnectionRegistry<V4Connection>(pair.InitiatorFingerprint);
        registry.Register(pair.Initiator, pair.ResponderFingerprint, pair.InitiatorFingerprint,
            pair.Initiator.TranscriptDigest, direct: false);
        return registry;
    }

    private static GroupConversation CreateGroup(Guid groupID, string local, params string[] additionalMembers)
        => new(groupID, "delivery-tests", local, 0,
            new[] { new GroupMember(local) }.Concat(additionalMembers.Select(fingerprint => new GroupMember(fingerprint))).ToArray());

    private static V4GroupMemberDeliveryResult ResultFor(IEnumerable<V4GroupMemberDeliveryResult> results,
        string fingerprint) => Assert.Single(results, result =>
            StringComparer.OrdinalIgnoreCase.Equals(result.MemberFingerprint.Replace(" ", string.Empty, StringComparison.Ordinal),
                fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal)));

    private static async Task<Pair> EstablishPair(IdentityKey initiatorIdentity)
    {
        using var responderIdentity = IdentityKey.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient(AddressFamily.InterNetwork);
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        Task<V4Connection> responderTask = V4Connection.EstablishAsync(accepted.GetStream(), responderIdentity,
            initiator: false, "Peer-B", (_, _) => Task.FromResult(true));
        V4Connection initiator = await V4Connection.EstablishAsync(client.GetStream(), initiatorIdentity,
            initiator: true, "Peer-A", (_, _) => throw new InvalidOperationException("initiator must not request consent"));
        V4Connection responder = await responderTask;
        return new Pair(initiator, responder, client, accepted, initiatorIdentity.Fingerprint,
            responderIdentity.Fingerprint);
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch { }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (Pair pair in _pairs)
        {
            pair.Initiator.Close();
            pair.Responder.Close();
        }
        await Task.WhenAll(_loops.Select(Observe));
        foreach (Pair pair in _pairs)
        {
            await pair.Initiator.DisposeAsync();
            await pair.Responder.DisposeAsync();
            pair.Client.Dispose();
            pair.Accepted.Dispose();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Pair(V4Connection Initiator, V4Connection Responder, TcpClient Client,
        TcpClient Accepted, string InitiatorFingerprint, string ResponderFingerprint);
}
