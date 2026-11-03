using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Feige.Core.Protocol;

namespace Feige.Core.Storage;

/// <summary>v6 folder fields of a manifest (Protocol v6 §2). Every file of one folder send shares all but <c>relativePath</c>.</summary>
public sealed record V6FolderInfo(
    [property: JsonRequired] string RootName,
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] Guid FolderID,
    [property: JsonRequired] int FileCount,
    [property: JsonRequired] ulong TotalBytes)
{
    public const int MaxFileCount = 10_000;
    public const int MaxComponents = 32;
    public const int MaxPathBytes = 1024;

    public string[] Components => RelativePath.Split('/');

    public void Validate()
    {
        if (!IsValidComponent(RootName) || FolderID == Guid.Empty || FileCount is < 1 or > MaxFileCount ||
            TotalBytes > long.MaxValue || Encoding.UTF8.GetByteCount(RelativePath ?? "") > MaxPathBytes)
            throw new ProtocolException(ProtocolError.InvalidManifest);
        string[] components = Components;
        if (components.Length > MaxComponents || !Array.TrueForAll(components, IsValidComponent))
            throw new ProtocolException(ProtocolError.InvalidManifest);
    }

    public bool SharesFolder(V6FolderInfo other)
        => RootName == other.RootName && FolderID == other.FolderID && FileCount == other.FileCount && TotalBytes == other.TotalBytes;

    public static bool IsValidComponent(string? value)
    {
        try { TransferManifest.ValidateFileName(value); return true; }
        catch (ProtocolException) { return false; }
    }
}

/// <summary>Sender-side identifiers. They are hashes so the peer never learns local paths.</summary>
public static class ResumeIdentifiers
{
    public static Guid ResumeID(string path, long size, DateTime modifiedUtc)
        => FromText($"file\0{Path.GetFullPath(path)}\0{size}\0{modifiedUtc.Ticks}");

    public static Guid FolderID(string path)
        => FromText($"folder\0{Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))}");

    /// <summary>Receiver storage key: the authenticated peer identity is part of it, so another device cannot extend the file.</summary>
    public static string StorageKey(string peerFingerprint, Guid id)
    {
        string peer = string.Concat(peerFingerprint.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(peer + "\0" + id.ToString("D").ToLowerInvariant()));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private static Guid FromText(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

public sealed record FolderEntry(string Path, string RelativePath, long Size, DateTime ModifiedUtc);

/// <summary>Lists the regular files of a folder in sending order. Reparse points are never followed.</summary>
public static class FolderEnumerator
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    public static IReadOnlyList<FolderEntry> Entries(string folder)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var result = new List<FolderEntry>();
        var pending = new Stack<(string Path, string Relative, int Depth)>();
        pending.Push((root, "", 0));
        while (pending.Count > 0)
        {
            var (directory, relative, depth) = pending.Pop();
            foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || Skipped.Contains(item.Name)) continue;
                string path = relative.Length == 0 ? item.Name : relative + "/" + item.Name;
                if (!V6FolderInfo.IsValidComponent(item.Name) || depth + 1 > V6FolderInfo.MaxComponents ||
                    Encoding.UTF8.GetByteCount(path) > V6FolderInfo.MaxPathBytes)
                    throw new IOException("文件夹层级太深或路径太长，无法发送。");
                if (item is DirectoryInfo) { pending.Push((item.FullName, path, depth + 1)); continue; }
                var file = (FileInfo)item;
                result.Add(new FolderEntry(file.FullName, path, file.Length, file.LastWriteTimeUtc));
                if (result.Count > V6FolderInfo.MaxFileCount) throw new IOException("文件夹内文件超过 10000 个，无法发送。");
            }
        }
        if (result.Count == 0) throw new IOException("文件夹中没有可发送的文件。");
        result.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }
}

/// <summary>
/// Keeps a partial file across disconnects in the receive directory as <c>.feige-resume-&lt;key&gt;.part</c>.
/// The commit verifies the whole-file SHA-256 before moving it to a name that never overwrites an existing file.
/// </summary>
public sealed class ResumableFileSink : IAsyncDisposable
{
    private readonly string _partPath;
    private readonly string _targetDirectory;
    private readonly string _fileName;
    private readonly long _expectedSize;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private FileStream? _stream;
    private long _written;
    private bool _finished;

    private ResumableFileSink(string partPath, string targetDirectory, string fileName, long expectedSize)
    {
        _partPath = partPath;
        _targetDirectory = targetDirectory;
        _fileName = fileName;
        _expectedSize = expectedSize;
    }

    /// <summary>Bytes already present from an earlier attempt; the sender resumes here.</summary>
    public long ExistingLength { get; private set; }
    public long Written => _written;

    public static async Task<ResumableFileSink> OpenAsync(string receiveDirectory, string key, long expectedSize,
        string targetDirectory, string fileName, CancellationToken cancellationToken = default)
    {
        TransferManifest.ValidateFileName(fileName);
        if (expectedSize < 0) throw new ProtocolException(ProtocolError.InvalidManifest);
        string partPath = Path.Combine(receiveDirectory, $".feige-resume-{key}.part");
        var sink = new ResumableFileSink(partPath, targetDirectory, fileName, expectedSize);
        try
        {
            Directory.CreateDirectory(receiveDirectory);
            if (File.Exists(partPath) && File.GetAttributes(partPath).HasFlag(FileAttributes.ReparsePoint)) File.Delete(partPath);
            sink._stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous);
            // A partial file longer than the manifest belongs to something else and starts over.
            if (sink._stream.Length > expectedSize) sink._stream.SetLength(0);
            byte[] buffer = new byte[1 << 20];
            sink._stream.Position = 0;
            int read;
            while ((read = await sink._stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sink._hash.AppendData(buffer, 0, read);
                sink._written += read;
            }
            sink.ExistingLength = sink._written;
            return sink;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await sink.DisposeAsync();
            throw new ProtocolException(ProtocolError.StorageFailure, ex);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_finished || _stream is null) throw new InvalidOperationException("The sink has already finished.");
        if (data.Length > _expectedSize - _written) throw new ProtocolException(ProtocolError.SizeMismatch);
        try { await _stream.WriteAsync(data, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ProtocolException(ProtocolError.StorageFailure, ex); }
        _hash.AppendData(data.Span);
        _written += data.Length;
    }

    /// <summary>On a digest mismatch the partial file is deleted, so the next attempt starts at 0.</summary>
    public async Task<string> CommitAsync(ReadOnlyMemory<byte> expectedSha256, CancellationToken cancellationToken = default)
    {
        if (_finished || _stream is null) throw new InvalidOperationException("The sink has already finished.");
        if (expectedSha256.Length != TransferManifest.Sha256Size) throw new ProtocolException(ProtocolError.InvalidManifest);
        if (_written != _expectedSize) throw new ProtocolException(ProtocolError.SizeMismatch);
        if (!CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), expectedSha256.Span))
        {
            await RemovePartialAsync();
            throw new ProtocolException(ProtocolError.ChecksumMismatch);
        }
        try
        {
            await _stream.FlushAsync(cancellationToken);
            _stream.Flush(flushToDisk: true);
            await _stream.DisposeAsync();
            _stream = null;
            Directory.CreateDirectory(_targetDirectory);
            string name = SafeFileName.MakeWritable(_fileName);
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                string candidate = Path.Combine(_targetDirectory, SafeFileName.WithSuffix(name, attempt));
                try
                {
                    File.Move(_partPath, candidate, overwrite: false);
                    _finished = true;
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
            throw new ProtocolException(ProtocolError.StorageFailure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException(ProtocolError.StorageFailure, ex);
        }
    }

    public async Task RemovePartialAsync()
    {
        _finished = true;
        if (_stream is not null) { await _stream.DisposeAsync(); _stream = null; }
        try { File.Delete(_partPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Keeps the partial file for the next attempt.</summary>
    public async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        if (_stream is not null)
        {
            try { await _stream.FlushAsync(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            await _stream.DisposeAsync();
            _stream = null;
        }
    }
}

/// <summary>
/// Receiver-side folder progress, stored as <c>.feige-folder-&lt;key&gt;.state</c>: the first line is the created
/// root directory name, each later line a completed resume ID.
/// </summary>
public sealed class FolderReceiveState
{
    private readonly string _receiveDirectory;
    private readonly string _statePath;
    private readonly HashSet<Guid> _completed;

    private FolderReceiveState(V6FolderInfo info, string receiveDirectory, string statePath, string rootPath, HashSet<Guid> completed)
    {
        Info = info;
        _receiveDirectory = receiveDirectory;
        _statePath = statePath;
        RootPath = rootPath;
        _completed = completed;
    }

    public V6FolderInfo Info { get; }
    public string RootPath { get; }
    public int AcceptedFiles { get; set; }
    public ulong AcceptedBytes { get; set; }
    public ulong FinishedBytes { get; set; }
    public bool IsCompleted(Guid resumeID) => _completed.Contains(resumeID);

    public static FolderReceiveState Open(V6FolderInfo info, string receiveDirectory, string peerFingerprint)
    {
        receiveDirectory = Path.GetFullPath(receiveDirectory);
        Directory.CreateDirectory(receiveDirectory);
        string statePath = Path.Combine(receiveDirectory, $".feige-folder-{ResumeIdentifiers.StorageKey(peerFingerprint, info.FolderID)}.state");
        string? rootName = null;
        var completed = new HashSet<Guid>();
        if (File.Exists(statePath) && !File.GetAttributes(statePath).HasFlag(FileAttributes.ReparsePoint) &&
            new FileInfo(statePath).Length <= 1 << 20)
        {
            string[] lines = File.ReadAllLines(statePath, Encoding.UTF8);
            if (lines.Length > 0 && V6FolderInfo.IsValidComponent(lines[0]) &&
                IsPlainDirectory(Path.Combine(receiveDirectory, lines[0])))
            {
                rootName = lines[0];
                foreach (string line in lines.Skip(1)) if (Guid.TryParse(line, out Guid id)) completed.Add(id);
            }
        }
        rootName ??= CreateUniqueDirectory(receiveDirectory, SafeFileName.MakeWritable(info.RootName));
        var state = new FolderReceiveState(info, receiveDirectory, statePath, Path.Combine(receiveDirectory, rootName), completed);
        state.Persist();
        return state;
    }

    /// <summary>Creates the parent directories of a folder file below the root, refusing reparse points.</summary>
    public string ParentDirectory(V6FolderInfo info)
    {
        string current = RootPath;
        foreach (string component in info.Components[..^1])
        {
            current = Path.Combine(current, SafeFileName.MakeWritable(component));
            if (File.Exists(current) || Directory.Exists(current) && !IsPlainDirectory(current))
                throw new ProtocolException(ProtocolError.StorageFailure);
            Directory.CreateDirectory(current);
        }
        return current;
    }

    public void MarkCompleted(Guid resumeID)
    {
        _completed.Add(resumeID);
        Persist();
    }

    public void RemoveState()
    {
        try { File.Delete(_statePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Persist()
    {
        string temporary = _statePath + ".tmp";
        var lines = new List<string> { Path.GetFileName(RootPath) };
        lines.AddRange(_completed.Select(id => id.ToString("D")));
        File.WriteAllText(temporary, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static bool IsPlainDirectory(string path)
        => Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>A new folder never merges into an existing one: <c>Photos</c>, <c>Photos (1)</c>, …</summary>
    private static string CreateUniqueDirectory(string parent, string preferred)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string name = attempt == 0 ? preferred : $"{preferred} ({attempt})";
            string path = Path.Combine(parent, name);
            if (File.Exists(path) || Directory.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return name;
        }
        throw new ProtocolException(ProtocolError.StorageFailure);
    }
}

public static class ResumableStorageCleanup
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Removes partial files and folder progress older than seven days from the receive directory.</summary>
    public static IReadOnlyList<string> RemoveExpired(string receiveDirectory, DateTime? nowUtc = null)
    {
        var removed = new List<string>();
        if (!Directory.Exists(receiveDirectory)) return removed;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        foreach (var file in new DirectoryInfo(receiveDirectory).EnumerateFiles(".feige-*"))
        {
            bool ours = file.Name.StartsWith(".feige-resume-", StringComparison.Ordinal) && file.Name.EndsWith(".part", StringComparison.Ordinal) ||
                file.Name.StartsWith(".feige-folder-", StringComparison.Ordinal) &&
                (file.Name.EndsWith(".state", StringComparison.Ordinal) || file.Name.EndsWith(".state.tmp", StringComparison.Ordinal));
            if (!ours || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || now - file.LastWriteTimeUtc <= Retention) continue;
            try { file.Delete(); removed.Add(file.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
