using System.Text;
using System.Text.Json;

namespace Feige.Core.Storage;

public sealed record LocalPreferences(string DeviceName, string ReceiveFolder, SetupProgress? Setup = null,
    bool AutoCheckUpdates = true, bool NotificationsEnabled = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName != DeviceName.Trim() || DeviceName.Length > 48 ||
            DeviceName.Any(char.IsControl) || Encoding.UTF8.GetByteCount(DeviceName) > 128 || string.IsNullOrWhiteSpace(ReceiveFolder))
            throw new InvalidDataException("本机设置无效。");
        Setup?.Validate();
    }
}

/// <summary>Per-user settings for EXE installs. Commit before changing the live settings object.</summary>
public sealed class LocalPreferencesFile(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalPreferences? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var preferences = JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllBytes(FilePath), Json)
            ?? throw new InvalidDataException("无法读取本机设置。");
        preferences.Validate();
        return preferences;
    }

    public void Save(LocalPreferences preferences)
    {
        preferences.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
