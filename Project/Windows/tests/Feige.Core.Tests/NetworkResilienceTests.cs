using System.Net;
using System.Net.Sockets;
using Feige.Core.Crypto;
using Feige.Core.Discovery;
using Feige.Core.Net;

namespace Feige.Core.Tests;

public sealed class NetworkResilienceTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.10", 24, true)]
    [InlineData("192.168.2.20", "192.168.1.10", 24, false)]
    [InlineData("10.42.128.1", "10.42.0.1", 16, true)]
    [InlineData("10.42.128.1", "10.42.0.1", 17, false)]
    [InlineData("fd12:3456::2", "fd12:3456::1", 64, true)]
    [InlineData("fd12:3457::2", "fd12:3456::1", 64, false)]
    [InlineData("fe80::2%7", "fe80::1%8", 64, false)]
    [InlineData("192.168.1.20", "192.168.1.10", 0, false)]
    public void SubnetRanking_UsesPrefixesAndReceiverScope(string address, string local, int prefix, bool same)
        => Assert.Equal(same, LanAddressPolicy.IsSameSubnet(IPAddress.Parse(address), IPAddress.Parse(local), prefix));

    [Fact] public void LinkLocalManualAddress_RequiresLocalInterfaceScope()
    {
        Assert.Throws<ArgumentException>(() => V3PeerConnector.ParseTarget("fe80::1234", 1234));
        Assert.Equal(7, V3PeerConnector.ParseTarget(" fe80::1234%7 ", 1234).Address.ScopeId);
    }

    [Fact] public async Task Discovery_PrefersReceivingSourceAndRetainsOtherLiveAddresses()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport) { RequiredVersion = 3 };
        var packet = Announcement("10.20.0.9", "192.168.1.20", "fd12::20");
        transport.Deliver(packet, "192.168.1.20");
        var peer = Assert.Single(browser.Peers);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), peer.EndPoint.Address);
        Assert.Equal(3, peer.EndPoints.Count);
        Assert.Contains(peer.EndPoints, p => p.Address.Equals(IPAddress.Parse("10.20.0.9")));
    }
    [Fact] public async Task Discovery_UsesLocalSubnetBeforeUnrelatedVirtualNetworks()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport)
        { LocalSubnetsProvider = () => [(IPAddress.Parse("192.168.44.10"), 24)] };
        transport.Deliver(Announcement("10.0.0.2", "192.168.44.20"), "192.168.55.1");
        Assert.Equal("192.168.44.20", Assert.Single(browser.Peers).EndPoint.Address.ToString());
        Assert.DoesNotContain(browser.Peers[0].EndPoints, p => p.Address.ToString() == "192.168.55.1"); // Reflector source is not an invented TCP target.
    }
    [Fact] public async Task Discovery_UpdatesAlreadyVisiblePeerWhenAddressOrPortChanges()
    {
        var transport = new TestTransport(); int updates = 0;
        await using var browser = new PeerBrowser(transport); browser.PeerFound += (_, _) => updates++;
        transport.Deliver(Announcement("192.168.1.20"), "192.168.1.20");
        transport.Deliver(Announcement("192.168.1.21"), "192.168.1.21");
        Assert.Equal(2, Assert.Single(browser.Peers).EndPoints.Count); Assert.Equal(2, updates);
        var changed = new MdnsMessage { IsResponse = true };
        changed.Answers.Add(new SrvRecord(Instance, 52002, Host));
        transport.Deliver(changed, "192.168.1.21");
        Assert.All(browser.Peers[0].EndPoints, p => Assert.Equal(52002, p.Port));
        transport.Deliver(changed, "192.168.1.21"); Assert.Equal(3, updates); // Duplicate packets do not churn rows.
    }
    [Fact] public async Task Discovery_AddressGoodbyeSelectsAnotherCandidateAndThenRemovesPeer()
    {
        var transport = new TestTransport(); int lost = 0; await using var browser = new PeerBrowser(transport);
        browser.PeerLost += (_, _) => lost++;
        transport.Deliver(Announcement("192.168.1.20", "192.168.1.21"), "192.168.1.20");
        foreach (string ip in new[] { "192.168.1.20", "192.168.1.21" })
        {
            var goodbye = new MdnsMessage { IsResponse = true }; goodbye.Answers.Add(new AddressRecord(Host, IPAddress.Parse(ip), 0));
            transport.Deliver(goodbye, ip);
            if (ip.EndsWith("20")) Assert.Equal("192.168.1.21", Assert.Single(browser.Peers).EndPoint.Address.ToString());
        }
        Assert.Empty(browser.Peers); Assert.Equal(1, lost);
    }
    [Fact] public async Task Discovery_SrvGoodbyeRaisesLostAndHostChangeDoesNotKeepOldAddresses()
    {
        var transport = new TestTransport(); int lost = 0; await using var browser = new PeerBrowser(transport);
        browser.PeerLost += (_, _) => lost++;
        transport.Deliver(Announcement("192.168.1.20"), "192.168.1.20");
        var changed = new MdnsMessage { IsResponse = true };
        changed.Answers.Add(new SrvRecord(Instance, 51001, new DnsName("new", "local")));
        transport.Deliver(changed, "192.168.1.20"); Assert.Empty(browser.Peers); Assert.Equal(1, lost);
        transport.Deliver(Announcement("192.168.1.20"), "192.168.1.20");
        var goodbye = new MdnsMessage { IsResponse = true }; goodbye.Answers.Add(new SrvRecord(Instance, 51001, Host, ttl: 0));
        transport.Deliver(goodbye, "192.168.1.20"); Assert.Empty(browser.Peers); Assert.Equal(2, lost);
    }
    [Fact] public async Task Discovery_UsesReceiverScopeEvenWhenSenderSuppliesAnotherNumber()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport);
        transport.Deliver(Announcement("fe80::20%40"), "fe80::21%7", 7);
        var peer = Assert.Single(browser.Peers); Assert.Equal(7, peer.EndPoint.Address.ScopeId);
        Assert.Equal("[fe80::20%7]:51001", peer.EndpointText);
    }
    [Fact] public async Task Discovery_DropsUnscopedLinkLocalAndNonLanCandidates()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport);
        transport.Deliver(Announcement("8.8.8.8", "127.0.0.1", "::1", "2001:db8::1", "fe80::20"), "192.168.1.30");
        Assert.Empty(browser.Peers);
    }
    [Fact] public async Task Discovery_BoundsCandidatesAndProcessesAddressesBeforeSrvInPacket()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport);
        var packet = Announcement(Enumerable.Range(1, 30).Select(n => $"10.20.0.{n}").ToArray());
        packet.Additionals.Reverse(); transport.Deliver(packet, "10.20.0.30");
        Assert.Equal(V3PeerConnector.MaxCandidates, Assert.Single(browser.Peers).EndPoints.Count);
        Assert.Equal("10.20.0.30", browser.Peers[0].EndPoint.Address.ToString());
    }
    [Fact] public async Task Discovery_ExpiresIndividualAddressWhileKeepingLiveAlternate()
    {
        var transport = new TestTransport(); await using var browser = new PeerBrowser(transport);
        await browser.StartAsync();
        var packet = Announcement("192.168.1.21"); packet.Additionals.Add(new AddressRecord(Host, IPAddress.Parse("192.168.1.20"), ttl: 1));
        transport.Deliver(packet, "192.168.1.20");
        Assert.Equal("192.168.1.20", Assert.Single(browser.Peers).EndPoint.Address.ToString());
        for (int i = 0; i < 40 && browser.Peers[0].EndPoints.Count > 1; i++) await Task.Delay(100);
        Assert.Equal("192.168.1.21", Assert.Single(browser.Peers).EndPoint.Address.ToString());
        Assert.Single(browser.Peers[0].EndPoints);
    }

    [Fact] public async Task MultipleAddresses_FallsBackAfterTcpFailureWithOneConsentAndRealReceipt()
    {
        using var a = IdentityKey.Create(); using var b = IdentityKey.Create();
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0)); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        int approvals = 0;
        var accepting = listener.AcceptAsync(new LocalDevice(b, "B"), (_, _) => { approvals++; return Task.FromResult(true); }, ct);
        await using var sender = await V3PeerConnector.ConnectAsync(new[] {
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), listener.Port), new IPEndPoint(IPAddress.Loopback, listener.Port) }, new LocalDevice(a, "A"), ct);
        await using var receiver = await accepting;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TextReceived = m => { received.TrySetResult(m.Text); return Task.CompletedTask; };
        var runA = sender.RunAsync(() => throw new InvalidOperationException(), ct);
        var runB = receiver.RunAsync(() => throw new InvalidOperationException(), ct);
        await sender.SendTextAsync("多地址真实回执", cancellationToken: ct); Assert.Equal("多地址真实回执", await received.Task); Assert.Equal(1, approvals);
        sender.Close(); receiver.Close(); await IgnoreClosed(runA); await IgnoreClosed(runB);
    }
    [Fact] public async Task RejectedConsent_DoesNotTryAnotherAddressOrAskAgain()
    {
        using var a = IdentityKey.Create(); using var b = IdentityKey.Create();
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0)); listener.Start();
        var alternate = new TcpListener(IPAddress.Loopback, 0); alternate.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); int approvals = 0;
        try
        {
            var accepting = listener.AcceptAsync(new LocalDevice(b, "B"), (_, _) => { approvals++; return Task.FromResult(false); }, deadline.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => V3PeerConnector.ConnectAsync(new[] { new IPEndPoint(IPAddress.Loopback, listener.Port), (IPEndPoint)alternate.LocalEndpoint }, new LocalDevice(a, "A"), deadline.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => accepting); Assert.Equal(1, approvals); Assert.False(alternate.Pending());
        }
        finally { alternate.Stop(); }
    }
    [Fact] public async Task BrokenHandshake_DoesNotFallbackToASecondDevice()
    {
        using var identity = IdentityKey.Create(); var broken = new TcpListener(IPAddress.Loopback, 0); var alternate = new TcpListener(IPAddress.Loopback, 0);
        broken.Start(); alternate.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var server = Task.Run(async () =>
            {
                using var client = await broken.AcceptTcpClientAsync(deadline.Token);
                var stream = client.GetStream(); var header = new byte[4]; await stream.ReadExactlyAsync(header, deadline.Token);
                int length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header));
                Assert.InRange(length, 1, 16384); await stream.ReadExactlyAsync(new byte[length], deadline.Token);
                await stream.WriteAsync(new byte[4], deadline.Token); await Task.Delay(100, deadline.Token);
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => V3PeerConnector.ConnectAsync(new[] { (IPEndPoint)broken.LocalEndpoint, (IPEndPoint)alternate.LocalEndpoint }, new LocalDevice(identity, "A"), deadline.Token));
            await server; Assert.False(alternate.Pending());
        }
        finally { broken.Stop(); alternate.Stop(); }
    }
    [Fact] public async Task CancelBeforeAlternateDial_DoesNotLeaveAQueuedConnection()
    {
        using var identity = IdentityKey.Create(); var alternate = new TcpListener(IPAddress.Loopback, 0); alternate.Start();
        var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start(); var closed = (IPEndPoint)unused.LocalEndpoint; unused.Stop();
        using var deadline = new CancellationTokenSource(60);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => V3PeerConnector.ConnectAsync(new[] { closed, (IPEndPoint)alternate.LocalEndpoint }, new LocalDevice(identity, "A"), deadline.Token));
            Assert.False(alternate.Pending());
        }
        finally { alternate.Stop(); }
    }
    [Fact] public async Task InvalidCandidateList_IsRejectedBeforeAnySocketConnects()
    {
        using var identity = IdentityKey.Create(); var local = new LocalDevice(identity, "A"); var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        try
        {
            var valid = (IPEndPoint)server.LocalEndpoint;
            await Assert.ThrowsAsync<ArgumentException>(() => V3PeerConnector.ConnectAsync(new[] { valid, new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443) }, local));
            await Assert.ThrowsAsync<ArgumentException>(() => V3PeerConnector.ConnectAsync(Enumerable.Repeat(valid, 17).ToArray(), local));
            await Assert.ThrowsAsync<ArgumentException>(() => V3PeerConnector.ConnectAsync(Array.Empty<IPEndPoint>(), local));
            Assert.False(server.Pending());
        }
        finally { server.Stop(); }
    }
    private static async Task IgnoreClosed(Task run) { try { await run; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { } }
    private static readonly DnsName Instance = new("Feige-network-test", "_feige", "_tcp", "local");
    private static readonly DnsName Host = new("network-test", "local");
    private static MdnsMessage Announcement(params string[] addresses)
    {
        var packet = new MdnsMessage { IsResponse = true };
        packet.Answers.Add(new PtrRecord(FeigeService.Type, Instance));
        packet.Additionals.Add(new SrvRecord(Instance, 51001, Host));
        packet.Additionals.Add(new TxtRecord(Instance, ["version=3", "name=Network Test"]));
        foreach (var address in addresses) packet.Additionals.Add(new AddressRecord(Host, IPAddress.Parse(address)));
        return packet;
    }
    private sealed class TestTransport : IMdnsTransport
    {
        public event EventHandler<MdnsDatagram>? DatagramReceived;
        public ValueTask SendAsync(MdnsMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendAsync(MdnsMessage message, IPEndPoint target, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Deliver(MdnsMessage message, string source, long scope = 0)
            => DatagramReceived?.Invoke(this, new(message, new IPEndPoint(IPAddress.Parse(source), 5353), scope));
    }
}
