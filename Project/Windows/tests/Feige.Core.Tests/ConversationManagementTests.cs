using System.Text.Json;
using Feige.Core.Conversations;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class ConversationManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-conversation-management", Guid.NewGuid().ToString("N"));
    private const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static ConversationMessage Message(string fingerprint = A, TransferDirection direction = TransferDirection.Received,
        MessageState state = MessageState.Received) => new(Guid.NewGuid(), fingerprint, "同名设备", direction, MessageKind.Text, state, DateTimeOffset.UtcNow, Text: "保留记录");

    [Fact]
    public void HidingPersistsAndPreservesMessages()
    {
        var history = ConversationHistory.Load(_root); var message = Message(); history.Upsert(message);
        history.SetHidden(A.ToLowerInvariant(), true);
        var loaded = ConversationHistory.Load(_root);
        Assert.True(loaded.IsHidden(A)); Assert.Equal(message, Assert.Single(loaded.Messages));
        loaded.SetHidden(A, false);
        Assert.False(ConversationHistory.Load(_root).IsHidden(A));
    }

    [Fact]
    public void OnlyNewIncomingMessagesRevealHiddenConversations()
    {
        var history = ConversationHistory.Load(_root); var received = Message(); history.Upsert(received); history.SetHidden(A, true);
        history.Upsert(received with { Text = "重传同一消息" }); Assert.True(history.IsHidden(A));
        var sent = Message(direction: TransferDirection.Sent, state: MessageState.Delivered);
        history.Upsert(sent); Assert.True(history.IsHidden(A));
        history.Upsert(Message()); Assert.False(history.IsHidden(A));
        Assert.False(ConversationHistory.Load(_root).IsHidden(A));
    }

    [Fact]
    public void RestoreAllPreservesMessagesAcrossReload()
    {
        var history = ConversationHistory.Load(_root); history.Upsert(Message()); history.Upsert(Message(B));
        history.SetHidden(A, true); history.SetHidden(B, true); history.RestoreAllHidden();
        var loaded = ConversationHistory.Load(_root);
        Assert.Empty(loaded.HiddenFingerprints); Assert.Equal(2, loaded.Messages.Count);
    }

    [Fact]
    public void DeleteScopesByFingerprintAcrossDirectionsAndKeepsDiskFiles()
    {
        var history = ConversationHistory.Load(_root);
        string path = Path.Combine(_root, "keep.txt"); Directory.CreateDirectory(_root); File.WriteAllText(path, "keep");
        history.Upsert(Message() with { Kind = MessageKind.File, Text = "", FileName = "keep.txt", Path = path, Size = 4 });
        history.Upsert(Message(direction: TransferDirection.Sent, state: MessageState.Delivered));
        var other = Message(B); history.Upsert(other); history.SetHidden(A, true);
        history.DeleteConversation(A);
        var loaded = ConversationHistory.Load(_root);
        Assert.Equal(other, Assert.Single(loaded.Messages)); Assert.False(loaded.IsHidden(A)); Assert.Equal("keep", File.ReadAllText(path));
        var fresh = Message(); loaded.Upsert(fresh);
        Assert.Contains(fresh, ConversationHistory.Load(_root).Messages);
    }

    [Theory]
    [InlineData(MessageState.Queued)]
    [InlineData(MessageState.Sending)]
    public void ActiveTransferPreventsDeletion(MessageState state)
    {
        var history = ConversationHistory.Load(_root); var active = Message(direction: TransferDirection.Sent, state: state); history.Upsert(active);
        Assert.Throws<InvalidOperationException>(() => history.DeleteConversation(A));
        Assert.Equal(active, Assert.Single(history.Messages));
    }

    [Fact]
    public void FailedDeleteDoesNotMutateMessagesOrHiddenState()
    {
        var history = ConversationHistory.Load(_root); var message = Message(); history.Upsert(message); history.SetHidden(A, true);
        string path = Path.Combine(_root, "conversations.json"); File.Move(path, path + ".backup"); Directory.CreateDirectory(path);
        Assert.True(Assert.ThrowsAny<Exception>(() => history.DeleteConversation(A)) is IOException or UnauthorizedAccessException);
        Assert.True(history.IsHidden(A)); Assert.Equal(message, Assert.Single(history.Messages));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void FailedHideDoesNotChangeVisibleState()
    {
        var history = ConversationHistory.Load(_root); history.Upsert(Message());
        string path = Path.Combine(_root, "conversations.json"); File.Move(path, path + ".backup"); Directory.CreateDirectory(path);
        Assert.True(Assert.ThrowsAny<Exception>(() => history.SetHidden(A, true)) is IOException or UnauthorizedAccessException); Assert.False(history.IsHidden(A));
    }

    [Fact]
    public void ReadsOldHistoryAndRejectsInvalidHiddenMetadataWithoutOverwriting()
    {
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "conversations.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, messages = new[] { Message() } }, V3Wire.Json));
        Assert.Empty(ConversationHistory.Load(_root).HiddenFingerprints);
        File.WriteAllText(path, "{\"version\":1,\"messages\":[],\"hiddenFingerprints\":[\"bad\"]}");
        string snapshot = File.ReadAllText(path);
        Assert.Throws<InvalidDataException>(() => ConversationHistory.Load(_root)); Assert.Equal(snapshot, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void InvalidIdentityCannotHideOrDeleteRecords(string fingerprint)
    {
        var history = ConversationHistory.Load(_root); history.Upsert(Message());
        Assert.Throws<InvalidDataException>(() => history.SetHidden(fingerprint, true));
        Assert.Throws<InvalidDataException>(() => history.DeleteConversation(fingerprint)); Assert.Single(history.Messages);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
