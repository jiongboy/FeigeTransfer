using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class LocalPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-preferences", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstRun_ReturnsNoPreferencesWithoutCreatingFiles()
    {
        Assert.Null(new LocalPreferencesFile(_root).Load());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Save_RoundTripsUnicodeAndDirectoryAcrossUpgradeInstances()
    {
        var settings = new LocalPreferences("我的电脑 👋", Path.Combine(_root, "接收目录"));
        new LocalPreferencesFile(_root).Save(settings);
        Assert.Equal(settings, new LocalPreferencesFile(_root).Load());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void FailedReplacement_PreservesPreviousFile()
    {
        var file = new LocalPreferencesFile(_root);
        var previous = new LocalPreferences("Old name", _root); file.Save(previous);
        Assert.Throws<InvalidDataException>(() => file.Save(new("bad\nname", _root)));
        Assert.Equal(previous, file.Load());
        var snapshot = File.ReadAllText(file.FilePath);
        Assert.Throws<InvalidDataException>(() => file.Save(new(new string('你', 48), _root)));
        Assert.Equal(snapshot, File.ReadAllText(file.FilePath));
    }

    [Fact]
    public void CorruptSettings_AreReportedWithoutOverwriting()
    {
        Directory.CreateDirectory(_root);
        var file = new LocalPreferencesFile(_root); File.WriteAllText(file.FilePath, "{}");
        Assert.Throws<InvalidDataException>(() => file.Load());
        Assert.Equal("{}", File.ReadAllText(file.FilePath));
    }

    [Fact]
    public void UnwritableDestination_FailsAndCleansTemporaryFile()
    {
        var file = new LocalPreferencesFile(_root); Directory.CreateDirectory(file.FilePath);
        var error = Assert.ThrowsAny<Exception>(() => file.Save(new("My PC", _root)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
