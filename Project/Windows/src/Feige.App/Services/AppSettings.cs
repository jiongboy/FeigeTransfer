using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Storage;
using Feige.Core.Storage;

namespace Feige.App.Services;

/// <summary>Commit settings and guide progress together before publishing any live changes.</summary>
public sealed class AppSettings
{
    private const string CompositeKey = "PreferencesV2";
    private LocalPreferences _values;
    private AppSettings(LocalPreferences values, string? loadError = null) { _values = values; LoadError = loadError; }
    public string DeviceName => _values.DeviceName;
    public string ReceiveFolder => _values.ReceiveFolder;
    public SetupProgress Setup => _values.Setup!;
    public bool AutoCheckUpdates => _values.AutoCheckUpdates;
    public bool NotificationsEnabled => _values.NotificationsEnabled;
    public string? LoadError { get; }

    public static AppSettings Load()
    {
        try
        {
            LocalPreferences? saved;
            if (!AppEnvironment.IsPackaged) saved = new LocalPreferencesFile(AppDataFolder).Load();
            else
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(CompositeKey, out object? composite))
                {
                    if (composite is not ApplicationDataCompositeValue data || !data.TryGetValue("json", out object? json) || json is not string source)
                        throw new InvalidDataException("本机设置格式无效。");
                    saved = JsonSerializer.Deserialize<LocalPreferences>(source) ?? throw new InvalidDataException("无法读取本机设置。");
                    saved.Validate();
                }
                else
                {
                    bool hasName = values.TryGetValue("DeviceName", out object? name);
                    bool hasFolder = values.TryGetValue("ReceiveFolder", out object? folder);
                    if (hasName && name is not string || hasFolder && folder is not string)
                        throw new InvalidDataException("原有本机设置格式无效。");
                    saved = hasName || hasFolder ? new(name as string ?? DefaultDeviceName(), folder as string ?? LegacyReceiveFolder()) : null;
                    saved?.Validate();
                }
            }
            bool existing = new[] { "identity.pkcs8.dpapi", "conversations.json", "history.json" }
                .Any(file => File.Exists(Path.Combine(AppDataFolder, file)));
            var progress = SetupProgress.Resolve(saved, existing);
            return new((saved ?? new(DefaultDeviceName(), existing ? LegacyReceiveFolder() : DefaultReceiveFolder())) with { Setup = progress });
        }
        catch (Exception ex)
        {
            // Keep unreadable/unknown data byte-for-byte. Recovery is explicit; never overwrite it with defaults.
            AppLog.Write("settings: read failed " + ex.GetType().Name);
            return new(new(DefaultDeviceName(), LegacyReceiveFolder(), new(Step: 2, Ended: true)),
                "无法读取已有设置，原资料已保留。请检查资料文件或恢复备份后重试。" + ErrorText.Describe(ex));
        }
    }

    private void Commit(LocalPreferences next)
    {
        if (LoadError is not null) throw new InvalidDataException(LoadError);
        next.Validate();
        if (AppEnvironment.IsPackaged)
            ApplicationData.Current.LocalSettings.Values[CompositeKey] = new ApplicationDataCompositeValue { ["json"] = JsonSerializer.Serialize(next) };
        else new LocalPreferencesFile(AppDataFolder).Save(next);
        _values = next;
    }

    public void SaveDeviceName(string name) => Commit(_values with { DeviceName = SetupValidation.DeviceName(name) });
    public void SaveReceiveFolder(string folder) => Commit(_values with { ReceiveFolder = folder });
    public void SaveAutoCheckUpdates(bool enabled) => Commit(_values with { AutoCheckUpdates = enabled });
    public void SaveNotificationsEnabled(bool enabled) => Commit(_values with { NotificationsEnabled = enabled });
    public void SaveProgress(SetupProgress progress) => Commit(_values with { Setup = progress });
    public void SaveSetup(string name, string folder, bool autoCheckUpdates)
        => Commit(_values with { DeviceName = SetupValidation.DeviceName(name), ReceiveFolder = folder,
            AutoCheckUpdates = autoCheckUpdates, Setup = Setup with { Step = 2 } });

    public static string DefaultDeviceName() => SetupValidation.DefaultDeviceName(Environment.MachineName);
    /// <summary>Folder name used under Downloads (and by the pre-1.2 Documents default).</summary>
    public const string ReceiveFolderName = "FeigeTransfer";

    private static string LegacyReceiveFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ReceiveFolderName);

    /// <summary>Use the redirected/localized Downloads known folder, never a hard-coded user path.</summary>
    public static string DefaultReceiveFolder()
    {
        nint path = 0;
        try
        {
            Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(in downloads, 0, 0, out path));
            string? folder = Marshal.PtrToStringUni(path);
            return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) ? "" : Path.Combine(folder, ReceiveFolderName);
        }
        catch { return ""; } // The guide asks for a folder instead of silently using an unrelated location.
        finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    public static string AppDataFolder => AppEnvironment.IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songli", "FeigeTransfer");
}
