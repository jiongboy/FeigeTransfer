using System.Net;
using System.Text;
using Feige.Core.Discovery;
using Xunit;

namespace Feige.Core.Tests;

public sealed class UnicastProbeTests
{
    private static readonly ProbeAdvertisement Advertisement = new("PC", new string('A', 64), "3,4,5,6", 52000, null);

    [Fact]
    public void TargetsAcceptOnlyPrivateAddressesAndSmallPrefixes()
    {
        Assert.Equal([0xC0A83C0Cu], ProbeTargets.Hosts("192.168.60.12"));
        Assert.Equal(254, ProbeTargets.Hosts("10.0.0.0/24")!.Count);
        Assert.Equal([0xAC100104u, 0xAC100105u], ProbeTargets.Hosts("172.16.1.4/31"));
        foreach (string invalid in new[] { "8.8.8.8", "127.0.0.1", "172.32.0.1", "10.0.0.0/23", "10.0.0.0/33", "10.0.0.0/", "010.0.0.1",
                     "10.0.0", "10.0.0.256", "host.local", "", "fe80::1" })
            Assert.Null(ProbeTargets.Hosts(invalid));
    }

    [Fact]
    public void TargetListDeduplicatesAndEnforcesEntryLimit()
    {
        Assert.Equal(2, ProbeTargets.Hosts(new[] { "10.0.0.1", "10.0.0.0/30" })!.Count);
        Assert.Null(ProbeTargets.Hosts(Enumerable.Repeat("10.0.0.1", 17).ToArray()));
        Assert.Null(ProbeTargets.Hosts(new[] { "10.0.0.1", "8.8.8.8" }));
    }

    [Fact]
    public void RequestIsPaddedAndReplyNeverExceedsIt()
    {
        string nonce = Convert.ToBase64String(new byte[16]);
        byte[] request = ProbeMessage.CreateRequest(nonce);
        Assert.Equal(ProbeMessage.DatagramSize, request.Length);
        byte[] reply = ProbeMessage.CreateReply(request, Advertisement)!;
        Assert.True(reply.Length <= request.Length);
        var parsed = ProbeMessage.ParseReply(reply, IPAddress.Parse("10.0.0.2"))!.Value;
        Assert.Equal(nonce, parsed.Nonce);
        Assert.Equal(new HashSet<int> { 4, 5, 6 }, parsed.Peer.ProtocolVersions);
        Assert.Null(ProbeMessage.CreateReply(request.AsSpan(0, 100), Advertisement));
    }

    [Fact]
    public void MalformedRepliesAreRejected()
    {
        static byte[] Reply(string name, string fp, string caps, int port = 1) =>
            Encoding.UTF8.GetBytes($$"""{"feige":"reply","v":1,"nonce":"n","name":"{{name}}","fp":"{{fp}}","caps":"{{caps}}","port":{{port}}}""");
        string fp = new('b', 64);
        var address = IPAddress.Parse("10.0.0.1");
        Assert.Equal(fp.ToUpperInvariant(), ProbeMessage.ParseReply(Reply("A", fp, "4"), address)!.Value.Peer.Fingerprint);
        Assert.Null(ProbeMessage.ParseReply(Reply("", fp, "4"), address));
        Assert.Null(ProbeMessage.ParseReply(Reply("A", "zz", "4"), address));
        Assert.Null(ProbeMessage.ParseReply(Reply("A", fp, "3"), address));
        Assert.Null(ProbeMessage.ParseReply(Reply("A", fp, "4", 0), address));
        Assert.Null(ProbeMessage.ParseReply("not json"u8, address));
    }

    [Fact]
    public async Task LoopbackScanFindsResponder()
    {
        // Ask the OS for a free UDP port; a fixed random range collided with other tests and local apps.
        int port;
        using (var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
        {
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
            port = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        }
        await using var responder = new UnicastProbe(Advertisement, respondsToProbes: true, port);
        responder.Start();
        Assert.True(responder.CanRespond);
        await using var scanner = new UnicastProbe(Advertisement with { Fingerprint = new string('B', 64) }, respondsToProbes: false, port);
        var found = new TaskCompletionSource<ProbePeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.PeerFound += peer => found.TrySetResult(peer);
        scanner.Start();
        await scanner.ScanAsync([0x7F000001], CancellationToken.None);
        ProbePeer peer = await found.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new string('A', 64), peer.Fingerprint);
    }
}
