using System.Net;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Net;
using Feige.Core.Storage;
using Feige.Core.Updates;

namespace Feige.Core.Tests;

public sealed class UpdateInstallationTests
{
    [Fact] public void ActiveWorkPreventsInstallAndInstallAtomicallyRejectsNewWork()
    {
        var gate = new UpdateWorkGate();
        var work = gate.TryEnter()!;
        Assert.False(gate.TryBeginInstall()); Assert.False(gate.IsInstalling);
        work.Dispose(); work.Dispose(); // A repeated cleanup must not make the activity count negative.
        Assert.True(gate.TryBeginInstall()); Assert.Null(gate.TryEnter());
        gate.CancelInstall();
        using var after = gate.TryEnter(); Assert.NotNull(after); Assert.True(gate.HasActiveWork);
    }

    [Fact] public async Task InstallAndNewWorkRace_OnlyOneCanEnter()
    {
        for (int i = 0; i < 100; i++)
        {
            var gate = new UpdateWorkGate(); using var start = new ManualResetEventSlim();
            IDisposable? admitted = null; bool installing = false;
            var work = Task.Run(() => { start.Wait(); admitted = gate.TryEnter(); });
            var install = Task.Run(() => { start.Wait(); installing = gate.TryBeginInstall(); });
            start.Set(); await Task.WhenAll(work, install);
            Assert.NotEqual(admitted is not null, installing);
            admitted?.Dispose();
        }
    }

    [Fact] public async Task IncomingTextPendingPersistence_PreventsInstallUntilRealReceipt()
    {
        using var a = IdentityKey.Create(); using var b = IdentityKey.Create();
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0)); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        var accepting = listener.AcceptAsync(new LocalDevice(b, "B"), (_, _) => Task.FromResult(true), ct);
        await using var sender = await V3PeerConnector.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), new LocalDevice(a, "A"), ct);
        await using var receiver = await accepting;
        var gate = new UpdateWorkGate(); receiver.BeginIncomingWork = gate.TryEnter;
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.TextReceived = async _ => { recording.SetResult(); await commit.Task.WaitAsync(ct); };
        var runA = sender.RunAsync(() => throw new InvalidOperationException(), ct);
        var runB = receiver.RunAsync(() => throw new InvalidOperationException(), ct);
        var sending = sender.SendTextAsync("更新时也须真实落盘", cancellationToken: ct);
        await recording.Task.WaitAsync(ct);
        Assert.False(gate.TryBeginInstall()); Assert.False(sending.IsCompleted);
        commit.SetResult(); await sending;
        for (int i = 0; i < 100 && gate.HasActiveWork; i++) await Task.Delay(10, ct);
        Assert.True(gate.TryBeginInstall());
        sender.Close(); receiver.Close(); await IgnoreClosed(runA); await IgnoreClosed(runB);
    }

    [Fact] public async Task UpdateStageRejectsNewFileBeforeCreatingAnyReceiveFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.txt"); await File.WriteAllTextAsync(source, "data");
        using var a = IdentityKey.Create(); using var b = IdentityKey.Create();
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0)); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        try
        {
            var accepting = listener.AcceptAsync(new LocalDevice(b, "B"), (_, _) => Task.FromResult(true), ct);
            await using var sender = await V3PeerConnector.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), new LocalDevice(a, "A"), ct);
            await using var receiver = await accepting;
            var gate = new UpdateWorkGate(); Assert.True(gate.TryBeginInstall()); receiver.BeginIncomingWork = gate.TryEnter;
            bool accepted = false; receiver.FileReceiving = _ => { accepted = true; return Task.FromResult(true); };
            var runA = sender.RunAsync(() => throw new InvalidOperationException(), ct);
            var runB = receiver.RunAsync(() => Path.Combine(directory, "received"), ct);
            await Assert.ThrowsAsync<IOException>(() => sender.SendFileAsync(source, cancellationToken: ct));
            Assert.False(accepted); Assert.False(Directory.Exists(Path.Combine(directory, "received")));
            sender.Close(); receiver.Close(); await IgnoreClosed(runA); await IgnoreClosed(runB);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void DraftsSurviveRestartAndUnknownFormatsRemainUntouched()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var draft = new ConversationDraft(new string('A', 64), "Mac虚拟机", "未发送的草稿\n第二行");
        try
        {
            ConversationDrafts.Save(directory, [draft]);
            Assert.Equal(draft, Assert.Single(ConversationDrafts.Load(directory)));
            string path = Path.Combine(directory, "drafts.json"); byte[] original = "{\"Version\":999,\"Drafts\":[]}"u8.ToArray();
            File.WriteAllBytes(path, original);
            Assert.Throws<InvalidDataException>(() => ConversationDrafts.Load(directory));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void InvalidDraftSaveDoesNotReplaceExistingDrafts()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var valid = new ConversationDraft(new string('A', 64), "A", "保留原件");
        try
        {
            ConversationDrafts.Save(directory, [valid]);
            Assert.Throws<InvalidDataException>(() => ConversationDrafts.Save(directory, [valid, valid]));
            Assert.Equal(valid, Assert.Single(ConversationDrafts.Load(directory)));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task IgnoreClosed(Task run)
    { try { await run; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { } }
}
