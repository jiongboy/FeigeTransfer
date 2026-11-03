using Feige.Core.Conversations;
using Xunit;

namespace Feige.Core.Tests;

public sealed class TrustedDialPlannerTests
{
    private static readonly string Low = new('1', 64), High = new('E', 64);
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalizeKeepsUniqueFullFingerprints()
    {
        string grouped = string.Join(' ', Enumerable.Range(0, 16).Select(_ => "abcd"));
        var result = TrustedDialPlanner.Normalize([grouped, grouped.ToUpperInvariant().Replace(" ", ""), "short", ""]);
        Assert.Equal([string.Concat(Enumerable.Repeat("ABCD", 16))], result);
    }

    [Fact]
    public void LargerFingerprintWaitsAfterDiscovery()
    {
        var planner = new TrustedDialPlanner();
        Assert.Equal(DateTimeOffset.MinValue, planner.DueAt(High, Low, Now));
        Assert.Equal(Now + TrustedDialPlanner.PeerDelay, planner.DueAt(Low, High, Now));
        planner.MarkUnseen(Low);
        Assert.Equal(Now.AddMinutes(1) + TrustedDialPlanner.PeerDelay, planner.DueAt(Low, High, Now.AddMinutes(1)));
    }

    [Fact]
    public void FailuresBackOffAndRejectionSuppressesUntilManualConnect()
    {
        var planner = new TrustedDialPlanner();
        planner.RecordFailure(High, rejected: false, Now);
        Assert.Equal(Now.AddSeconds(30), planner.DueAt(High, Low, Now));
        planner.RecordFailure(High, rejected: false, Now);
        Assert.Equal(Now.AddMinutes(1), planner.DueAt(High, Low, Now));
        for (int i = 0; i < 5; i++) planner.RecordFailure(High, rejected: false, Now);
        Assert.Equal(Now.AddMinutes(30), planner.DueAt(High, Low, Now));

        planner.RecordFailure(High, rejected: true, Now);
        Assert.Null(planner.DueAt(High, Low, Now));
        planner.Reset(High, clearSuppression: false);
        Assert.True(planner.IsSuppressed(High));
        planner.Reset(High, clearSuppression: true);
        Assert.Equal(DateTimeOffset.MinValue, planner.DueAt(High, Low, Now));
    }

    [Fact]
    public void DisconnectWaitsUnlessSuppressed()
    {
        var planner = new TrustedDialPlanner();
        planner.RecordDisconnect(High, Now);
        Assert.Equal(Now.AddSeconds(30), planner.DueAt(High, Low, Now));
        planner.Suppress(High);
        planner.RecordDisconnect(High, Now);
        Assert.Null(planner.DueAt(High, Low, Now));
    }
}
