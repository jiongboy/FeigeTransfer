using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feige.Core.Storage;

/// <summary>Which way a recorded file went.</summary>
public enum TransferDirection
{
    /// <summary>The file arrived from a peer.</summary>
    Received,

    /// <summary>The file was sent to a peer.</summary>
    Sent,
}

/// <summary>One persisted transfer, enough to rebuild the list after a restart.</summary>
/// <param name="Direction">Received or sent.</param>
/// <param name="Name">File name as written on disk.</param>
/// <param name="Path">Absolute path on this machine.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Peer">Device name of the other side.</param>
/// <param name="At">When the transfer finished.</param>
public sealed record TransferEntry(
    TransferDirection Direction,
    string Name,
    string Path,
    long Size,
    string Peer,
    DateTimeOffset At);

/// <summary>
/// Transfers across sessions, stored as a small JSON document in a directory the caller chooses.
/// </summary>
/// <remarks>
/// <para>
/// The list is a convenience, not a record of truth: the files on disk are. Every load and save is wrapped in a catch
/// because a corrupt, unreadable or locked history must never stop the app from starting — worst case the lists come
/// up empty.
/// </para>
/// <para>
/// Writes go through a temp file plus <see cref="File.Move(string, string, bool)"/> so a crash mid-write cannot leave
/// a truncated document behind; the previous file stays valid until the new one is complete.
/// </para>
/// <para>
/// This type is deliberately free of Windows-specific APIs so it can be exercised in plain .NET tests; the app only
/// supplies the directory.
/// </para>
/// </remarks>
public sealed class TransferHistory
{
    /// <summary>Newest entries kept; older ones are dropped on save.</summary>
    public const int MaxEntries = 100;

    /// <summary>File name inside the history directory.</summary>
    public const string FileName = "history.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<TransferEntry> _entries = [];

    /// <summary>Creates an empty history that reads and writes <see cref="FilePath"/>.</summary>
    /// <param name="directory">Directory holding <c>history.json</c>; created on first save.</param>
    public TransferHistory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>Directory holding the history file. Not created until something is saved.</summary>
    public string Directory { get; }

    /// <summary>Full path of the JSON document.</summary>
    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Recorded transfers, newest first.</summary>
    public IReadOnlyList<TransferEntry> Entries => _entries;

    /// <summary>Reads the history from <paramref name="directory"/>. Never throws.</summary>
    /// <param name="directory">Directory holding <c>history.json</c>.</param>
    public static TransferHistory Load(string directory)
    {
        var history = new TransferHistory(directory);

        try
        {
            string path = history.FilePath;

            if (!File.Exists(path))
            {
                return history;
            }

            HistoryFile? file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), SerializerOptions);

            if (file?.Entries is null)
            {
                return history;
            }

            foreach (HistoryEntryDto dto in file.Entries)
            {
                // Skip rows that no longer make sense instead of discarding the whole file.
                if (dto.TryToEntry(out TransferEntry? entry))
                {
                    history._entries.Add(entry!);
                }

                if (history._entries.Count >= MaxEntries)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            history._entries.Clear();
        }

        return history;
    }

    /// <summary>Records a transfer, dropping the oldest entry past <see cref="MaxEntries"/>.</summary>
    public void Add(TransferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Insert(0, entry);

        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }

        Save();
    }

    /// <summary>Forgets everything and persists the empty list.</summary>
    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var file = new HistoryFile { Version = 1 };

            foreach (TransferEntry entry in _entries)
            {
                file.Entries.Add(HistoryEntryDto.From(entry));
            }

            string path = FilePath;
            string temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing history is annoying, not fatal: the files themselves are already committed on disk.
        }
    }

    /// <summary>On-disk shape, kept separate from <see cref="TransferEntry"/> so the file format outlives refactors.</summary>
    internal sealed class HistoryFile
    {
        /// <summary>Required by the serializer: it only picks up public parameterless constructors.</summary>
        public HistoryFile()
        {
        }

        public int Version { get; set; } = 1;

        public List<HistoryEntryDto> Entries { get; set; } = [];
    }

    /// <summary>One row of the on-disk file.</summary>
    internal sealed class HistoryEntryDto
    {
        /// <summary>Required by the serializer: it only picks up public parameterless constructors.</summary>
        public HistoryEntryDto()
        {
        }

        public string Direction { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Peer { get; set; } = string.Empty;

        public DateTimeOffset At { get; set; }

        public static HistoryEntryDto From(TransferEntry entry) => new()
        {
            Direction = entry.Direction == TransferDirection.Sent ? "sent" : "received",
            Name = entry.Name,
            Path = entry.Path,
            Size = entry.Size,
            Peer = entry.Peer,
            At = entry.At,
        };

        /// <summary>Converts to a usable entry; returns false for rows missing required fields.</summary>
        public bool TryToEntry(out TransferEntry? entry)
        {
            entry = null;

            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Path) || Size < 0)
            {
                return false;
            }

            TransferDirection direction = string.Equals(Direction, "sent", StringComparison.OrdinalIgnoreCase)
                ? TransferDirection.Sent
                : TransferDirection.Received;

            entry = new TransferEntry(direction, Name, Path, Size, Peer ?? string.Empty, At);
            return true;
        }
    }
}
