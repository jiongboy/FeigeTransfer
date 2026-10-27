using System.Text.Json;
using Feige.Core.Conversations;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class ConversationHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-conversations", Guid.NewGuid().ToString("N"));
    private static ConversationMessage Text(string fingerprint = "", TransferDirection direction = TransferDirection.Received,
        MessageState state = MessageState.Received, Guid? id = null) => new(id ?? Guid.NewGuid(), fingerprint.Length == 0 ? new string('A', 64) : fingerprint,
            "Same name", direction, MessageKind.Text, state, DateTimeOffset.UtcNow, Text: "  你好 👋\n保留空格  ");

    [Fact]
    public void RoundTrip_PreservesUnicodeAndSeparatesSameNamePeersAndDirections()
    {
        var history = ConversationHistory.Load(_root); var id = Guid.NewGuid();
        history.Upsert(Text(id: id));
        history.Upsert(Text(new string('B', 64), id: id));
        history.Upsert(Text(direction: TransferDirection.Sent, state: MessageState.Delivered, id: id));
        var loaded = ConversationHistory.Load(_root);
        Assert.Equal(3, loaded.Messages.Count);
        Assert.All(loaded.Messages, m => Assert.Equal("  你好 👋\n保留空格  ", m.Text));
        Assert.Equal(history.Messages, loaded.Messages);
    }

    [Fact]
    public void DeclinedState_IsPersistedForEitherDirectionWhenValid()
    {
        var history = ConversationHistory.Load(_root);
        var sent = Text(direction: TransferDirection.Sent, state: MessageState.Declined) with { Kind = MessageKind.File, FileName = "sent.bin", Text = "" };
        history.Upsert(sent);
        var rejectedIncoming = Text(direction: TransferDirection.Received, state: MessageState.Declined) with { Kind = MessageKind.File, FileName = "incoming.bin", Text = "", Error = "已拒绝接收" };
        history.Upsert(rejectedIncoming);
        var loaded = ConversationHistory.Load(_root).Messages;
        Assert.Equal(MessageState.Declined, loaded.Single(m => m.Id == sent.Id).State);
        Assert.Equal(MessageState.Declined, loaded.Single(m => m.Id == rejectedIncoming.Id).State);
    }

    [Fact]
    public void Retry_UpdatesOriginalUuidAndNormalizesFingerprint()
    {
        var history = ConversationHistory.Load(_root);
        var message = Text(string.Join(' ', Enumerable.Repeat("aaaa", 16)), TransferDirection.Sent, MessageState.Failed);
        history.Upsert(message); history.Upsert(message with { State = MessageState.Delivered });
        var loaded = Assert.Single(ConversationHistory.Load(_root).Messages);
        Assert.Equal(new string('A', 64), loaded.Fingerprint);
        Assert.Equal(message.Id, loaded.Id); Assert.Equal(MessageState.Delivered, loaded.State);
    }

    [Theory]
    [InlineData(MessageState.Queued)]
    [InlineData(MessageState.Sending)]
    public void Restart_MarksUnfinishedItemsFailed(MessageState state)
    {
        var history = ConversationHistory.Load(_root);
        history.Upsert(Text(direction: TransferDirection.Sent, state: state));
        var message = Assert.Single(ConversationHistory.Load(_root).Messages);
        Assert.Equal(MessageState.Failed, message.State); Assert.NotEmpty(message.Error);
    }

    [Fact]
    public void Retention_KeepsAllActiveItemsAndLatest1000Completed()
    {
        Directory.CreateDirectory(_root);
        var first = Text() with { At = DateTimeOffset.UtcNow.AddDays(-2) };
        var active = Text(direction: TransferDirection.Sent, state: MessageState.Sending) with { At = first.At.AddDays(-1) };
        var rows = Enumerable.Range(0, 1001).Select(i => first with { Id = Guid.NewGuid(), At = first.At.AddSeconds(i) }).ToList();
        rows.Add(active);
        File.WriteAllText(Path.Combine(_root, "conversations.json"), JsonSerializer.Serialize(new { version = 1, messages = rows }, V3Wire.Json));
        // Loading marks the old active item failed and applies the completed retention window.
        var history = ConversationHistory.Load(_root); Assert.Equal(1000, history.Messages.Count);
        var newActive = active with { Id = Guid.NewGuid() }; history.Upsert(newActive);
        Assert.Equal(1001, history.Messages.Count); Assert.Contains(history.Messages, m => m.Id == newActive.Id && m.IsActive);
    }

    [Fact]
    public void FailedWrite_PreservesPreviousSnapshotAndDoesNotClaimReceived()
    {
        var history = ConversationHistory.Load(_root); history.Upsert(Text());
        var original = Assert.Single(history.Messages);
        File.Move(Path.Combine(_root, "conversations.json"), Path.Combine(_root, "backup.json"));
        Directory.CreateDirectory(Path.Combine(_root, "conversations.json"));
        var error = Assert.ThrowsAny<Exception>(() => history.Upsert(Text()));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(original, Assert.Single(history.Messages));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":2,\"messages\":[]}")]
    [InlineData("{\"version\":1,\"messages\":[{}]}")]
    public void CorruptHistory_IsReportedWithoutOverwritingExistingFile(string json)
    {
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "conversations.json"); File.WriteAllText(path, json);
        Assert.ThrowsAny<Exception>(() => ConversationHistory.Load(_root)); Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void Snapshot_CannotBeMutatedByCaller()
    {
        var history = ConversationHistory.Load(_root); history.Upsert(Text());
        var copy = history.Messages.ToArray(); copy[0] = Text(new string('B', 64));
        Assert.Equal(new string('A', 64), Assert.Single(history.Messages).Fingerprint);
    }

    [Fact]
    public void Disconnect_FailsOnlyActiveItemsOfThisPeerAndPreservesDeliveredMessages()
    {
        var history = ConversationHistory.Load(_root);
        var queued = Text(direction: TransferDirection.Sent, state: MessageState.Queued);
        var delivered = Text(direction: TransferDirection.Sent, state: MessageState.Delivered);
        var other = Text(new string('B', 64), TransferDirection.Sent, MessageState.Sending);
        history.Upsert(queued); history.Upsert(delivered); history.Upsert(other);
        var failed = Assert.Single(history.FailActive(new string('A', 64), "断线"));
        Assert.Equal(queued.Id, failed.Id); Assert.Equal(MessageState.Failed, failed.State);
        Assert.Equal(MessageState.Delivered, history.Messages.Single(m => m.Id == delivered.Id).State);
        Assert.True(history.Messages.Single(m => m.Id == other.Id).IsActive);
        Assert.Empty(history.FailActive(new string('A', 64), "再次断线"));
        Assert.Equal(MessageState.Failed, ConversationHistory.Load(_root).Messages.Single(m => m.Id == queued.Id).State);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
