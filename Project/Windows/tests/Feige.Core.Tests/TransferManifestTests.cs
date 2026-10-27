using System.Security.Cryptography;
using System.Text;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>Manifest JSON and the field rules of 通信协议 §4.4.</summary>
public sealed class TransferManifestTests
{
    private static readonly byte[] SampleHash = Enumerable.Repeat((byte)0xAB, 32).ToArray();

    [Fact]
    public void Json_RoundTrips()
    {
        var manifest = new TransferManifest(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), "photo.bin", 120000, SampleHash);

        TransferManifest decoded = TransferManifest.FromJson(manifest.ToJson());

        Assert.Equal(manifest.TransferId, decoded.TransferId);
        Assert.Equal(manifest.FileName, decoded.FileName);
        Assert.Equal(manifest.Size, decoded.Size);
        Assert.Equal(Convert.ToHexString(SampleHash), Convert.ToHexString(decoded.Sha256.Span));
    }

    [Fact]
    public void Json_UsesProtocolFieldNamesAndUppercaseTransferId()
    {
        var manifest = new TransferManifest(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"), "photo.bin", 120000, SampleHash);

        string json = Encoding.UTF8.GetString(manifest.ToJson());

        Assert.Contains("\"transferID\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\"", json);
        Assert.Contains("\"fileName\":\"photo.bin\"", json);
        Assert.Contains("\"size\":120000", json);
        Assert.Contains("\"sha256\":\"" + Convert.ToBase64String(SampleHash) + "\"", json);
    }

    [Fact]
    public void Ctor_EnforcesSizeAndDigestBounds()
    {
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest("a.bin", 0, SampleHash));
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest("a.bin", -1, SampleHash));
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest("a.bin", TransferManifest.MaxFileSize + 1, SampleHash));
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest("a.bin", 1, new byte[31]));
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest("a.bin", 1, new byte[33]));

        // Boundaries are inclusive.
        _ = new TransferManifest("a.bin", 1, SampleHash);
        _ = new TransferManifest("a.bin", TransferManifest.MaxFileSize, SampleHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("dir/file.txt")]
    [InlineData("dir\\file.txt")]
    [InlineData("nul\u0000byte")]
    [InlineData("line\nbreak")]
    public void Ctor_RejectsIllegalFileNames(string fileName)
    {
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest(fileName, 1, SampleHash));
    }

    [Fact]
    public void Ctor_AcceptsNamesAtTheByteLimitAndRejectsOver()
    {
        _ = new TransferManifest(new string('a', TransferManifest.MaxFileNameBytes), 1, SampleHash);
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest(new string('a', TransferManifest.MaxFileNameBytes + 1), 1, SampleHash));

        // 86 CJK characters are 258 UTF-8 bytes, so the limit is on bytes, not characters.
        _ = new TransferManifest(new string('文', 85), 1, SampleHash);
        AssertError(ProtocolError.InvalidManifest, () => new TransferManifest(new string('文', 86), 1, SampleHash));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"transferID\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\",\"fileName\":\"a.bin\",\"size\":1}")]
    [InlineData("{\"transferID\":\"not-a-guid\",\"fileName\":\"a.bin\",\"size\":1,\"sha256\":\"q6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6s=\"}")]
    [InlineData("{\"transferID\":\"00000000-0000-0000-0000-000000000000\",\"fileName\":\"a.bin\",\"size\":1,\"sha256\":\"q6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6s=\"}")]
    [InlineData("{\"transferID\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\",\"fileName\":\"a.bin\",\"size\":\"1\",\"sha256\":\"q6urq6urq6urq6urq6urq6urq6urq6urq6urq6urq6s=\"}")]
    [InlineData("{\"transferID\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\",\"fileName\":\"a.bin\",\"size\":1,\"sha256\":\"not base64\"}")]
    [InlineData("not json")]
    public void FromJson_RejectsMalformedInput(string json)
    {
        AssertError(ProtocolError.InvalidManifest, () => TransferManifest.FromJson(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void FromJson_RejectsEmptyPayload()
    {
        AssertError(ProtocolError.InvalidManifest, () => TransferManifest.FromJson([]));
    }

    [Fact]
    public void MatchesHash_ComparesInConstantTime()
    {
        var manifest = new TransferManifest("a.bin", 1, SampleHash);

        Assert.True(manifest.MatchesHash(SampleHash));
        Assert.False(manifest.MatchesHash(new byte[32]));
        Assert.False(manifest.MatchesHash([.. SampleHash[..31], 0x00]));
    }

    [Fact]
    public async Task ForFileAsync_HashesExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"feige-manifest-{Guid.NewGuid():N}.bin");
        byte[] content = RandomNumberGenerator.GetBytes(1024);
        await File.WriteAllBytesAsync(path, content);

        try
        {
            TransferManifest manifest = await TransferManifest.ForFileAsync(path);

            Assert.Equal(Path.GetFileName(path), manifest.FileName);
            Assert.Equal(content.Length, manifest.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), Convert.ToHexString(manifest.Sha256.Span));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ForFileAsync_RejectsMissingAndEmptyFiles()
    {
        AssertError(ProtocolError.InvalidManifest, () => TransferManifest.ForFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        string empty = Path.Combine(Path.GetTempPath(), $"feige-empty-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(empty, []);
        try
        {
            AssertError(ProtocolError.InvalidManifest, () => TransferManifest.ForFileAsync(empty).GetAwaiter().GetResult());
        }
        finally
        {
            File.Delete(empty);
        }
    }

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }
}
