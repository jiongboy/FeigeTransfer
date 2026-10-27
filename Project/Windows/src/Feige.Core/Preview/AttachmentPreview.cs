using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Feige.Core.Preview;

public enum PreviewKind { Unsupported, Image, Text, Code, Pdf, Word }
public sealed record PreviewFormat(PreviewKind Kind, string Label);
public sealed record TextPreview(string Text, bool Truncated, string EncodingName);

/// <summary>Local, bounded attachment readers. Documents are read as data, never executed.</summary>
public static class AttachmentPreview
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxCharacters = 120_000;
    private const int MaxDocumentBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico" };
    private static readonly Dictionary<string, string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".fs"] = "F#", [".c"] = "C", [".h"] = "C/C++", [".cpp"] = "C++", [".hpp"] = "C++",
        [".js"] = "JavaScript", [".jsx"] = "JSX", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".ts"] = "TypeScript", [".tsx"] = "TSX", [".py"] = "Python", [".swift"] = "Swift", [".go"] = "Go", [".rs"] = "Rust",
        [".java"] = "Java", [".kt"] = "Kotlin", [".kts"] = "Kotlin", [".rb"] = "Ruby", [".php"] = "PHP", [".dart"] = "Dart", [".lua"] = "Lua",
        [".html"] = "HTML", [".htm"] = "HTML", [".xml"] = "XML", [".xaml"] = "XAML", [".svg"] = "SVG 源码",
        [".css"] = "CSS", [".scss"] = "SCSS", [".less"] = "Less", [".vue"] = "Vue", [".svelte"] = "Svelte",
        [".json"] = "JSON", [".jsonc"] = "JSON", [".yaml"] = "YAML", [".yml"] = "YAML", [".toml"] = "TOML",
        [".ini"] = "INI", [".conf"] = "配置文件", [".config"] = "配置文件", [".env"] = "环境配置",
        [".sql"] = "SQL", [".sh"] = "Shell", [".bash"] = "Shell", [".zsh"] = "Shell", [".ps1"] = "PowerShell", [".bat"] = "Batch", [".cmd"] = "Batch",
        [".md"] = "Markdown", [".markdown"] = "Markdown", [".tex"] = "LaTeX", [".r"] = "R"
    };

    public static PreviewFormat GetFormat(string name)
    {
        string extension = Path.GetExtension(name);
        if (Images.Contains(extension)) return new(PreviewKind.Image, "图片");
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return new(PreviewKind.Pdf, "PDF");
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return new(PreviewKind.Word, "Word 文本预览");
        if (Code.TryGetValue(extension, out string? label)) return new(PreviewKind.Code, label);
        if (new[] { ".txt", ".log", ".csv", ".tsv" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return new(PreviewKind.Text, extension.TrimStart('.').ToUpperInvariant());
        if (new[] { "Dockerfile", ".gitignore", ".editorconfig", ".env", "Makefile" }.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase)) return new(PreviewKind.Code, "配置文件");
        return new(PreviewKind.Unsupported, "文件");
    }

    public static async Task<TextPreview> ReadTextAsync(string path, PreviewKind kind, CancellationToken ct = default)
    {
        if (kind == PreviewKind.Word) return await Task.Run(() => ReadWord(path, ct), ct).ConfigureAwait(false);
        if (kind is not (PreviewKind.Text or PreviewKind.Code)) throw new NotSupportedException("该格式不支持文本预览。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        byte[] bytes = new byte[(int)Math.Min(file.Length, MaxTextBytes)];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await file.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        bool truncated = file.Length > count;
        (string text, string encoding) = Decode(bytes.AsSpan(0, count), !truncated);
        if (text.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t' or '\f')))
            throw new InvalidDataException("文件包含二进制内容，无法作为文本预览。");
        return Limit(text.Replace("\r\n", "\n").Replace('\r', '\n'), truncated, encoding);
    }

    private static (string, string) Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        int skip = 0;
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        try { return (DecodeWith(encoding, bytes[skip..], flush), encoding.WebName); }
        catch (DecoderFallbackException) when (skip == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (DecodeWith(encoding, bytes, flush), encoding.WebName);
        }
    }

    private static string DecodeWith(Encoding encoding, ReadOnlySpan<byte> bytes, bool flush)
    {
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        int count = encoding.GetDecoder().GetChars(bytes, chars, flush);
        return new string(chars, 0, count);
    }

    private static TextPreview ReadWord(string path, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("文档较大，请使用“打开文件”查看。");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10_000) throw new InvalidDataException("文档内容过多，请使用“打开文件”查看。");
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("文件不是有效的 Word 文档。");
        if (entry.Length > MaxDocumentBytes) throw new InvalidDataException("文档正文较大，请使用“打开文件”查看。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxDocumentBytes });
        const string word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string strictWord = "http://purl.oclc.org/ooxml/wordprocessingml/main";
        var text = new StringBuilder();
        bool inText = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI is word or strictWord)
            {
                if (reader.LocalName == "t") inText = !reader.IsEmptyElement;
                else if (reader.LocalName == "tab") text.Append('\t');
                else if (reader.LocalName is "br" or "cr") text.Append('\n');
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI is word or strictWord)
            {
                if (reader.LocalName == "t") inText = false;
                else if (reader.LocalName == "p") text.Append('\n');
                else if (reader.LocalName == "tc") text.Append('\t');
            }
            else if (inText && reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) text.Append(reader.Value);
            if (text.Length > MaxCharacters) return Limit(text.ToString(), true, "DOCX");
        }
        return Limit(text.ToString().TrimEnd(), false, "DOCX");
    }

    private static TextPreview Limit(string text, bool truncated, string encoding)
    {
        int length = Math.Min(text.Length, MaxCharacters);
        // Keep surrogate pairs intact and bound UI layout work for files with many short lines.
        int lines = 0;
        for (int i = 0; i < length; i++) if (text[i] == '\n' && ++lines >= 3000) { length = i; break; }
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return new(text[..length], truncated || length < text.Length, encoding);
    }
}
