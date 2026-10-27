using Feige.Core.Storage;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Transfer history persistence: JSON round-trip, corrupt-file tolerance, trimming and atomic overwrite.
/// Uses a temp directory so nothing touches the user's profile.
/// </summary>
public sealed class TransferHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feige-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyHistory()
    {
        TransferHistory history = TransferHistory.Load(_directory);

        Assert.Empty(history.Entries);
        Assert.False(File.Exists(history.FilePath));
    }

    [Fact]
    public void Add_ThenLoad_RoundTripsEveryFieldInOrder()
    {
        var first = new TransferEntry(
            TransferDirection.Received,
            "报告.pdf",
            @"C:\Users\me\Documents\报告.pdf",
            2048,
            "DESKTOP-ABC",
            new DateTimeOffset(2026, 10, 23, 16, 30, 0, TimeSpan.FromHours(8)));

        var second = new TransferEntry(
            TransferDirection.Sent,
            "photo.jpg",
            @"C:\Users\me\Pictures\photo.jpg",
            10,
            "Laptop-B",
            new DateTimeOffset(2026, 10, 23, 16, 31, 0, TimeSpan.FromHours(8)));

        TransferHistory write = TransferHistory.Load(_directory);
        write.Add(first);
        write.Add(second);

        TransferHistory read = TransferHistory.Load(_directory);

        // Newest first.
        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(second, read.Entries[0]);
        Assert.Equal(first, read.Entries[1]);
        Assert.Equal(TransferDirection.Sent, read.Entries[0].Direction);
        Assert.Equal("报告.pdf", read.Entries[1].Name);
        Assert.Equal(2048, read.Entries[1].Size);
        Assert.Equal(TimeSpan.FromHours(8), read.Entries[1].At.Offset);
    }

    [Fact]
    public void Add_TrimsToMaxEntriesKeepingNewest()
    {
        TransferHistory history = TransferHistory.Load(_directory);

        for (int i = 0; i < TransferHistory.MaxEntries + 15; i++)
        {
            history.Add(Entry($"file-{i}.bin", i));
        }

        TransferHistory read = TransferHistory.Load(_directory);

        Assert.Equal(TransferHistory.MaxEntries, read.Entries.Count);

        // The newest survives and the oldest are the ones dropped.
        Assert.Equal($"file-{TransferHistory.MaxEntries + 14}.bin", read.Entries[0].Name);
        Assert.Equal("file-15.bin", read.Entries[^1].Name);
    }

    [Fact]
    public void Clear_EmptiesFileAndReloadsEmpty()
    {
        TransferHistory history = TransferHistory.Load(_directory);
        history.Add(Entry("a.bin", 1));
        Assert.Single(history.Entries);

        history.Clear();

        Assert.Empty(history.Entries);
        Assert.Empty(TransferHistory.Load(_directory).Entries);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, TransferHistory.FileName), "{ not json at all ");

        TransferHistory history = TransferHistory.Load(_directory);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Load_SkipsRowsMissingRequiredFields()
    {
        Directory.CreateDirectory(_directory);

        // Hand-written file: one good row, then rows the loader must discard rather than fail on.
        File.WriteAllText(
            Path.Combine(_directory, TransferHistory.FileName),
            """
            {
              "version": 1,
              "entries": [
                { "direction": "sent", "name": "good.bin", "path": "C:\\good.bin", "size": 5, "peer": "P", "at": "2026-10-23T08:00:00+08:00" },
                { "direction": "sent", "name": "", "path": "C:\\bad.bin", "size": 5, "peer": "P", "at": "2026-10-23T08:00:00+08:00" },
                { "direction": "received", "name": "nopath.bin", "path": "", "size": 5, "peer": "P", "at": "2026-10-23T08:00:00+08:00" },
                { "direction": "received", "name": "negative.bin", "path": "C:\\neg.bin", "size": -1, "peer": "P", "at": "2026-10-23T08:00:00+08:00" }
              ]
            }
            """);

        TransferHistory history = TransferHistory.Load(_directory);

        Assert.Single(history.Entries);
        Assert.Equal("good.bin", history.Entries[0].Name);
        Assert.Equal(TransferDirection.Sent, history.Entries[0].Direction);
    }

    [Fact]
    public void UnknownDirection_FallsBackToReceived()
    {
        Directory.CreateDirectory(_directory);

        File.WriteAllText(
            Path.Combine(_directory, TransferHistory.FileName),
            """
            {
              "version": 1,
              "entries": [
                { "direction": "sideways", "name": "x.bin", "path": "C:\\x.bin", "size": 1, "peer": "P" }
              ]
            }
            """);

        TransferHistory history = TransferHistory.Load(_directory);

        Assert.Single(history.Entries);
        Assert.Equal(TransferDirection.Received, history.Entries[0].Direction);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        TransferHistory history = TransferHistory.Load(_directory);
        history.Add(Entry("a.bin", 1));
        history.Add(Entry("b.bin", 2));

        Assert.True(File.Exists(history.FilePath));
        Assert.False(File.Exists(history.FilePath + ".tmp"));
    }

    [Fact]
    public void SecondSave_OverwritesPreviousContent()
    {
        TransferHistory history = TransferHistory.Load(_directory);
        history.Add(Entry("old.bin", 1));
        history.Clear();
        history.Add(Entry("new.bin", 2));

        TransferHistory read = TransferHistory.Load(_directory);

        Assert.Single(read.Entries);
        Assert.Equal("new.bin", read.Entries[0].Name);
    }

    [Fact]
    public void Constructor_RejectsBlankDirectory()
    {
        Assert.Throws<ArgumentException>(() => new TransferHistory("  "));
        Assert.Throws<ArgumentNullException>(() => new TransferHistory(null!));
    }

    private static TransferEntry Entry(string name, int seed)
        => new(
            TransferDirection.Received,
            name,
            Path.Combine("C:\\incoming", name),
            seed,
            "PEER",
            new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
}
