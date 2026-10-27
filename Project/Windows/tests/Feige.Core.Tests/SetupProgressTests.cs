using System.Text;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class SetupProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-setup", Guid.NewGuid().ToString("N"));
    [Fact] public void FreshInstall_HasNoImplicitCompletion()
    {
        Assert.Equal(new SetupProgress(), SetupProgress.Resolve(null, false));
        Assert.False(Directory.Exists(_root));
    }
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void LegacyEvidence_EntersMainWithoutResettingData(bool preferences, bool historyOrIdentity)
    {
        var progress = SetupProgress.Resolve(preferences ? new("Old name", _root) : null, historyOrIdentity);
        Assert.True(progress.Ended); Assert.True(progress.LegacyReviewPending); Assert.Equal(2, progress.Step);
    }
    [Fact] public void ExplicitIncompleteState_WinsOverExistingIdentityAndRecords()
    {
        var progress = new SetupProgress(Step: 2);
        Assert.Equal(progress, SetupProgress.Resolve(new("Saved PC", _root, progress), true));
    }
    [Fact] public void SavedStep_RestartsAtPreparationWithSameNameFolderAndUpdateChoice()
    {
        var file = new LocalPreferencesFile(_root);
        var saved = new LocalPreferences("我的电脑", Path.Combine(_root, "接收"), new(Step: 2), AutoCheckUpdates: false);
        file.Save(saved);
        Assert.Equal(saved, new LocalPreferencesFile(_root).Load());
        Assert.False(file.Load()!.Setup!.Ended);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EndedAndDeferred_AreIndependentFromRuntimeReadiness(bool deferred)
    {
        var file = new LocalPreferencesFile(_root);
        var progress = new SetupProgress(Step: 2, Ended: true, Deferred: deferred, ConnectionTipSeen: true,
            TransferTipSeen: true, NotificationInvitationSeen: true, BackgroundTipSeen: true);
        file.Save(new("PC", _root, progress, NotificationsEnabled: true));
        Assert.Equal(progress, file.Load()!.Setup); Assert.True(file.Load()!.NotificationsEnabled);
    }
    [Theory]
    [InlineData(2, 2, true, false)] [InlineData(1, 0, false, false)]
    [InlineData(1, 1, true, false)] [InlineData(1, 2, false, true)]
    public void UnknownOrInconsistentState_RefusesOverwrite(int version, int step, bool ended, bool deferred)
    {
        var file = new LocalPreferencesFile(_root); file.Save(new("Old", _root));
        string before = File.ReadAllText(file.FilePath);
        Assert.Throws<InvalidDataException>(() => file.Save(new("New", _root, new(version, step, ended, deferred))));
        Assert.Equal(before, File.ReadAllText(file.FilePath));
    }
    [Fact] public void OldJsonWithoutNewFields_RetainsConfiguration()
    {
        Directory.CreateDirectory(_root); var file = new LocalPreferencesFile(_root);
        File.WriteAllText(file.FilePath, "{\"deviceName\":\"原有名称\",\"receiveFolder\":\"D:\\\\Saved\"}");
        var old = file.Load()!;
        Assert.Equal("原有名称", old.DeviceName); Assert.Equal("D:\\Saved", old.ReceiveFolder);
        Assert.Null(old.Setup); Assert.True(old.AutoCheckUpdates); Assert.False(old.NotificationsEnabled);
    }
    [Fact] public void UnknownSavedVersion_IsReportedWithoutMutation()
    {
        Directory.CreateDirectory(_root); var file = new LocalPreferencesFile(_root);
        const string json = "{\"deviceName\":\"PC\",\"receiveFolder\":\"D:\\\\Saved\",\"setup\":{\"version\":77}}";
        File.WriteAllText(file.FilePath, json);
        Assert.Throws<InvalidDataException>(() => file.Load()); Assert.Equal(json, File.ReadAllText(file.FilePath));
    }
    [Fact] public void FailedCommit_DoesNotLeaveCompletedProgressOrTemporaryFiles()
    {
        var file = new LocalPreferencesFile(_root); Directory.CreateDirectory(file.FilePath);
        Assert.ThrowsAny<Exception>(() => file.Save(new("PC", _root, new(Step: 2, Ended: true))));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Theory]
    [InlineData("  我的电脑 👋  ", "我的电脑 👋")]
    [InlineData("\t\r\n", "Windows")]
    [InlineData("a\u0001b", "ab")]
    public void SystemName_NormalizesWhitespaceAndControls(string name, string expected)
        => Assert.Equal(expected, SetupValidation.DefaultDeviceName(name));
    [Fact] public void SystemName_DoesNotSplitEmojiOrCombiningCharacters()
    {
        string name = SetupValidation.DefaultDeviceName(new string('a', 47) + "👩‍💻");
        Assert.Equal(new string('a', 47), name);
        name = SetupValidation.DefaultDeviceName(string.Concat(Enumerable.Repeat("你👋e\u0301", 40)));
        Assert.True(name.Length <= 48); Assert.True(Encoding.UTF8.GetByteCount(name) <= 128);
        Assert.False(char.IsHighSurrogate(name[^1])); Assert.Equal(name, SetupValidation.DeviceName(name));
    }
    [Theory] [InlineData("")] [InlineData("\t ")] [InlineData("bad\nname")]
    public void InvalidEnteredName_RejectsWithoutSilentReplacement(string name)
        => Assert.Throws<ArgumentException>(() => SetupValidation.DeviceName(name));
    [Fact] public void EnteredName_EnforcesBothProtocolLimits()
    {
        Assert.Equal("设备", SetupValidation.DeviceName(" 设备 "));
        Assert.Throws<ArgumentException>(() => SetupValidation.DeviceName(new string('a', 49)));
        Assert.Throws<ArgumentException>(() => SetupValidation.DeviceName(new string('你', 48)));
    }
    [Fact] public void FolderProbe_CreatesSelectedFolderAndPreservesExistingFiles()
    {
        Directory.CreateDirectory(_root); string userFile = Path.Combine(_root, "user.txt"); File.WriteAllText(userFile, "keep");
        SetupValidation.ReceiveFolder(_root);
        Assert.Equal("keep", File.ReadAllText(userFile)); Assert.Single(Directory.GetFiles(_root));
        SetupValidation.ReceiveFolder(Path.Combine(_root, "新目录")); Assert.True(Directory.Exists(Path.Combine(_root, "新目录")));
        Assert.Empty(Directory.GetFiles(_root, ".feige-write-*", SearchOption.AllDirectories));
    }
    [Theory] [InlineData("")] [InlineData("relative/folder")]
    public void InvalidFolder_DoesNotCreateUnexpectedLocations(string folder)
        => Assert.Throws<ArgumentException>(() => SetupValidation.ReceiveFolder(folder));
    [Fact] public void FolderIsAFile_ReportsFailureWithoutOverwriting()
    {
        Directory.CreateDirectory(_root); string file = Path.Combine(_root, "file"); File.WriteAllText(file, "keep");
        Assert.ThrowsAny<Exception>(() => SetupValidation.ReceiveFolder(file)); Assert.Equal("keep", File.ReadAllText(file));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
