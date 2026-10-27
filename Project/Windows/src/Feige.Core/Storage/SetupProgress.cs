using System.Globalization;
using System.Text;

namespace Feige.Core.Storage;

/// <summary>Durable user decisions, never cached permission or network success.</summary>
public sealed record SetupProgress(int Version = 1, int Step = 1, bool Ended = false, bool Deferred = false,
    bool LegacyReviewPending = false, bool ConnectionTipSeen = false, bool TransferTipSeen = false,
    bool NotificationInvitationSeen = false, bool BackgroundTipSeen = false)
{
    public void Validate()
    {
        if (Version != 1 || Step is < 1 or > 2 || Ended && Step != 2 || Deferred && !Ended)
            throw new InvalidDataException("首次引导资料版本不兼容或状态无效，请保留资料并检查应用版本。");
    }

    public static SetupProgress Resolve(LocalPreferences? preferences, bool hasExistingData)
        => preferences?.Setup ?? (preferences is not null || hasExistingData
            ? new(Step: 2, Ended: true, LegacyReviewPending: true) : new());
}

public static class SetupValidation
{
    public static string DeviceName(string value)
    {
        string name = value.Trim();
        if (name.Length is < 1 or > 48 || name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name) > 128)
            throw new ArgumentException("设备名称须为 1–48 个字符，最多 128 个 UTF-8 字节，且不能包含控制字符。");
        return name;
    }

    /// <summary>Do not split surrogate pairs or combining characters in a system-supplied name.</summary>
    public static string DefaultDeviceName(string value)
    {
        string clean = string.Concat(value.Where(c => !char.IsControl(c))).Trim();
        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(clean);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (result.Length + element.Length > 48 || Encoding.UTF8.GetByteCount(result.ToString()) + Encoding.UTF8.GetByteCount(element) > 128) break;
            result.Append(element);
        }
        return result.Length == 0 ? "Windows" : result.ToString();
    }

    public static void ReceiveFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("请选择有效的接收文件夹。");
        Directory.CreateDirectory(folder);
        using var probe = new FileStream(Path.Combine(folder, $".feige-write-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.Flush(flushToDisk: true);
    }
}
