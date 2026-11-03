using System.Text.Json;

namespace Feige.Core.Storage;

/// <summary>
/// Local unread counts keyed by conversation: a peer fingerprint, or <c>group:&lt;id&gt;</c>.
/// Nothing is sent to the peer: there are no read receipts.
/// </summary>
public sealed class UnreadCounter
{
    private readonly string? _path;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="path">JSON file to persist to; null keeps counts in memory only.</param>
    public UnreadCounter(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllBytes(path));
            foreach (var (key, value) in saved ?? []) if (value > 0) _counts[key] = value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public static string GroupKey(Guid groupId) => "group:" + groupId.ToString("D");

    public int Get(string key) { lock (_gate) return _counts.GetValueOrDefault(key); }

    /// <summary>Sum over conversations accepted by <paramref name="include"/>.</summary>
    public int Total(Func<string, bool>? include = null)
    {
        lock (_gate) return _counts.Where(entry => include?.Invoke(entry.Key) ?? true).Sum(entry => entry.Value);
    }

    public void Increment(string key)
    {
        lock (_gate) _counts[key] = _counts.GetValueOrDefault(key) + 1;
        Save();
    }

    /// <returns>True when the conversation had unread arrivals.</returns>
    public bool Clear(string key)
    {
        lock (_gate) if (!_counts.Remove(key)) return false;
        Save();
        return true;
    }

    private void Save()
    {
        if (_path is null) return;
        byte[] data;
        lock (_gate) data = JsonSerializer.SerializeToUtf8Bytes(_counts);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
}
