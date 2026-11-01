using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class PeerConnectionRegistryTests
{
    [Fact]
    public void SimultaneousDialCandidates_ConvergeRegardlessOfRegistrationOrder()
    {
        string a = Fingerprint(1), b = Fingerprint(2);
        var sideA = new PeerConnectionRegistry<string>(a);
        var sideB = new PeerConnectionRegistry<string>(b);
        var aInitiated = new Candidate("A-initiated", a, Enumerable.Repeat((byte)1, 32).ToArray());
        var bInitiated = new Candidate("B-initiated", b, Enumerable.Repeat((byte)2, 32).ToArray());

        sideA.Register(aInitiated.Connection, b, aInitiated.Initiator, aInitiated.Digest, direct: true);
        sideA.Register(bInitiated.Connection, b, bInitiated.Initiator, bInitiated.Digest, direct: true);
        sideB.Register(bInitiated.Connection, a, bInitiated.Initiator, bInitiated.Digest, direct: true);
        sideB.Register(aInitiated.Connection, a, aInitiated.Initiator, aInitiated.Digest, direct: true);

        Assert.True(sideA.TryGet(b, out var selectedA));
        Assert.True(sideB.TryGet(a, out var selectedB));
        Assert.Equal("A-initiated", selectedA);
        Assert.Equal(selectedA, selectedB);
    }

    [Fact]
    public void EnforcesTwentyDirectAndThirtyNineTotalConnections()
    {
        var registry = new PeerConnectionRegistry<string>(Fingerprint(0));
        for (int i = 1; i <= 20; i++)
            registry.Register($"direct-{i}", Fingerprint(i), Fingerprint(0), Digest(i), direct: true);
        Assert.Equal(20, registry.DirectPeerCount);
        Assert.Throws<InvalidOperationException>(() => registry.Register("direct-21", Fingerprint(21), Fingerprint(0), Digest(21), direct: true));

        for (int i = 22; i <= 40; i++)
            registry.Register($"group-{i}", Fingerprint(i), Fingerprint(0), Digest(i), direct: false);
        Assert.Equal(39, registry.ConnectionCount);
        Assert.Throws<InvalidOperationException>(() => registry.Register("group-41", Fingerprint(41), Fingerprint(0), Digest(41), direct: false));
    }

    [Fact]
    public void EnforcesGroupOnlyPeerLimitEvenWhenTotalCapacityRemains()
    {
        var registry = new PeerConnectionRegistry<string>(Fingerprint(0));
        for (int i = 1; i <= PeerConnectionRegistry<string>.MaximumGroupOnlyPeers; i++)
            registry.Register($"group-{i}", Fingerprint(i), Fingerprint(0), Digest(i), direct: false);

        Assert.Equal(19, registry.ConnectionCount);
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            "group-20", Fingerprint(20), Fingerprint(0), Digest(20), direct: false));
    }

    [Fact]
    public void AGroupOnlyConnection_CanBecomeDirectWithoutConsumingAnotherSlot()
    {
        string local = Fingerprint(0), peer = Fingerprint(1);
        var registry = new PeerConnectionRegistry<string>(local);
        const string group = "group-only-connection";
        registry.Register(group, peer, local, Digest(1), direct: false);
        registry.Register("duplicate-dial", peer, local, Digest(2), direct: true);

        Assert.Equal(1, registry.ConnectionCount);
        Assert.Equal(1, registry.DirectPeerCount);
        Assert.True(registry.IsDirect(peer));
    }

    private sealed record Candidate(string Connection, string Initiator, byte[] Digest);
    private static string Fingerprint(int value) => string.Concat(Enumerable.Repeat(value.ToString("X2").PadLeft(2, '0'), 32));
    private static byte[] Digest(int value) => Enumerable.Repeat(checked((byte)value), 32).ToArray();
}
