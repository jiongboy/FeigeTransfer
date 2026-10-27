using System.Security.Cryptography;
using Feige.Core.Protocol;
using Feige.Core.Storage;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Temp-file streaming, verification-before-rename and no-overwrite behaviour (通信协议 §5.1, 安全要求 6).
/// </summary>
public sealed class TransferFileSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-sink-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Commit_WritesFileOnlyAfterVerification()
    {
        byte[] content = RandomNumberGenerator.GetBytes(200_000);
        byte[] digest = SHA256.HashData(content);

        await using TransferFileSink sink = TransferFileSink.Create(_root, "photo.bin", content.Length);
        foreach (byte[] chunk in Chunk(content, TransferSender.ChunkSize))
        {
            await sink.WriteAsync(chunk);
        }

        // The final name must not exist yet, only the temp file.
        Assert.False(File.Exists(Path.Combine(_root, "photo.bin")));
        Assert.Single(Directory.GetFiles(_root, ".feige-*.tmp"));

        string path = await sink.CommitAsync(digest);

        Assert.Equal(Path.Combine(_root, "photo.bin"), path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, ".feige-*.tmp"));
    }

    [Fact]
    public async Task Commit_RejectsChecksumMismatchAndKeepsNoFile()
    {
        byte[] content = RandomNumberGenerator.GetBytes(1024);

        await using (TransferFileSink sink = TransferFileSink.Create(_root, "photo.bin", content.Length))
        {
            await sink.WriteAsync(content);
            AssertError(ProtocolError.ChecksumMismatch, () => sink.CommitAsync(new byte[32]).GetAwaiter().GetResult());
        }

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Commit_RejectsIncompleteTransfer()
    {
        await using (TransferFileSink sink = TransferFileSink.Create(_root, "photo.bin", 100))
        {
            await sink.WriteAsync(new byte[10]);
            AssertError(ProtocolError.SizeMismatch, () => sink.CommitAsync(new byte[32]).GetAwaiter().GetResult());
        }

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Write_RejectsDataBeyondManifestSize()
    {
        await using TransferFileSink sink = TransferFileSink.Create(_root, "photo.bin", 10);
        await sink.WriteAsync(new byte[10]);

        AssertError(ProtocolError.SizeMismatch, () => sink.WriteAsync(new byte[1]).GetAwaiter().GetResult());
        Assert.Equal(10, sink.Written);
    }

    [Fact]
    public async Task Commit_NeverOverwritesAnExistingFile()
    {
        Directory.CreateDirectory(_root);
        string existing = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(existing, "original");

        byte[] content = "incoming"u8.ToArray();
        await using TransferFileSink sink = TransferFileSink.Create(_root, "report.txt", content.Length);
        await sink.WriteAsync(content);
        string path = await sink.CommitAsync(SHA256.HashData(content));

        Assert.Equal(Path.Combine(_root, "report (1).txt"), path);
        Assert.Equal("original", await File.ReadAllTextAsync(existing));
        Assert.Equal("incoming", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Commit_UsesNextFreeSuffixForEachCollision()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "a (1).txt"), "x");

        byte[] content = "y"u8.ToArray();
        await using TransferFileSink sink = TransferFileSink.Create(_root, "a.txt", content.Length);
        await sink.WriteAsync(content);
        string path = await sink.CommitAsync(SHA256.HashData(content));

        Assert.Equal(Path.Combine(_root, "a (2).txt"), path);
    }

    [Fact]
    public async Task Dispose_WithoutCommit_DeletesTempFile()
    {
        await using (TransferFileSink sink = TransferFileSink.Create(_root, "photo.bin", 10))
        {
            await sink.WriteAsync(new byte[5]);
        }

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Create_SanitizesWindowsIllegalNames()
    {
        byte[] content = "z"u8.ToArray();
        await using TransferFileSink sink = TransferFileSink.Create(_root, "con:weird?.txt", content.Length);
        await sink.WriteAsync(content);
        string path = await sink.CommitAsync(SHA256.HashData(content));

        Assert.Equal("con_weird_.txt", Path.GetFileName(path));
        Assert.Equal("z", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Create_RejectsIllegalProtocolNames()
    {
        await Task.CompletedTask;
        AssertError(ProtocolError.InvalidManifest, () => TransferFileSink.Create(_root, "../escape.bin", 1));
        AssertError(ProtocolError.InvalidManifest, () => TransferFileSink.Create(_root, "a/b.bin", 1));
        AssertError(ProtocolError.InvalidManifest, () => TransferFileSink.Create(_root, "a.bin", 0));
    }

    [Fact]
    public void SafeFileName_KeepsValidNamesAndFixesReservedOnes()
    {
        Assert.Equal("photo.jpg", SafeFileName.MakeWritable("photo.jpg"));
        Assert.Equal("_CON.txt", SafeFileName.MakeWritable("CON.txt"));
        Assert.Equal("_nul", SafeFileName.MakeWritable("nul"));
        Assert.Equal("trailing", SafeFileName.MakeWritable("trailing.")); // Windows strips trailing dots
        Assert.Equal("_", SafeFileName.MakeWritable("   "));
        Assert.Equal("report (1).pdf", SafeFileName.WithSuffix("report.pdf", 1));
        Assert.Equal("report.pdf", SafeFileName.WithSuffix("report.pdf", 0));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    private static IEnumerable<byte[]> Chunk(byte[] data, int size)
    {
        for (int offset = 0; offset < data.Length; offset += size)
        {
            yield return data[offset..Math.Min(offset + size, data.Length)];
        }
    }

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }
}
