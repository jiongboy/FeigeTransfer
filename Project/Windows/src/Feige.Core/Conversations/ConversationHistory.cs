using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Storage;

namespace Feige.Core.Conversations;

public enum MessageKind { Text, File, Image }
public enum MessageState { Queued, Sending, Delivered, Received, Failed, Declined }

public sealed record ConversationMessage(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] string PeerName,
    [property: JsonRequired] TransferDirection Direction,
    [property: JsonRequired] MessageKind Kind,
    [property: JsonRequired] MessageState State,
    [property: JsonRequired] DateTimeOffset At,
    string Text = "", string FileName = "", string Path = "", long Size = 0, string Error = "")
{
    public bool IsActive => State is MessageState.Queued or MessageState.Sending;
    public void Validate()
    {
        if (Id == Guid.Empty || Fingerprint.Length != 64 || !Fingerprint.All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(PeerName) || !Enum.IsDefined(Direction) || !Enum.IsDefined(Kind) ||
            !Enum.IsDefined(State) || At == default || Size < 0)
            throw new InvalidDataException("会话记录格式无效。");
        if (Kind == MessageKind.Text) new V3Text(Id, Text).Validate();
        else Feige.Core.Protocol.TransferManifest.ValidateFileName(FileName);
        if ((Direction == TransferDirection.Sent && State == MessageState.Received) ||
            (Direction == TransferDirection.Received && State is (MessageState.Queued or MessageState.Delivered)) ||
            (State == MessageState.Declined && Kind == MessageKind.Text) ||
            (Direction == TransferDirection.Received && State == MessageState.Declined && string.IsNullOrWhiteSpace(Error)))
            throw new InvalidDataException("会话记录方向与状态不一致。");
    }
}

/// <summary>Atomic, strict message storage. A failed write never updates the in-memory snapshot or claims delivery.</summary>
public sealed class ConversationHistory
{
    public const int MaxCompleted = 1000;
    private readonly string _path;
    private readonly object _gate = new();
    private List<ConversationMessage> _messages = [];
    private HashSet<string> _hidden = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record HistoryFile([property: JsonRequired] int Version, [property: JsonRequired] List<ConversationMessage> Messages, List<string>? HiddenFingerprints = null);

    private ConversationHistory(string directory) => _path = System.IO.Path.Combine(directory, "conversations.json");
    public IReadOnlyList<ConversationMessage> Messages { get { lock (_gate) return _messages.ToArray(); } }
    public IReadOnlyCollection<string> HiddenFingerprints { get { lock (_gate) return _hidden.ToArray(); } }
    public bool IsHidden(string fingerprint) { lock (_gate) return _hidden.Contains(RequireFingerprint(fingerprint)); }

    public static string NormalizeFingerprint(string value) => value.Replace(" ", "").ToUpperInvariant();

    public static ConversationHistory Load(string directory)
    {
        var history = new ConversationHistory(directory);
        if (!File.Exists(history._path)) return history;
        var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllBytes(history._path), Json)
            ?? throw new InvalidDataException("无法读取本机会话记录。");
        if (file.Version != 1 || file.Messages is null) throw new InvalidDataException("会话记录版本不兼容。");
        var keys = new HashSet<(string, TransferDirection, Guid)>();
        foreach (var row in file.Messages)
        {
            if (row is null || row.Fingerprint is null) throw new InvalidDataException("会话记录格式无效。");
            var message = row with { Fingerprint = NormalizeFingerprint(row.Fingerprint) };
            message.Validate();
            if (!keys.Add((message.Fingerprint, message.Direction, message.Id))) throw new InvalidDataException("会话记录重复。");
            history._messages.Add(message.IsActive ? message with { State = MessageState.Failed, Error = "上次运行时未完成，请重连后重试。" } : message);
        }
        if (file.HiddenFingerprints is { Count: > 10000 }) throw new InvalidDataException("隐藏会话记录过多。");
        foreach (string fingerprint in file.HiddenFingerprints ?? [])
            history._hidden.Add(RequireFingerprint(fingerprint));
        history._messages = Trim(history._messages);
        return history;
    }

    public void Upsert(ConversationMessage message)
    {
        message = message with { Fingerprint = NormalizeFingerprint(message.Fingerprint) };
        message.Validate();
        lock (_gate)
        {
            var next = _messages.ToList();
            int index = next.FindIndex(m => m.Fingerprint == message.Fingerprint && m.Direction == message.Direction && m.Id == message.Id);
            if (index < 0) next.Add(message); else next[index] = message;
            var hidden = new HashSet<string>(_hidden, StringComparer.Ordinal);
            // Only a new incoming message reveals a hidden conversation, not a receipt or retry update.
            if (index < 0 && message.Direction == TransferDirection.Received) hidden.Remove(message.Fingerprint);
            Commit(next, hidden);
        }
    }

    public IReadOnlyList<ConversationMessage> FailActive(string fingerprint, string error)
    {
        fingerprint = NormalizeFingerprint(fingerprint);
        lock (_gate)
        {
            var changed = _messages.Where(m => m.Fingerprint == fingerprint && m.IsActive)
                .Select(m => m with { State = MessageState.Failed, Error = error }).ToArray();
            if (changed.Length == 0) return changed;
            var next = _messages.Select(m => m.Fingerprint == fingerprint && m.IsActive ? m with { State = MessageState.Failed, Error = error } : m).ToList();
            Commit(next);
            return changed;
        }
    }

    public void SetHidden(string fingerprint, bool hidden)
    {
        fingerprint = RequireFingerprint(fingerprint);
        lock (_gate)
        {
            if (_hidden.Contains(fingerprint) == hidden) return;
            var next = new HashSet<string>(_hidden, StringComparer.Ordinal);
            if (hidden) next.Add(fingerprint); else next.Remove(fingerprint);
            if (next.Count > 10000) throw new InvalidDataException("隐藏会话记录过多。");
            Commit(_messages.ToList(), next);
        }
    }

    public void RestoreAllHidden()
    {
        lock (_gate) { if (_hidden.Count > 0) Commit(_messages.ToList(), new(StringComparer.Ordinal)); }
    }

    public void DeleteConversation(string fingerprint)
    {
        fingerprint = RequireFingerprint(fingerprint);
        lock (_gate)
        {
            if (_messages.Any(m => m.Fingerprint == fingerprint && m.IsActive))
                throw new InvalidOperationException("正在收发的会话无法删除，请完成传输后重试。");
            var next = _messages.Where(m => m.Fingerprint != fingerprint).ToList();
            var hidden = new HashSet<string>(_hidden, StringComparer.Ordinal); hidden.Remove(fingerprint);
            if (next.Count != _messages.Count || hidden.Count != _hidden.Count) Commit(next, hidden);
        }
    }

    private static string RequireFingerprint(string fingerprint)
    {
        if (fingerprint is null) throw new InvalidDataException("会话身份无效。");
        string normalized = NormalizeFingerprint(fingerprint);
        if (normalized.Length != 64 || !normalized.All(char.IsAsciiHexDigit)) throw new InvalidDataException("会话身份无效。");
        return normalized;
    }

    private void Commit(List<ConversationMessage> next, HashSet<string>? hidden = null)
    {
        next = Trim(next);
        hidden ??= new HashSet<string>(_hidden, StringComparer.Ordinal);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new HistoryFile(1, next, hidden.Order(StringComparer.Ordinal).ToList()), Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _messages = next;
            _hidden = hidden;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static List<ConversationMessage> Trim(List<ConversationMessage> messages)
    {
        var completed = messages.Where(m => !m.IsActive).OrderByDescending(m => m.At).Take(MaxCompleted).ToHashSet();
        return messages.Where(m => m.IsActive || completed.Contains(m)).OrderBy(m => m.At).ToList();
    }
}
