using System.Security.Cryptography;
using Feige.Core.Protocol;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class V3StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v3-storage", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("CON.backup.txt")]
    [InlineData("LPT1.log")]
    [InlineData("COM¹.txt")]
    [InlineData("CONIN$")]
    [InlineData("a:b.txt")]
    [InlineData("file.")]
    [InlineData("file ")]
    public void Session_RejectsWindowsUnsupportedNamesBeforeCreatingDirectory(string name)
    {
        Assert.Throws<ProtocolException>(() => TransferFileSink.CreateForSession(_root, name, 0));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Session_PreservesSupportedLongNamesAndNeverOverwrites()
    {
        string name = new string('a', 150) + ".txt";
        Directory.CreateDirectory(_root);
        string existing = Path.Combine(_root, name);
        await File.WriteAllTextAsync(existing, "keep me");
        await using var sink = TransferFileSink.CreateForSession(_root, name, 0);
        string saved = await sink.CommitAsync(SHA256.HashData([]));
        Assert.Equal(Path.Combine(_root, new string('a', 150) + " (1).txt"), saved);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        Assert.Equal(0, new FileInfo(saved).Length);
        Assert.Empty(Directory.GetFiles(_root, ".feige-*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
