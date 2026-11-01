using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Net;
using Xunit.Abstractions;

namespace Feige.Core.Tests;

public sealed class V4PeerTransportTests
{
    private readonly ITestOutputHelper _output;

    public V4PeerTransportTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public async Task ConnectorAndListener_EstablishV4AndApproveInboundConnectionOnce()
    {
        using var identityA = IdentityKey.Create();
        using var identityB = IdentityKey.Create();
        var localA = new LocalDevice(identityA, "Windows-A");
        var localB = new LocalDevice(identityB, "Windows-B");
        await using var listener = new V4PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var approvalRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int approvalCount = 0;

        Task<V4Connection> accepting = listener.AcceptAsync(localB, (peerName, _) =>
        {
            Interlocked.Increment(ref approvalCount);
            approvalRequested.TrySetResult(peerName);
            return approval.Task;
        }, deadline.Token);
        Task<V4Connection> connecting = V4PeerConnector.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port), localA, deadline.Token);

        Assert.Equal("Windows-A", await approvalRequested.Task.WaitAsync(deadline.Token));
        Assert.False(accepting.IsCompleted);
        Assert.False(connecting.IsCompleted);
        approval.TrySetResult(true);

        await using V4Connection client = await connecting.WaitAsync(deadline.Token);
        await using V4Connection server = await accepting.WaitAsync(deadline.Token);
        Assert.Equal(4, client.ProtocolVersion);
        Assert.Equal(4, server.ProtocolVersion);
        Assert.Equal(localB.Fingerprint, client.PeerFingerprint);
        Assert.Equal(localA.Fingerprint, server.PeerFingerprint);
        Assert.Equal("Windows-B", client.PeerName);
        Assert.Equal("Windows-A", server.PeerName);
        Assert.Equal(1, approvalCount);
    }

    [Fact]
    public async Task Connector_RacesCandidatesButOnlyAuthenticatesTheWinningSocket()
    {
        using var identityA = IdentityKey.Create();
        using var identityB = IdentityKey.Create();
        var localA = new LocalDevice(identityA, "Windows-A");
        var localB = new LocalDevice(identityB, "Windows-B");
        await using var listener = new V4PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int approvals = 0;
        Task<V4Connection> accepting = listener.AcceptAsync(localB, (_, _) =>
        {
            Interlocked.Increment(ref approvals);
            return Task.FromResult(true);
        }, deadline.Token);

        int closedPort;
        using (var temporary = new TcpListener(IPAddress.Loopback, 0))
        {
            temporary.Start();
            closedPort = ((IPEndPoint)temporary.LocalEndpoint).Port;
        }

        Task<V4Connection> connecting = V4PeerConnector.ConnectAsync(
            [new IPEndPoint(IPAddress.Loopback, closedPort), new IPEndPoint(IPAddress.Loopback, listener.Port)],
            localA, deadline.Token);
        await using V4Connection client = await connecting.WaitAsync(deadline.Token);
        await using V4Connection server = await accepting.WaitAsync(deadline.Token);
        Assert.Equal(localB.Fingerprint, client.PeerFingerprint);
        Assert.Equal(1, approvals);
    }

    [Fact]
    public async Task Listener_RejectsOneBrokenHandshakeAndAcceptsTheNextConnection()
    {
        using var identityA = IdentityKey.Create();
        using var identityB = IdentityKey.Create();
        var localA = new LocalDevice(identityA, "Windows-A");
        var localB = new LocalDevice(identityB, "Windows-B");
        await using var listener = new V4PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int approvals = 0;

        Task<V4Connection> brokenAccept = listener.AcceptAsync(localB, (_, _) =>
        {
            Interlocked.Increment(ref approvals);
            return Task.FromResult(true);
        }, deadline.Token);
        using (var brokenClient = new TcpClient())
        {
            await brokenClient.ConnectAsync(IPAddress.Loopback, listener.Port, deadline.Token);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => brokenAccept.WaitAsync(deadline.Token));
        Assert.Equal(0, approvals);

        Task<V4Connection> accepting = listener.AcceptAsync(localB, (_, _) =>
        {
            Interlocked.Increment(ref approvals);
            return Task.FromResult(true);
        }, deadline.Token);
        await using V4Connection client = await V4PeerConnector.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port), localA, deadline.Token);
        await using V4Connection server = await accepting.WaitAsync(deadline.Token);
        Assert.Equal(1, approvals);
    }

    [Fact]
    public async Task V4Connector_DoesNotFallbackWhenPeerOnlySpeaksV3()
    {
        using var identityA = IdentityKey.Create();
        using var identityB = IdentityKey.Create();
        var localA = new LocalDevice(identityA, "Windows-A");
        var localB = new LocalDevice(identityB, "Windows-B");
        await using var v3Listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        v3Listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task<V3Connection> acceptingV3 = v3Listener.AcceptAsync(localB, (_, _) => Task.FromResult(true), deadline.Token);
        Task<V4Connection> connectingV4 = V4PeerConnector.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, v3Listener.Port), localA, deadline.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => connectingV4.WaitAsync(deadline.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => acceptingV3.WaitAsync(deadline.Token));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.2")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1%1")]
    public void ManualTarget_AcceptsLocalNetworkAddresses(string address)
        => Assert.Equal(IPAddress.Parse(address), V4PeerConnector.ParseTarget(address, 12345).Address);

    [Theory]
    [InlineData("example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("2001:4860:4860::8888")]
    public void ManualTarget_RejectsPublicDomainAndWildcardAddresses(string address)
        => Assert.Throws<ArgumentException>(() => V4PeerConnector.ParseTarget(address, 12345));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ManualTarget_RejectsInvalidPort(int port)
        => Assert.Throws<ArgumentException>(() => V4PeerConnector.ParseTarget("127.0.0.1", port));

    [Fact]
    public async Task RealV4Sockets_HoldTwentyDirectAndNineteenGroupOnlyPeers()
    {
        const int peerCount = PeerConnectionRegistry<V4Connection>.MaximumConnections;
        using var localIdentity = IdentityKey.Create();
        var local = new LocalDevice(localIdentity, "Windows-capacity-owner");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var identities = Enumerable.Range(0, peerCount).Select(_ => IdentityKey.Create()).ToArray();
        var peers = identities.Select((identity, index) => new LocalDevice(identity, $"Windows-peer-{index + 1}")).ToArray();
        var listeners = Enumerable.Range(0, peerCount)
            .Select(_ => new V4PeerListener(new IPEndPoint(IPAddress.Loopback, 0)))
            .ToArray();
        using Process process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long managedBefore = GC.GetTotalMemory(forceFullCollection: false);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var connectionTimer = Stopwatch.StartNew();
        Task<(V4Connection Client, V4Connection Server)>[] sessions = Enumerable.Range(0, peerCount)
            .Select(async index =>
            {
                Task<V4Connection> accepting = listeners[index].AcceptAsync(local, (_, _) => Task.FromResult(true), deadline.Token);
                V4Connection client = await V4PeerConnector.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, listeners[index].Port), peers[index], deadline.Token);
                V4Connection server = await accepting.WaitAsync(deadline.Token);
                return (client, server);
            })
            .ToArray();

        try
        {
            (V4Connection Client, V4Connection Server)[] established = await Task.WhenAll(sessions).WaitAsync(deadline.Token);
            connectionTimer.Stop();
            process.Refresh();
            long workingSetAfter = process.WorkingSet64;
            long managedAfter = GC.GetTotalMemory(forceFullCollection: false);
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            TimeSpan cpuAfter = process.TotalProcessorTime;
            const double mebibyte = 1024d * 1024d;
            _output.WriteLine(
                $"39 authenticated loopback connections: elapsed={connectionTimer.Elapsed.TotalMilliseconds:F0} ms; " +
                $"process CPU={(cpuAfter - cpuBefore).TotalMilliseconds:F0} ms; " +
                $"working set={workingSetAfter / mebibyte:F1} MiB (delta={(workingSetAfter - workingSetBefore) / mebibyte:+0.0;-0.0;0.0} MiB); " +
                $"managed heap delta={(managedAfter - managedBefore) / mebibyte:+0.0;-0.0;0.0} MiB; " +
                $"allocated={(allocatedAfter - allocatedBefore) / mebibyte:F1} MiB");
            var registry = new PeerConnectionRegistry<V4Connection>(local.Fingerprint);
            for (int i = 0; i < established.Length; i++)
            {
                V4Connection server = established[i].Server;
                Assert.False(server.IsClosed);
                registry.Register(server, server.PeerFingerprint, server.InitiatorFingerprint,
                    server.TranscriptDigest, direct: i < PeerConnectionRegistry<V4Connection>.MaximumDirectPeers);
            }

            Assert.Equal(20, registry.DirectPeerCount);
            Assert.Equal(39, registry.ConnectionCount);
            Assert.Equal(39, established.Select(pair => pair.Server.PeerFingerprint).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            deadline.Cancel();
            foreach (Task<(V4Connection Client, V4Connection Server)> session in sessions)
            {
                if (!session.IsCompletedSuccessfully) continue;
                var pair = await session;
                await pair.Client.DisposeAsync();
                await pair.Server.DisposeAsync();
            }
            foreach (V4PeerListener listener in listeners) await listener.DisposeAsync();
            foreach (IdentityKey identity in identities) identity.Dispose();
        }
    }

    [Fact]
    public async Task Connector_RejectsTooManyCandidatesBeforeDialing()
    {
        using var identity = IdentityKey.Create();
        var local = new LocalDevice(identity, "Windows");
        IPEndPoint[] candidates = Enumerable.Range(0, V4PeerConnector.MaxCandidates + 1)
            .Select(port => new IPEndPoint(IPAddress.Loopback, port + 1)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => V4PeerConnector.ConnectAsync(candidates, local));
    }
}
