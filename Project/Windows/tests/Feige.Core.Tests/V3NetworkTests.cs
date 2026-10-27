using System.Net;
using System.Net.Sockets;
using Feige.Core.Crypto;
using Feige.Core.Net;

namespace Feige.Core.Tests;

public sealed class V3NetworkTests
{
    [Fact]
    public async Task Listener_RenameDuringPendingAcceptUsesCurrentNameInAuthenticatedHandshake()
    {
        using var identityA = IdentityKey.Create(); using var identityB = IdentityKey.Create();
        var local = new LocalDevice(identityB, "界面验证设备");
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int reads = 0, approvals = 0;
        var accepting = listener.AcceptAsync(() => { reads++; return local; },
            (_, _) => { approvals++; return Task.FromResult(true); }, deadline.Token);
        Assert.Equal(0, reads);
        local = new LocalDevice(identityB, "Mac虚拟机");

        await using var sender = await V3PeerConnector.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port),
            new LocalDevice(identityA, "ROG"), deadline.Token);
        await using var receiver = await accepting;
        Assert.Equal("Mac虚拟机", sender.PeerName);
        Assert.Equal(local.Fingerprint.Replace(" ", ""), sender.PeerFingerprint.Replace(" ", ""));
        Assert.Equal("ROG", receiver.PeerName);
        Assert.Equal(1, reads);
        Assert.Equal(1, approvals);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.0.0.1")] [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")] [InlineData("192.168.1.1")] [InlineData("169.254.1.2")]
    [InlineData("::1")] [InlineData("fc00::1")] [InlineData("fdff::1")]
    [InlineData("fe80::1%1")] [InlineData("::ffff:192.168.1.1")]
    public void ManualTarget_AcceptsLanAndLoopback(string ip)
        => Assert.Equal(IPAddress.Parse(ip), V3PeerConnector.ParseTarget(ip, 12345).Address);

    [Theory]
    [InlineData("example.com")] [InlineData("8.8.8.8")] [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")] [InlineData("0.0.0.0")] [InlineData("224.0.0.1")]
    [InlineData("::")] [InlineData("2001:4860:4860::8888")] [InlineData("::ffff:8.8.8.8")]
    public void ManualTarget_RejectsPublicDomainsAndWildcard(string ip)
        => Assert.Throws<ArgumentException>(() => V3PeerConnector.ParseTarget(ip, 12345));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(65536)]
    public void ManualTarget_RejectsInvalidPort(int port)
        => Assert.Throws<ArgumentException>(() => V3PeerConnector.ParseTarget("127.0.0.1", port));

    [Fact]
    public async Task Listener_StillAcceptsAfterBrokenHandshakeAndSupportsV3Text()
    {
        using var identityA = IdentityKey.Create(); using var identityB = IdentityKey.Create();
        var a = new LocalDevice(identityA, "A"); var b = new LocalDevice(identityB, "B");
        await using var listener = new V3PeerListener(); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = deadline.Token;
        var broken = listener.AcceptAsync(b, (_, _) => Task.FromResult(true), ct);
        using (var client = new TcpClient())
        { await client.ConnectAsync(IPAddress.Loopback, listener.Port); await client.GetStream().WriteAsync(new byte[4], ct); }
        await Assert.ThrowsAnyAsync<Exception>(() => broken);
        var accepting = listener.AcceptAsync(b, (_, _) => Task.FromResult(true), ct);
        await using var sender = await V3PeerConnector.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), a, ct);
        await using var receiver = await accepting;
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TextReceived = m => { delivered.TrySetResult(m.Text); return Task.CompletedTask; };
        var runA = sender.RunAsync(() => throw new InvalidOperationException(), ct);
        var runB = receiver.RunAsync(() => throw new InvalidOperationException(), ct);
        await sender.SendTextAsync("v3 TCP", cancellationToken: ct);
        Assert.Equal("v3 TCP", await delivered.Task);
        sender.Close(); receiver.Close();
        foreach (var run in new[] { runA, runB })
            try { await run; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
