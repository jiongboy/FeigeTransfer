using System.Net;
using Feige.Core.Conversations;
using Feige.Core.Discovery;

namespace Feige.Core.Tests;

public sealed class ConversationDisplayNameTests
{
    private static readonly string Fingerprint = new('A', 64);
    private static readonly DnsName Instance = FeigeService.InstanceName("peer", Fingerprint, version: 3);
    private static readonly DnsName Host = new("rename-test", "local");

    [Fact]
    public async Task TxtRename_UpdatesExistingConversationWithoutReconnectOrChangingIdentity()
    {
        var transport = new TestTransport();
        await using var browser = new PeerBrowser(transport) { RequiredVersion = 3 };
        var display = new ConversationDisplayName(Fingerprint, "界面验证设备");
        int changed = 0;
        browser.PeerFound += (_, peer) => { if (display.ApplyDiscovery(peer)) changed++; };
        transport.Deliver(Announcement("界面验证设备"));

        var renamed = new MdnsMessage { IsResponse = true };
        renamed.Answers.Add(new TxtRecord(Instance, ["version=3", "name=Mac虚拟机"]));
        transport.Deliver(renamed);

        Assert.Equal("Mac虚拟机", display.Name);
        Assert.Equal(Fingerprint, display.Fingerprint);
        Assert.Equal("Mac虚拟机", Assert.Single(browser.Peers).DeviceName);
        Assert.Equal(1, changed);
        transport.Deliver(renamed);
        Assert.Equal(1, changed); // Duplicate broadcasts do not churn the UI.
    }

    [Fact]
    public async Task DiscoveryRefresh_UpdatesRetainedConversationAndKeepsNameDuringGoodbye()
    {
        var display = new ConversationDisplayName(Fingerprint, "界面验证设备");
        var transport = new TestTransport();
        await using var browser = new PeerBrowser(transport) { RequiredVersion = 3 };
        browser.PeerFound += (_, peer) => display.ApplyDiscovery(peer);
        transport.Deliver(Announcement("Mac虚拟机"));
        var goodbye = new MdnsMessage { IsResponse = true };
        goodbye.Answers.Add(new PtrRecord(FeigeService.Type, Instance, 0));
        transport.Deliver(goodbye);
        Assert.Empty(browser.Peers);
        Assert.Equal("Mac虚拟机", display.Name);

        // A refreshed browser has no cached records, but the existing conversation is retained.
        var refreshedTransport = new TestTransport();
        await using var refreshed = new PeerBrowser(refreshedTransport) { RequiredVersion = 3 };
        refreshed.PeerFound += (_, peer) => display.ApplyDiscovery(peer);
        refreshedTransport.Deliver(Announcement("Mac虚拟机二号"));
        Assert.Equal("Mac虚拟机二号", display.Name);
    }

    [Fact]
    public void DiscoveryBeforeConversationCreation_CanSupplyCurrentNameInsteadOfOldMessageName()
    {
        var display = new ConversationDisplayName(Fingerprint.ToLowerInvariant(), "旧消息中的名字");
        Assert.True(display.ApplyDiscovery(Peer("Mac虚拟机")));
        Assert.Equal("Mac虚拟机", display.Name);
        Assert.Equal(Fingerprint, display.Fingerprint);
    }

    [Theory]
    [InlineData("different-device")]
    [InlineData("wrong-service")]
    [InlineData("wrong-domain")]
    [InlineData("extra-label")]
    public void UnrelatedDiscovery_DoesNotRenameConversation(string kind)
    {
        var instance = kind switch
        {
            "different-device" => FeigeService.InstanceName("peer", new string('B', 64), version: 3),
            "wrong-service" => new DnsName(Instance.FirstLabel, "_other", "_tcp", "local"),
            "wrong-domain" => new DnsName(Instance.FirstLabel, "_feige", "_tcp", "example"),
            _ => new DnsName(Instance.FirstLabel, "extra", "_feige", "_tcp", "local"),
        };
        var display = new ConversationDisplayName(Fingerprint, "原名称");
        Assert.False(display.ApplyDiscovery(Peer("别的设备", instance)));
        Assert.Equal("原名称", display.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bad\nName")]
    [InlineData("Bad\0Name")]
    public void InvalidBroadcastName_DoesNotEraseOrInjectConversationTitle(string name)
    {
        var display = new ConversationDisplayName(Fingerprint, "原名称");
        Assert.False(display.ApplyDiscovery(Peer(name)));
        Assert.Equal("原名称", display.Name);
    }

    [Fact]
    public void BroadcastName_EnforcesUtf8LimitAndNeverFallsBackToInstanceIdentifier()
    {
        var display = new ConversationDisplayName(Fingerprint, "原名称");
        Assert.False(display.ApplyDiscovery(Peer(new string('中', 43))));
        Assert.False(display.ApplyDiscovery(Peer("")));
        Assert.Equal("原名称", display.Name);
        Assert.True(display.ApplyDiscovery(Peer(new string('A', 128))));
    }

    private static DiscoveredPeer Peer(string name, DnsName? instance = null)
        => new(instance ?? Instance, name, null, new IPEndPoint(IPAddress.Parse("192.168.50.121"), 51001));

    private static MdnsMessage Announcement(string name)
    {
        var message = new MdnsMessage { IsResponse = true };
        message.Answers.Add(new PtrRecord(FeigeService.Type, Instance));
        message.Additionals.Add(new SrvRecord(Instance, 51001, Host));
        message.Additionals.Add(new TxtRecord(Instance, ["version=3", $"name={name}"]));
        message.Additionals.Add(new AddressRecord(Host, IPAddress.Parse("192.168.50.121")));
        return message;
    }

    private sealed class TestTransport : IMdnsTransport
    {
        public event EventHandler<MdnsDatagram>? DatagramReceived;
        public ValueTask SendAsync(MdnsMessage message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SendAsync(MdnsMessage message, IPEndPoint target, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Deliver(MdnsMessage message) => DatagramReceived?.Invoke(this, new(message,
            new IPEndPoint(IPAddress.Parse("192.168.50.121"), 5353)));
    }
}
