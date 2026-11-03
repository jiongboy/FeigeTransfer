using Feige.Core.Conversations;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class OutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-outbox", Guid.NewGuid().ToString("N"));
    private const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private static ConversationMessage Pending(string text, DateTimeOffset at, string fingerprint = A)
        => new(Guid.NewGuid(), fingerprint, "离线设备", TransferDirection.Sent, MessageKind.Text, MessageState.Queued, at,
            Text: text, OutboxSince: at);

    [Fact]
    public void PendingMessagesSurviveRestartAndInterruptionsReturnToPending()
    {
        var history = ConversationHistory.Load(_root);
        var first = Pending("一", Now);
        var second = Pending("二", Now.AddSeconds(1));
        history.Enqueue([second]); history.Enqueue([first]);
        history.Upsert(second with { State = MessageState.Sending });
        var ordinary = new ConversationMessage(Guid.NewGuid(), A, "离线设备", TransferDirection.Sent, MessageKind.Text,
            MessageState.Sending, Now, Text: "普通");
        history.Upsert(ordinary);

        var loaded = ConversationHistory.Load(_root);
        Assert.Equal(MessageState.Queued, loaded.Messages.Single(m => m.Id == second.Id).State);
        Assert.Equal(MessageState.Failed, loaded.Messages.Single(m => m.Id == ordinary.Id).State);
        Assert.Equal(2, loaded.OutboxCount(A.ToLowerInvariant()));
        Assert.Equal(first.Id, loaded.NextOutbox(A)!.Id);

        loaded.Upsert(first with { State = MessageState.Sending });
        var interrupted = loaded.FailActive(A, "连接已断开。");
        Assert.All(interrupted, m => Assert.Equal(MessageState.Queued, m.State));
        loaded.Upsert(first with { State = MessageState.Delivered });
        Assert.Equal(second.Id, loaded.NextOutbox(A)!.Id);
    }

    [Fact]
    public void LimitCancelAndExpiry()
    {
        var history = ConversationHistory.Load(_root);
        history.Enqueue(Enumerable.Range(0, ConversationHistory.OutboxLimit).Select(i => Pending($"{i}", Now.AddSeconds(i))).ToArray());
        Assert.Throws<InvalidOperationException>(() => history.Enqueue([Pending("超出", Now)]));
        history.Enqueue([Pending("其他会话", Now, B)]);

        var head = history.NextOutbox(A)!;
        Assert.True(history.CancelOutbox(A, head.Id));
        Assert.False(history.CancelOutbox(A, head.Id));
        Assert.Equal(ConversationHistory.OutboxLimit - 1, history.OutboxCount(A));

        Assert.Empty(history.ExpireOutbox(Now + ConversationHistory.OutboxLifetime));
        var expired = history.ExpireOutbox(Now.AddSeconds(1000) + ConversationHistory.OutboxLifetime);
        Assert.Equal(ConversationHistory.OutboxLimit, expired.Count);
        Assert.All(expired, m => Assert.Equal(ConversationHistory.OutboxExpiredError, m.Error));
        Assert.Equal(0, history.OutboxCount(A));

        // A manual resend queues the same message again under a fresh timestamp.
        var resend = expired.First(m => m.Fingerprint == A) with { State = MessageState.Queued, Error = "", OutboxSince = Now.AddDays(8) };
        history.Enqueue([resend]);
        Assert.Equal(resend.Id, history.NextOutbox(A)!.Id);
    }

    [Fact]
    public void OutboxFieldIsOptionalAndOnlyForSentMessages()
    {
        var history = ConversationHistory.Load(_root);
        history.Upsert(new ConversationMessage(Guid.NewGuid(), A, "设备", TransferDirection.Sent, MessageKind.Text, MessageState.Delivered, Now, Text: "旧记录"));
        Assert.DoesNotContain("outboxSince", File.ReadAllText(Path.Combine(_root, "conversations.json")));
        var received = new ConversationMessage(Guid.NewGuid(), A, "设备", TransferDirection.Received, MessageKind.Text,
            MessageState.Received, Now, Text: "收到", OutboxSince: Now);
        Assert.Throws<InvalidDataException>(() => history.Upsert(received));
        Assert.Throws<ArgumentException>(() => history.Enqueue([Pending("一", Now), Pending("二", Now, B)]));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
