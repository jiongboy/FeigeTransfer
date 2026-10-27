using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feige.Core.Updates;

public sealed record UpdateScopeState(DateTimeOffset? LastAttempt = null, long HighestSeen = 0, long SkippedBuild = 0,
    string LastResult = "尚未检查更新", long LastPromptBuild = 0, DateTimeOffset? LastPromptAt = null)
{
    public bool IsDue(DateTimeOffset now) => LastAttempt is null || now - LastAttempt >= TimeSpan.FromHours(24);
    public bool ShouldPrompt(long build, DateTimeOffset now) => build != SkippedBuild &&
        (LastPromptBuild != build || LastPromptAt is null || now - LastPromptAt >= TimeSpan.FromHours(24));
}

/// <summary>Updater-owned state only; a failed commit never changes the live snapshot.</summary>
public sealed class UpdateStateFile
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, UpdateScopeState> _scopes;
    private sealed record State([property: JsonRequired] int Version,
        [property: JsonRequired] Dictionary<string, UpdateScopeState> Scopes);
    public UpdateStateFile(string directory)
    {
        _path = Path.Combine(directory, "update-state.json");
        _scopes = [];
        if (!File.Exists(_path)) return;
        byte[] bytes = File.ReadAllBytes(_path);
        if (bytes.Length > 128 * 1024) throw new InvalidDataException("更新检查记录过大，原记录已保留。");
        UpdateManifest.RejectDuplicateProperties(bytes);
        var state = JsonSerializer.Deserialize<State>(bytes, UpdateManifest.Json);
        if (state is null || state.Version != 1 || state.Scopes is null || state.Scopes.Count > 64 ||
            state.Scopes.Any(row => row.Key.Length > 256 || row.Value is null || row.Value.HighestSeen < 0 ||
                row.Value.SkippedBuild < 0 || row.Value.LastPromptBuild < 0 || row.Value.LastResult is null || row.Value.LastResult.Length > 500))
            throw new InvalidDataException("更新检查记录无效，原记录已保留。");
        _scopes = state.Scopes;
    }

    public UpdateScopeState Get(string scope) { lock (_gate) return _scopes.GetValueOrDefault(scope) ?? new(); }
    public void Save(string scope, UpdateScopeState value)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, UpdateScopeState>(_scopes, StringComparer.Ordinal) { [scope] = value };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, new State(1, next), UpdateManifest.Json);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _scopes = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
