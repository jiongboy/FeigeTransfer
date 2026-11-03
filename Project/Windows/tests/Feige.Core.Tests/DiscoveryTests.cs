using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Feige.Core.Crypto;
using Feige.Core.Discovery;
using Feige.Core.Net;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// DNS-SD discovery: the hand-rolled mDNS codec (including compression pointers), the responder's answer rules, and a
/// full advertise → browse → goodbye cycle over an in-memory network.
/// </summary>
public sealed class DiscoveryTests
{
    [Fact]
    public void Codec_RoundTripsEveryRecordTypeUsedByTheService()
    {
        DnsName instance = FeigeService.InstanceName("Win-A");
        var host = new DnsName("win-a-feige", "local");

        var message = new MdnsMessage { IsResponse = true, IsAuthoritative = true };
        message.Answers.Add(new PtrRecord(FeigeService.Type, instance));
        message.Additionals.Add(new SrvRecord(instance, 51_234, host));
        message.Additionals.Add(new TxtRecord(instance, FeigeService.BuildTxt("Win-A", "AB CD EF")));
        message.Additionals.Add(new AddressRecord(host, IPAddress.Parse("192.168.1.10")));
        message.Additionals.Add(new AddressRecord(host, IPAddress.Parse("fe80::1")));

        MdnsMessage? parsed = MdnsMessage.TryParse(message.ToArray());

        Assert.NotNull(parsed);
        Assert.True(parsed.IsResponse);
        Assert.True(parsed.IsAuthoritative);

        var ptr = Assert.IsType<PtrRecord>(parsed.Answers[0]);
        Assert.Equal(FeigeService.Type, ptr.Name);
        Assert.Equal(instance, ptr.Target);
        Assert.Equal(MdnsDefaults.ServiceTtl, ptr.Ttl);

        var srv = Assert.IsType<SrvRecord>(parsed.Additionals[0]);
        Assert.Equal((ushort)51_234, srv.Port);
        Assert.Equal(host, srv.Target);
        Assert.True(srv.CacheFlush);

        var txt = Assert.IsType<TxtRecord>(parsed.Additionals[1]);
        Assert.Equal("Win-A", txt.GetValue("name"));
        Assert.Equal("ABCDEF", txt.GetValue("fp"));
        Assert.Equal("1", txt.GetValue("v"));

        var v4 = Assert.IsType<AddressRecord>(parsed.Additionals[2]);
        Assert.Equal(DnsRecordType.A, v4.Type);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), v4.Address);

        var v6 = Assert.IsType<AddressRecord>(parsed.Additionals[3]);
        Assert.Equal(DnsRecordType.Aaaa, v6.Type);
        Assert.Equal(IPAddress.Parse("fe80::1"), v6.Address);
    }

    [Fact]
    public void Codec_FollowsCompressionPointersInQuestionsAndRdata()
    {
        DnsName instance = FeigeService.InstanceName("Dev-A");

        // Hand-built response: the question holds the full service name; the answer and the PTR rdata point back at it.
        var writer = new List<byte>();
        void U16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            writer.AddRange(buffer.ToArray());
        }

        void U32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            writer.AddRange(buffer.ToArray());
        }

        U16(0);
        U16(0x8400);
        U16(1);
        U16(1);
        U16(0);
        U16(0);

        // Question at offset 12: _feige._tcp.local PTR IN
        foreach (string label in FeigeService.Type.Labels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            writer.Add((byte)bytes.Length);
            writer.AddRange(bytes);
        }

        writer.Add(0);
        U16((ushort)DnsRecordType.Ptr);
        U16(DnsClass.In);

        // Answer: name = pointer to offset 12, PTR, IN, ttl 4500, rdata = "Dev-A" + pointer to offset 12.
        writer.AddRange([0xC0, 0x0C]);
        U16((ushort)DnsRecordType.Ptr);
        U16(DnsClass.In);
        U32(MdnsDefaults.ServiceTtl);
        int rdataLength = 1 + Encoding.UTF8.GetByteCount(instance.FirstLabel) + 2;
        U16((ushort)rdataLength);
        byte[] label0 = Encoding.UTF8.GetBytes(instance.FirstLabel);
        writer.Add((byte)label0.Length);
        writer.AddRange(label0);
        writer.AddRange([0xC0, 0x0C]);

        MdnsMessage? parsed = MdnsMessage.TryParse(writer.ToArray());

        Assert.NotNull(parsed);
        Assert.Single(parsed.Questions);
        Assert.Equal(FeigeService.Type, parsed.Questions[0].Name);

        var ptr = Assert.IsType<PtrRecord>(parsed.Answers[0]);
        Assert.Equal(FeigeService.Type, ptr.Name);
        Assert.Equal(instance, ptr.Target);
    }

    [Fact]
    public void Codec_RejectsMalformedDatagramsInsteadOfThrowing()
    {
        Assert.Null(MdnsMessage.TryParse([]));
        Assert.Null(MdnsMessage.TryParse(new byte[11]));

        // Header claims one answer but there is no body.
        byte[] empty = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(empty.AsSpan(6), 1);
        Assert.Null(MdnsMessage.TryParse(empty));

        // Self-referencing compression pointer: must be rejected, not loop forever.
        var loop = new List<byte>(new byte[12]);
        BinaryPrimitives.WriteUInt16BigEndian(loop.ToArray().AsSpan(4), 1);
        loop[4] = 0;
        loop[5] = 1; // one question
        loop.AddRange([0xC0, 0x0C]);
        loop.AddRange([0, 12]);
        loop.AddRange([0, 1]);
        Assert.Null(MdnsMessage.TryParse(loop.ToArray()));
    }

    [Fact]
    public void ServiceType_AndTxtFollowTheDocumentedShape()
    {
        Assert.Equal(["_feige", "_tcp", "local"], FeigeService.Type.Labels);
        Assert.Equal("_feige._tcp.local", FeigeService.Type.ToString());

        DnsName instance = FeigeService.InstanceName("Mac-A");
        Assert.Equal("Mac-A", instance.FirstLabel);
        Assert.Equal("_feige._tcp.local", new DnsName(instance.Labels.Skip(1)).ToString());

        // A long name is truncated in the instance label (63-byte DNS limit) but kept intact in TXT.
        DnsName longInstance = FeigeService.InstanceName(new string('x', 100));
        Assert.True(Encoding.UTF8.GetByteCount(longInstance.FirstLabel) <= 63);

        IReadOnlyList<string> txt = FeigeService.BuildTxt("Win-A", "AA BB CC");
        Assert.Contains("v=1", txt);
        Assert.Contains("name=Win-A", txt);
        Assert.Contains("fp=AABBCC", txt);

        (string name, string? fingerprint) = FeigeService.ParseTxt(txt);
        Assert.Equal("Win-A", name);
        Assert.Equal("AABBCC", fingerprint);

        string fullFingerprint = new('A', 64);
        DnsName v3Instance = FeigeService.InstanceName("Win-A", fullFingerprint, version: 3);
        DnsName v4Instance = FeigeService.InstanceName("Win-A", fullFingerprint, version: 4);
        Assert.Equal("Feige-" + fullFingerprint[..20], v3Instance.FirstLabel);
        Assert.Equal("Feige4-" + fullFingerprint[..20], v4Instance.FirstLabel);
        Assert.NotEqual(v3Instance, v4Instance);
        Assert.Equal(FeigeService.Type, new DnsName(v3Instance.Labels.Skip(1)));
        Assert.Equal(FeigeService.Type, new DnsName(v4Instance.Labels.Skip(1)));

        IReadOnlyList<string> v4Txt = FeigeService.BuildTxt("Win-A", fullFingerprint, version: 4);
        Assert.Contains("v=4", v4Txt);
        Assert.Contains("caps=3,4,5,6", v4Txt);
        Assert.Equal(4, FeigeService.ParseProtocolVersion(v4Txt));
        Assert.Equal(3, FeigeService.ParseProtocolVersion(["version=3", "name=Win-A"]));
        Assert.Equal(1, FeigeService.ParseProtocolVersion(txt));

        (string macName, string? macFingerprint) = FeigeService.ParseTxt([
            "v=3", "name=Mac-A", $"fp={fullFingerprint}", "caps=3,4"]);
        Assert.Equal("Mac-A", macName);
        Assert.Equal(fullFingerprint, macFingerprint);
        IReadOnlySet<int> macVersions = FeigeService.ParseSupportedVersions([
            "v=3", "name=Mac-A", $"fp={fullFingerprint}", "caps=3,4,5"]);
        Assert.Equal(3, macVersions.Count);
        Assert.Contains(3, macVersions);
        Assert.Contains(4, macVersions);
        Assert.Contains(5, macVersions);
    }

    [Fact]
    public void AvatarTxtMetadataIsOptionalAndUnknownIdsAreIgnored()
    {
        string fingerprint = new('C', 64);
        IReadOnlyList<string> advertised = FeigeService.BuildTxt("Win-A", fingerprint, version: 4, avatarId: "fox");
        Assert.Contains("avatar=fox", advertised);
        Assert.Equal("fox", FeigeService.ParseAvatarId(advertised));

        IReadOnlyList<string> legacy = ["v=4", "name=Old device", $"fp={fingerprint}", "caps=3,4,5"];
        Assert.Null(FeigeService.ParseAvatarId(legacy));
        Assert.Null(FeigeService.ParseAvatarId(["v=4", "name=Peer", "avatar=attacker-controlled"]));
        Assert.DoesNotContain(FeigeService.BuildTxt("Win-A", fingerprint, version: 4, avatarId: "unknown"),
            entry => entry.StartsWith("avatar=", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_AdvertisedTxtRoundTripsThroughTheSameRulesMacUses()
    {
        // macOS advertises TXT entries as "name", "v" and "caps" (its "fp" key is not read by
        // Windows today), so the fingerprint must come from the fingerprinted instance label.
        // Whatever Windows advertises must parse back identically, and the self-ignore rule that
        // keeps a device from listing itself must see the same instance label from both sides.
        string fullFingerprint = new('B', 64);
        string compactFingerprint = fullFingerprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        IReadOnlyList<string> macAdvert = ["v=3", "caps=3,4,5", "name=Mac-A", $"fp={compactFingerprint}"];
        Assert.Equal(3, FeigeService.ParseProtocolVersion(macAdvert));
        Assert.Equal(new[] { 3, 4, 5 }, FeigeService.ParseSupportedVersions(macAdvert).OrderBy(item => item));
        Assert.Equal(new[] { 3 }, FeigeService.ParseSupportedVersions(["v=3", "caps=3", "name=Mac-B"]).OrderBy(item => item));
        DnsName macInstance = FeigeService.InstanceName("Mac-A", fullFingerprint, version: 3);
        Assert.Equal("Feige-" + compactFingerprint[..20], macInstance.FirstLabel);
        Assert.Equal(macInstance, FeigeService.InstanceName("Mac-A", compactFingerprint, version: 3));

        foreach (int version in new[] { 3, 4 })
        {
            IReadOnlyList<string> txt = FeigeService.BuildTxt("Win-Acceptance", fullFingerprint.WithSpaces(), version);
            (string name, string? fingerprint) = FeigeService.ParseTxt(txt);
            IReadOnlySet<int> versions = FeigeService.ParseSupportedVersions(txt);
            Assert.Equal("Win-Acceptance", name);
            Assert.Contains(version, versions);
            // Only the group-capable v4 advertisement carries the fingerprint and the 3,4,5 caps;
            // the legacy v3 record keeps its historical "version=/name=" shape.
            if (version == 4)
            {
                Assert.Equal(compactFingerprint, fingerprint);
                Assert.Contains(3, versions);
                Assert.Contains(4, versions);
                Assert.Contains(5, versions);
            }
        }

        // Windows names v3 and v4 advertisements differently (Feige- / Feige4-) and browses both
        // labels; both platforms derive the label from the same compact fingerprint.
        Assert.Equal("Feige-" + compactFingerprint[..20],
            FeigeService.InstanceName("Win-Acceptance", fullFingerprint, version: 3).FirstLabel);
        Assert.Equal("Feige4-" + compactFingerprint[..20],
            FeigeService.InstanceName("Win-Acceptance", fullFingerprint, version: 4).FirstLabel);
        Assert.Equal(macInstance.FirstLabel,
            FeigeService.InstanceName("Win-Acceptance", compactFingerprint, version: 3).FirstLabel);
    }

    [Fact]
    public void DiscoveredPeer_ExposesAReadableAccessibleName()
    {
        var peer = new DiscoveredPeer(new DnsName(["Feige-ABC", "_feige", "_tcp", "local"]), "Songli", null,
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.10"), 51_001));
        Assert.Equal("Songli，192.168.1.10:51001，指纹待握手确认", peer.AccessibleName);
        Assert.Equal(peer.AccessibleName, peer.ToString());
        Assert.DoesNotContain("DiscoveredPeer {", peer.ToString());
    }

    [Fact]
    public async Task Discovery_AdvertisesBrowsesAndSaysGoodbye()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        var deviceA = new LocalDevice(identityA, "Win-A");
        var deviceB = new LocalDevice(identityB, "Win-B");

        MemoryNetwork network = new();
        MemoryTransport transportA = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 5353));
        MemoryTransport transportB = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.11"), 5353));

        await using var discoveryA = new LanDiscovery(deviceA, 51_001, transportA, [IPAddress.Parse("192.168.1.10")]);
        await using var discoveryB = new LanDiscovery(deviceB, 51_002, transportB, [IPAddress.Parse("192.168.1.11")]);

        var found = new System.Collections.Concurrent.ConcurrentQueue<DiscoveredPeer>();
        discoveryB.PeerFound += (_, peer) => found.Enqueue(peer);

        await discoveryA.StartAsync();
        await discoveryB.StartAsync();

        await WaitUntilAsync(() => discoveryB.Peers.Count == 1 &&
            found.LastOrDefault()?.Fingerprint == identityA.Fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal), "B never resolved A");
        DiscoveredPeer peer = discoveryB.Peers[0];

        Assert.Equal("Win-A", peer.DeviceName);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), peer.EndPoint.Address);
        Assert.Equal(51_001, peer.EndPoint.Port);
        Assert.Equal(identityA.Fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal), peer.Fingerprint);
        // A partial SRV/address response can now be followed by a TXT metadata update.
        Assert.Single(found.Select(item => item.Instance).Distinct());
        Assert.Equal(peer, found.Last());

        // Self-ignore: A must not list itself even though its own mDNS traffic loops back — it sees only B.
        await WaitUntilAsync(() => discoveryA.Peers.Count == 1, "A never saw B");
        Assert.Equal("Win-B", discoveryA.Peers[0].DeviceName);

        // Goodbye (TTL 0) removes the peer without waiting for the 4500 s PTR TTL.
        await discoveryA.DisposeAsync();
        await WaitUntilAsync(() => discoveryB.Peers.Count == 0, "B did not drop A after goodbye");
    }

    [Fact]
    public async Task V3Discovery_UsesFingerprintInstanceFiltersV1AndRemovesGoodbye()
    {
        using var identityA = IdentityKey.Create(); using var identityB = IdentityKey.Create();
        var network = new MemoryNetwork();
        var transportA = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 5353));
        var transportB = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.11"), 5353));
        await using var a = new LanDiscovery(new LocalDevice(identityA, "Same name"), 51001,
            transportA, [IPAddress.Parse("192.168.1.10")], version: 3);
        await using var b = new LanDiscovery(new LocalDevice(identityB, "Same name"), 51002,
            transportB, [IPAddress.Parse("192.168.1.11")], version: 3);
        var old = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.12"), 5353));
        await a.StartAsync(); await b.StartAsync();
        await WaitUntilAsync(() => a.Peers.Count == 1 && b.Peers.Count == 1, "V3 devices did not discover one another");
        Assert.Equal("Feige-" + identityA.Fingerprint.Replace(" ", "")[..20], a.Advertiser.InstanceName.FirstLabel);
        Assert.Equal(3, b.Peers[0].ProtocolVersion);
        Assert.Contains("version=3", a.Advertiser.Txt);
        Assert.DoesNotContain(a.Advertiser.Txt, entry => entry.StartsWith("v=") || entry.StartsWith("fp="));
        Assert.Null(b.Peers[0].Fingerprint); // Only the signed handshake authenticates a v3 peer.
        var message = new MdnsMessage { IsResponse = true };
        var instance = FeigeService.InstanceName("Legacy"); var host = new DnsName("legacy", "local");
        message.Answers.Add(new PtrRecord(FeigeService.Type, instance));
        message.Additionals.Add(new SrvRecord(instance, 51003, host));
        message.Additionals.Add(new TxtRecord(instance, ["v=1", "name=Legacy"]));
        message.Additionals.Add(new AddressRecord(host, IPAddress.Parse("192.168.1.12")));
        var processedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transportA.DatagramReceived += (_, datagram) => { if (ReferenceEquals(datagram.Message, message)) processedA.TrySetResult(); };
        transportB.DatagramReceived += (_, datagram) => { if (ReferenceEquals(datagram.Message, message)) processedB.TrySetResult(); };
        await old.SendAsync(message);
        await Task.WhenAll(processedA.Task, processedB.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(a.Peers); Assert.Single(b.Peers);
        await a.DisposeAsync();
        await WaitUntilAsync(() => b.Peers.Count == 0, "V3 goodbye did not remove the peer");
    }

    [Fact]
    public async Task DualVersionDiscovery_AdvertisesBothVersionsAndIgnoresBothLocalInstances()
    {
        using var identityA = IdentityKey.Create();
        using var identityB = IdentityKey.Create();
        var network = new MemoryNetwork();
        var addressA = IPAddress.Parse("192.168.1.10");
        var addressB = IPAddress.Parse("192.168.1.11");
        await using var a = new LanDiscovery(new LocalDevice(identityA, "Same name"), 51011, 51012,
            network.Connect(new IPEndPoint(addressA, 5353)), [addressA]);
        await using var b = new LanDiscovery(new LocalDevice(identityB, "Same name"), 51021, 51022,
            network.Connect(new IPEndPoint(addressB, 5353)), [addressB]);

        Assert.Equal(FeigeService.Type, new DnsName(a.V3Advertiser!.InstanceName.Labels.Skip(1)));
        Assert.Equal(FeigeService.Type, new DnsName(a.V4Advertiser!.InstanceName.Labels.Skip(1)));
        Assert.NotEqual(a.V3Advertiser.InstanceName, a.V4Advertiser.InstanceName);
        Assert.Equal(2, a.Advertisers.Count);
        Assert.Equal(a.V3Advertiser, a.Advertiser);

        await a.StartAsync();
        await b.StartAsync();
        await WaitUntilAsync(() => a.Peers.Count == 2 && b.Peers.Count == 2,
            "dual-version peers did not discover both remote instances");

        Assert.Equal([3, 6], a.Peers.Select(peer => peer.ProtocolVersion).Order().ToArray());
        Assert.Equal([3, 6], b.Peers.Select(peer => peer.ProtocolVersion).Order().ToArray());
        Assert.All(a.Peers, peer => Assert.Equal("Same name", peer.DeviceName));
        Assert.All(b.Peers, peer => Assert.Equal("Same name", peer.DeviceName));
        Assert.Equal(51021, a.Peers.Single(peer => peer.ProtocolVersion == 3).EndPoint.Port);
        Assert.Equal(51022, a.Peers.Single(peer => peer.ProtocolVersion >= 4).EndPoint.Port);
        Assert.Equal(51011, b.Peers.Single(peer => peer.ProtocolVersion == 3).EndPoint.Port);
        Assert.Equal(51012, b.Peers.Single(peer => peer.ProtocolVersion >= 4).EndPoint.Port);
        Assert.DoesNotContain(a.Peers, peer => a.Advertisers.Any(local => local.InstanceName == peer.Instance));
        Assert.DoesNotContain(b.Peers, peer => b.Advertisers.Any(local => local.InstanceName == peer.Instance));

        await a.DisposeAsync();
        await WaitUntilAsync(() => b.Peers.Count == 0, "dual-version goodbyes did not remove both peers");
    }

    [Theory]
    [InlineData("version=2", false)]
    [InlineData("version=3", true)]
    public async Task V3Discovery_WithdrawsPeerWhenVersionChanges(string version, bool goodbye)
    {
        using var identity = IdentityKey.Create(); var network = new MemoryNetwork();
        var remote = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.12"), 5353));
        await using var discovery = new LanDiscovery(new LocalDevice(identity, "Local"), 51001,
            network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 5353)), [IPAddress.Parse("192.168.1.10")], version: 3);
        await discovery.StartAsync();
        var instance = new DnsName("Feige-remote", "_feige", "_tcp", "local"); var host = new DnsName("remote", "local");
        var message = new MdnsMessage { IsResponse = true };
        message.Answers.Add(new PtrRecord(FeigeService.Type, instance));
        message.Additionals.Add(new SrvRecord(instance, 51002, host));
        message.Additionals.Add(new TxtRecord(instance, ["version=3", "name=Remote"]));
        message.Additionals.Add(new AddressRecord(host, IPAddress.Parse("192.168.1.12")));
        await remote.SendAsync(message);
        await WaitUntilAsync(() => discovery.Peers.Count == 1, "Compatible peer not discovered");
        var withdrawal = new MdnsMessage { IsResponse = true };
        withdrawal.Answers.Add(new TxtRecord(instance, [version, "name=Remote"], goodbye ? 0u : 120u));
        await remote.SendAsync(withdrawal);
        await WaitUntilAsync(() => discovery.Peers.Count == 0, "Incompatible/expired TXT left a stale peer");
    }

    [Fact]
    public async Task Discovery_AnswersUnicastWhenTheQueryAsksForIt()
    {
        using IdentityKey identity = IdentityKey.Create();
        var device = new LocalDevice(identity, "Win-A");

        MemoryNetwork network = new();
        MemoryTransport advertiserTransport = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 5353));
        MemoryTransport querier = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 40_000));

        await using var discovery = new LanDiscovery(device, 51_001, advertiserTransport, [IPAddress.Parse("192.168.1.10")]);
        await discovery.StartAsync();

        // QU bit set (RFC 6762 §5.4) — and a non-5353 source port also asks for unicast (§6.7).
        var query = new MdnsMessage();
        query.Questions.Add(new MdnsQuestion(FeigeService.Type, DnsRecordType.Ptr, UnicastResponse: true));
        await querier.SendAsync(query);

        await WaitUntilAsync(
            () => advertiserTransport.Sent.Exists(entry => entry.Target is not null),
            "advertiser never answered by unicast");

        (MdnsMessage answer, IPEndPoint? target) = advertiserTransport.Sent.First(entry => entry.Target is not null);
        Assert.Equal(querier.Address, target);
        Assert.Contains(answer.Answers, record => record is PtrRecord);
        Assert.Contains(answer.Additionals, record => record is SrvRecord);
        Assert.Contains(answer.Additionals, record => record is AddressRecord);
    }

    [Fact]
    public async Task Discovery_ExpiresPeersWhenRecordsAreNotRefreshed()
    {
        MemoryNetwork network = new();
        MemoryTransport transport = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.11"), 5353));
        MemoryTransport fake = network.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.30"), 5353));

        using IdentityKey identity = IdentityKey.Create();
        var device = new LocalDevice(identity, "Win-B");
        await using var discovery = new LanDiscovery(device, 51_002, transport, [IPAddress.Parse("192.168.1.11")]);
        await discovery.StartAsync();

        // A one-second advertisement that is never repeated: the browser must forget it once it expires.
        DnsName instance = FeigeService.InstanceName("Ghost");
        var host = new DnsName("ghost-feige", "local");
        var announcement = new MdnsMessage { IsResponse = true, IsAuthoritative = true };
        announcement.Answers.Add(new PtrRecord(FeigeService.Type, instance, ttl: 1));
        announcement.Answers.Add(new SrvRecord(instance, 51_003, host, ttl: 1));
        announcement.Answers.Add(new TxtRecord(instance, FeigeService.BuildTxt("Ghost", "00"), ttl: 1));
        announcement.Additionals.Add(new AddressRecord(host, IPAddress.Parse("192.168.1.30"), ttl: 1));
        await fake.SendAsync(announcement);

        await WaitUntilAsync(() => discovery.Peers.Count == 1, "ghost peer was never found");
        Assert.Equal("Ghost", discovery.Peers[0].DeviceName);

        await WaitUntilAsync(() => discovery.Peers.Count == 0, "ghost peer never expired", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Advertiser_RejectsPortsOutsideTheValidRange()
    {
        using IdentityKey identity = IdentityKey.Create();
        var device = new LocalDevice(identity, "Win-A");
        MemoryTransport transport = new MemoryNetwork().Connect(new IPEndPoint(IPAddress.Loopback, 5353));

        Assert.Throws<ArgumentOutOfRangeException>(() => new MdnsAdvertiser(transport, device, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MdnsAdvertiser(transport, device, 70_000));
    }

    [Fact]
    public async Task UdpTransport_BindsJoinsAndReceives()
    {
        // A high port instead of 5353 so a real Bonjour/print service on the machine does not interfere; the socket
        // wiring (reuse-address, multicast join, receive loop, codec) is the same.
        const int port = 15_353;
        await using var transport = new UdpMdnsTransport(port);

        var received = new TaskCompletionSource<MdnsDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.DatagramReceived += (_, datagram) => received.TrySetResult(datagram);

        transport.Start();
        Assert.True(transport.JoinedInterfaceCount > 0, "no interface joined the mDNS group");

        var query = new MdnsMessage();
        query.Questions.Add(new MdnsQuestion(FeigeService.Type, DnsRecordType.Ptr));

        // Unicast to ourselves: proves the socket receives and the datagram is parsed end to end.
        await transport.SendAsync(query, new IPEndPoint(IPAddress.Loopback, port));

        MdnsDatagram datagram = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FeigeService.Type, datagram.Message.Questions[0].Name);
        Assert.Equal(DnsRecordType.Ptr, datagram.Message.Questions[0].Type);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(because);
    }

    /// <summary>Shared medium: every transport hears every datagram, the way a real multicast group behaves.</summary>
    private sealed class MemoryNetwork
    {
        private readonly List<MemoryTransport> _nodes = [];

        public MemoryTransport Connect(IPEndPoint address)
        {
            var node = new MemoryTransport(this, address);
            _nodes.Add(node);
            return node;
        }

        public void Broadcast(MdnsMessage message, IPEndPoint source)
        {
            foreach (MemoryTransport node in _nodes.ToArray())
            {
                node.Deliver(new MdnsDatagram(message, source));
            }
        }
    }

    private sealed class MemoryTransport : IMdnsTransport
    {
        private readonly MemoryNetwork _network;
        private readonly Channel<MdnsDatagram> _inbox = Channel.CreateUnbounded<MdnsDatagram>();

        public MemoryTransport(MemoryNetwork network, IPEndPoint address)
        {
            _network = network;
            Address = address;
            _ = PumpAsync();
        }

        public event EventHandler<MdnsDatagram>? DatagramReceived;

        public IPEndPoint Address { get; }

        public List<(MdnsMessage Message, IPEndPoint? Target)> Sent { get; } = [];

        public ValueTask SendAsync(MdnsMessage message, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((message, null));
            }

            _network.Broadcast(message, Address);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(MdnsMessage message, IPEndPoint target, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((message, target));
            }

            // Unicast in the test medium is still visible to everyone; only the recorded target matters.
            _network.Broadcast(message, Address);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Deliver(MdnsDatagram datagram) => _inbox.Writer.TryWrite(datagram);

        private async Task PumpAsync()
        {
            await foreach (MdnsDatagram datagram in _inbox.Reader.ReadAllAsync())
            {
                try
                {
                    DatagramReceived?.Invoke(this, datagram);
                }
                catch (Exception)
                {
                    // Subscriber failures are not the test medium's problem.
                }
            }
        }
    }
}

file static class DiscoveryTestText
{
    /// <summary>Same layout the device identity prints: groups of four hex digits.</summary>
    public static string WithSpaces(this string compact) =>
        string.Join(' ', Enumerable.Range(0, compact.Length / 4).Select(i => compact.Substring(i * 4, 4)));
}
