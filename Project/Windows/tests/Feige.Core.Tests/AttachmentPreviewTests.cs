using System.IO.Compression;
using System.Text;
using Feige.Core.Preview;

namespace Feige.Core.Tests;

public sealed class AttachmentPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-preview", Guid.NewGuid().ToString("N"));
    private string FilePath(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }

    [Theory]
    [InlineData("image.JPG", PreviewKind.Image)]
    [InlineData("source.tsx", PreviewKind.Code)]
    [InlineData("说明.md", PreviewKind.Code)]
    [InlineData("config.JSON", PreviewKind.Code)]
    [InlineData(".env", PreviewKind.Code)]
    [InlineData("Dockerfile", PreviewKind.Code)]
    [InlineData("报告.PDF", PreviewKind.Pdf)]
    [InlineData("报告.docx", PreviewKind.Word)]
    [InlineData("数据.csv", PreviewKind.Text)]
    [InlineData("legacy.doc", PreviewKind.Unsupported)]
    [InlineData("archive.zip", PreviewKind.Unsupported)]
    [InlineData("page.svg", PreviewKind.Code)]
    public void ClassifiesCommonFormatsWithoutExecutingContent(string name, PreviewKind kind)
        => Assert.Equal(kind, AttachmentPreview.GetFormat(name).Kind);

    [Fact]
    public async Task PreservesCodeWhitespaceUnicodeAndNewlines()
    {
        string path = FilePath("example.cs");
        await File.WriteAllTextAsync(path, "// 中文 👋\r\n\treturn \"<script>不执行</script>\";\r\n", new UTF8Encoding(false));
        var result = await AttachmentPreview.ReadTextAsync(path, PreviewKind.Code);
        Assert.Equal("// 中文 👋\n\treturn \"<script>不执行</script>\";\n", result.Text);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsUtf16WithBom(bool bigEndian)
    {
        string path = FilePath("utf16.txt");
        await File.WriteAllTextAsync(path, "中文\tUnicode", new UnicodeEncoding(bigEndian, true));
        Assert.Equal("中文\tUnicode", (await AttachmentPreview.ReadTextAsync(path, PreviewKind.Text)).Text);
    }

    [Fact]
    public async Task ReadsLegacyChineseEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = FilePath("gbk.txt");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding("GB18030").GetBytes("中文说明与代码预览"));
        Assert.Equal("中文说明与代码预览", (await AttachmentPreview.ReadTextAsync(path, PreviewKind.Text)).Text);
    }

    [Fact]
    public async Task BoundsLargeFilesAndDoesNotCutUnicodePairs()
    {
        string path = FilePath("large.txt");
        await File.WriteAllTextAsync(path, new string('a', AttachmentPreview.MaxCharacters - 1) + "👋" + new string('b', AttachmentPreview.MaxTextBytes));
        var result = await AttachmentPreview.ReadTextAsync(path, PreviewKind.Text);
        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= AttachmentPreview.MaxCharacters);
        Assert.False(char.IsHighSurrogate(result.Text[^1]));
    }

    [Fact]
    public async Task BoundsLineCountAndRejectsBinaryDisguisedAsText()
    {
        string path = FilePath("many.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("x\n", 5000)));
        var result = await AttachmentPreview.ReadTextAsync(path, PreviewKind.Text);
        Assert.True(result.Truncated);
        Assert.True(result.Text.Count(ch => ch == '\n') < 3000);
        await File.WriteAllBytesAsync(path, [65, 0, 66, 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AttachmentPreview.ReadTextAsync(path, PreviewKind.Text));
    }

    private string Word(string xml)
    {
        string path = FilePath(Guid.NewGuid() + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        writer.Write(xml);
        return path;
    }

    [Fact]
    public async Task WordPreservesParagraphsTableTextAndTabs()
    {
        string path = Word("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t xml:space="preserve"> 标题 </w:t><w:tab/><w:t>中文</w:t><w:br/><w:t>第二行</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>单元格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>""");
        var result = await AttachmentPreview.ReadTextAsync(path, PreviewKind.Word);
        Assert.Equal(" 标题 \t中文\n第二行\n单元格", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task WordRejectsDtdAndMissingBodyWithoutReadingExternalFiles()
    {
        string path = Word("""<!DOCTYPE root [<!ENTITY secret SYSTEM "file:///does-not-exist">]><root>&secret;</root>""");
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => AttachmentPreview.ReadTextAsync(path, PreviewKind.Word));
        string empty = FilePath("empty.docx");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        await Assert.ThrowsAsync<InvalidDataException>(() => AttachmentPreview.ReadTextAsync(empty, PreviewKind.Word));
    }

    [Fact]
    public async Task MissingFilesAndCancellationAreReported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => AttachmentPreview.ReadTextAsync(FilePath("missing.txt"), PreviewKind.Text));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttachmentPreview.ReadTextAsync(FilePath("missing.docx"), PreviewKind.Word, cancellation.Token));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
