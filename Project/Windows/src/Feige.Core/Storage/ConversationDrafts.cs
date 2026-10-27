using System.Text.Json;
using Feige.Core.Conversations;

namespace Feige.Core.Storage;

public sealed record ConversationDraft(string Fingerprint, string Name, string Text);
public static class ConversationDrafts
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private sealed record DraftFile(int Version, ConversationDraft[] Drafts);
    public static IReadOnlyList<ConversationDraft> Load(string directory)
    {
        string path = Path.Combine(directory, "drafts.json");
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("草稿资料过大，原件已保留。");
        var file = JsonSerializer.Deserialize<DraftFile>(File.ReadAllBytes(path));
        if (file is null || file.Version != 1 || file.Drafts is null) throw new InvalidDataException("草稿资料格式无效，原件已保留。");
        Validate(file.Drafts);
        return file.Drafts;
    }
    public static void Save(string directory, ConversationDraft[] drafts)
    {
        Validate(drafts);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new DraftFile(1, drafts));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("草稿过大，请先保存后再安装更新。");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "drafts.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(ConversationDraft[] drafts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (drafts.Length > 1000) throw new InvalidDataException("草稿数量过多。");
        foreach (var draft in drafts)
            if (draft is null || draft.Fingerprint is not { Length: 64 } || !draft.Fingerprint.All(char.IsAsciiHexDigit) ||
                !seen.Add(ConversationHistory.NormalizeFingerprint(draft.Fingerprint)) || draft.Text is null || draft.Text.Length > 128 * 1024 ||
                string.IsNullOrWhiteSpace(draft.Name) || System.Text.Encoding.UTF8.GetByteCount(draft.Name) > 128 || draft.Name.Any(char.IsControl))
                throw new InvalidDataException("草稿资料格式无效，原件已保留。");
    }
}
