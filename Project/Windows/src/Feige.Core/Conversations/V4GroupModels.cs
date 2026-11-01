using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feige.Core.Conversations;

/// <summary>A v4 group roster entry; the full authenticated fingerprint is its only identity.</summary>
public sealed record GroupMember
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; }

    /// <summary>Untrusted display-only metadata. Never use this value for identity or authorization.</summary>
    [JsonPropertyName("displayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; }

    [JsonConstructor]
    public GroupMember(string fingerprint, string? displayName = null)
    {
        Fingerprint = V4GroupWire.NormalizeFingerprint(fingerprint);
        DisplayName = V4GroupWire.NormalizeDisplayName(displayName);
    }
}

/// <summary>Validated immutable v4 group state, including the committed membership epoch.</summary>
public sealed record GroupConversation
{
    public const int MaximumMembers = 20;

    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("ownerFingerprint")]
    public string OwnerFingerprint { get; }

    [JsonPropertyName("revision")]
    public ulong Revision { get; }

    [JsonPropertyName("members")]
    public IReadOnlyList<GroupMember> Members { get; }

    [JsonPropertyName("previousOwnerFingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousOwnerFingerprint { get; }

    [JsonConstructor]
    public GroupConversation(Guid id, string name, string ownerFingerprint, ulong revision,
        IReadOnlyList<GroupMember> members, string? previousOwnerFingerprint = null)
    {
        if (members is null) throw new InvalidDataException("群组成员列表不能为空。");
        Id = id;
        Name = V4GroupWire.NormalizeGroupName(name);
        OwnerFingerprint = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        Revision = revision;
        Members = Array.AsReadOnly(members.ToArray());
        PreviousOwnerFingerprint = previousOwnerFingerprint is null
            ? null
            : V4GroupWire.NormalizeFingerprint(previousOwnerFingerprint);

        if (PreviousOwnerFingerprint == OwnerFingerprint)
            throw new InvalidDataException("前任群主不能与当前群主相同。");
        if (Members.Count == 0)
            throw new InvalidDataException("群组至少需要一名成员。");
        if (Members.Count > MaximumMembers)
            throw new InvalidDataException($"群组成员不能超过 {MaximumMembers} 人。");
        if (Members.Any(member => member is null))
            throw new InvalidDataException("群组成员不能为空。");
        if (Members.Select(member => member.Fingerprint).Distinct(StringComparer.Ordinal).Count() != Members.Count)
            throw new InvalidDataException("群组成员指纹必须唯一。");
        if (!Members.Any(member => member.Fingerprint == OwnerFingerprint))
            throw new InvalidDataException("群主必须位于群组成员名单中。");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<GroupControlKind>))]
public enum GroupControlKind
{
    Invite,
    InviteReply,
    MembershipPrepare,
    MembershipPrepareReply,
    MembershipCommit,
    MembershipCommitReply,
    MembershipAbort,
    MembershipAbortReply,
    Leave,
    LeaveReply
}

/// <summary>Strict v4 group membership control payload matching the macOS Codable field names.</summary>
public sealed record GroupControlMessage
{
    [JsonPropertyName("version")]
    public byte Version { get; }

    [JsonPropertyName("kind")]
    public GroupControlKind Kind { get; }

    [JsonPropertyName("eventID")]
    public Guid EventID { get; }

    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("ownerFingerprint")]
    public string OwnerFingerprint { get; }

    [JsonPropertyName("epoch")]
    public ulong Epoch { get; }

    [JsonPropertyName("baseEpoch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? BaseEpoch { get; }

    [JsonPropertyName("targetFingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetFingerprint { get; }

    [JsonPropertyName("newOwnerFingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewOwnerFingerprint { get; }

    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; }

    [JsonPropertyName("roster"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GroupMember>? Roster { get; }

    [JsonPropertyName("accepted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Accepted { get; }

    public GroupControlMessage(GroupControlKind kind, Guid groupID, string ownerFingerprint, ulong epoch,
        Guid? eventID = null, ulong? baseEpoch = null, string? targetFingerprint = null,
        string? newOwnerFingerprint = null, string? name = null, IReadOnlyList<GroupMember>? roster = null,
        bool? accepted = null)
        : this(1, kind, eventID ?? Guid.NewGuid(), groupID, ownerFingerprint, epoch, baseEpoch,
            targetFingerprint, newOwnerFingerprint, name, roster, accepted)
    {
    }

    [JsonConstructor]
    public GroupControlMessage(byte version, GroupControlKind kind, Guid eventID, Guid groupID,
        string ownerFingerprint, ulong epoch, ulong? baseEpoch = null, string? targetFingerprint = null,
        string? newOwnerFingerprint = null, string? name = null, IReadOnlyList<GroupMember>? roster = null,
        bool? accepted = null)
    {
        Version = version;
        Kind = kind;
        EventID = eventID;
        GroupID = groupID;
        OwnerFingerprint = V4GroupWire.NormalizeFingerprint(ownerFingerprint);
        Epoch = epoch;
        BaseEpoch = baseEpoch;
        TargetFingerprint = targetFingerprint is null ? null : V4GroupWire.NormalizeFingerprint(targetFingerprint);
        NewOwnerFingerprint = newOwnerFingerprint is null ? null : V4GroupWire.NormalizeFingerprint(newOwnerFingerprint);
        Name = name is null ? null : V4GroupWire.NormalizeGroupName(name);
        Roster = roster is null ? null : Array.AsReadOnly(roster.ToArray());
        Accepted = accepted;
        Validate();
    }

    private void Validate()
    {
        if (Version != 1) throw new InvalidDataException("不支持的群组控制消息版本。");
        if (Roster?.Any(member => member is null) == true)
            throw new InvalidDataException("群组名单不能包含空成员。");

        switch (Kind)
        {
            case GroupControlKind.Invite:
                if (BaseEpoch is not null || NewOwnerFingerprint is not null || TargetFingerprint is null ||
                    Name is null || Roster is null || Accepted is not null)
                    throw new InvalidDataException("邀请消息字段无效。");
                var invitedGroup = new GroupConversation(GroupID, Name, OwnerFingerprint, Epoch, Roster);
                if (!invitedGroup.Members.Any(member => member.Fingerprint == TargetFingerprint))
                    throw new InvalidDataException("邀请目标必须位于提议名单中。");
                break;

            case GroupControlKind.InviteReply:
            case GroupControlKind.LeaveReply:
                if (BaseEpoch is not null || NewOwnerFingerprint is not null || TargetFingerprint is null || Accepted is null)
                    throw new InvalidDataException("邀请/离开回执字段无效。");
                break;

            case GroupControlKind.MembershipPrepare:
            case GroupControlKind.MembershipCommit:
                if (Roster is null) throw new InvalidDataException("成员变更必须携带完整名单。");
                if (BaseEpoch is ulong baseEpoch)
                {
                    if (baseEpoch == ulong.MaxValue || Epoch != baseEpoch + 1)
                        throw new InvalidDataException("成员变更 epoch 必须比基础 epoch 大一。");
                }
                else if (Epoch != 0 || NewOwnerFingerprint is not null)
                {
                    throw new InvalidDataException("初始成员提交必须使用 epoch 0，且不能包含群主移交字段。");
                }
                if (NewOwnerFingerprint == OwnerFingerprint)
                    throw new InvalidDataException("新群主必须与当前群主不同。");
                _ = new GroupConversation(GroupID, Name ?? "群组", NewOwnerFingerprint ?? OwnerFingerprint, Epoch, Roster);
                break;

            case GroupControlKind.MembershipPrepareReply:
            case GroupControlKind.MembershipCommitReply:
            case GroupControlKind.MembershipAbortReply:
                if (BaseEpoch is not null || NewOwnerFingerprint is not null || TargetFingerprint is null || Accepted is null)
                    throw new InvalidDataException("成员变更回执字段无效。");
                break;

            case GroupControlKind.MembershipAbort:
                if (TargetFingerprint is null || NewOwnerFingerprint is not null || Name is not null ||
                    Roster is not null || Accepted is not null ||
                    (BaseEpoch is ulong abortBase
                        ? abortBase == ulong.MaxValue || Epoch != abortBase + 1
                        : Epoch != 0))
                    throw new InvalidDataException("成员变更取消字段无效。");
                break;

            case GroupControlKind.Leave:
                if (BaseEpoch is not null || NewOwnerFingerprint is not null || TargetFingerprint is null ||
                    Roster is not null || Name is not null || Accepted is not null)
                    throw new InvalidDataException("离开请求字段无效。");
                break;

            default:
                throw new InvalidDataException("未知的群组控制消息类型。");
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<GroupMessageKind>))]
public enum GroupMessageKind
{
    Text,
    Image,
    File
}

/// <summary>Metadata for a group image/file; deliberately contains no local filesystem path.</summary>
public sealed record GroupFileMetadata
{
    [JsonPropertyName("transferID")]
    public Guid TransferID { get; }

    [JsonPropertyName("fileName")]
    public string FileName { get; }

    [JsonPropertyName("size")]
    public ulong Size { get; }

    [JsonPropertyName("mediaType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; }

    [JsonPropertyName("sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Sha256 { get; }

    [JsonConstructor]
    public GroupFileMetadata(Guid transferID, string fileName, ulong size, string? mediaType = null, byte[]? sha256 = null)
    {
        TransferID = transferID;
        FileName = V4GroupWire.NormalizeFileName(fileName);
        if (size > long.MaxValue) throw new InvalidDataException("文件大小超出支持范围。");
        Size = size;
        MediaType = V4GroupWire.NormalizeMediaType(mediaType);
        if (sha256 is not null && sha256.Length != 32)
            throw new InvalidDataException("SHA-256 摘要必须为 32 字节。");
        Sha256 = sha256?.ToArray();
    }
}

/// <summary>Strict v4 group text/image/file envelope matching the macOS wire properties.</summary>
public sealed record GroupMessageEnvelope
{
    [JsonPropertyName("id")]
    public Guid Id { get; }

    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("senderFingerprint")]
    public string SenderFingerprint { get; }

    [JsonPropertyName("memberRevision")]
    public ulong MemberRevision { get; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; }

    [JsonPropertyName("kind")]
    public GroupMessageKind Kind { get; }

    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; }

    [JsonPropertyName("fileMetadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroupFileMetadata? FileMetadata { get; }

    [JsonConstructor]
    public GroupMessageEnvelope(Guid id, Guid groupID, string senderFingerprint, ulong memberRevision,
        DateTimeOffset date, GroupMessageKind kind, string? text = null, GroupFileMetadata? fileMetadata = null)
    {
        Id = id;
        GroupID = groupID;
        SenderFingerprint = V4GroupWire.NormalizeFingerprint(senderFingerprint);
        MemberRevision = memberRevision;
        Date = date.ToUniversalTime();
        Kind = kind;
        Text = text;
        FileMetadata = fileMetadata;
        Validate();
    }

    private void Validate()
    {
        switch (Kind)
        {
            case GroupMessageKind.Text:
                if (string.IsNullOrWhiteSpace(Text) || Encoding.UTF8.GetByteCount(Text) > 8192 || FileMetadata is not null)
                    throw new InvalidDataException("文字消息不能为空、不能只有空白字符、不能超过 8192 字节或带文件元数据。");
                break;
            case GroupMessageKind.Image:
            case GroupMessageKind.File:
                if (Text is not null || FileMetadata is null)
                    throw new InvalidDataException("图片/文件消息必须包含文件元数据且不能包含文字字段。");
                break;
            default:
                throw new InvalidDataException("未知的群消息类型。");
        }
    }
}

/// <summary>Acknowledgement for a persisted and deduplicated group text envelope.</summary>
public sealed record GroupTextAck
{
    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("messageID")]
    public Guid MessageID { get; }

    [JsonPropertyName("epoch")]
    public ulong Epoch { get; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; }

    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; }

    [JsonConstructor]
    public GroupTextAck(Guid groupID, Guid messageID, ulong epoch, bool accepted, string? reason = null)
    {
        GroupID = groupID;
        MessageID = messageID;
        Epoch = epoch;
        Reason = V4GroupWire.NormalizeReason(reason);
        Accepted = accepted;
        if (accepted ? Reason is not null : Reason is null)
            throw new InvalidDataException("已接受的回执不能带原因；拒绝回执必须带原因。");
    }
}

/// <summary>Canonical v4 group payload JSON and strict typed decoders.</summary>
public static class V4GroupWire
{
    public const string Canonicalization = "utf8-sorted-keys-compact-unescaped-slashes-unix-milliseconds-omit-nil-v1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static byte[] EncodeCanonical<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(encoded);
        using var output = new MemoryStream(encoded.Length);
        WriteCanonical(document.RootElement, output);
        return output.ToArray();
    }

    public static GroupControlMessage DecodeControl(ReadOnlySpan<byte> utf8Json)
        => DecodeStrict<GroupControlMessage>(utf8Json);

    public static GroupMessageEnvelope DecodeGroupText(ReadOnlySpan<byte> utf8Json)
        => DecodeStrict<GroupMessageEnvelope>(utf8Json);

    public static GroupTextAck DecodeGroupTextAck(ReadOnlySpan<byte> utf8Json)
        => DecodeStrict<GroupTextAck>(utf8Json);

    public static T DecodeStrict<T>(ReadOnlySpan<byte> utf8Json)
    {
        byte[] jsonBytes = utf8Json.ToArray();
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonBytes);
            RejectDuplicateProperties(document.RootElement);
            ValidateRequiredFields<T>(document.RootElement);
            return JsonSerializer.Deserialize<T>(jsonBytes, JsonOptions)
                ?? throw new InvalidDataException("群组消息 JSON 不能为 null。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("群组消息 JSON 无效或包含未知字段。", exception);
        }
    }

    internal static string NormalizeFingerprint(string fingerprint)
    {
        if (fingerprint is null) throw new InvalidDataException("设备指纹不能为空。");
        string hex = fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (hex.Length != 64 || hex.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new InvalidDataException("设备指纹必须是完整的 SHA-256 十六进制值。");

        var result = new StringBuilder(79);
        for (int index = 0; index < hex.Length; index += 4)
        {
            if (index > 0) result.Append(' ');
            result.Append(hex, index, 4);
        }
        return result.ToString();
    }

    internal static string NormalizeGroupName(string name)
    {
        if (name is null) throw new InvalidDataException("群组名称不能为空。");
        string normalized = name.Trim();
        if (normalized.Length == 0 || new StringInfo(normalized).LengthInTextElements > 80 || HasControlCharacter(normalized))
            throw new InvalidDataException("群组名称不能为空、不能含控制字符，且最多 80 个字符。");
        return normalized;
    }

    internal static string? NormalizeDisplayName(string? displayName)
    {
        if (displayName is null) return null;
        string normalized = displayName.Trim();
        if (Encoding.UTF8.GetByteCount(normalized) > 256 || HasControlCharacter(normalized))
            throw new InvalidDataException("成员显示名称包含控制字符或超过 256 字节。");
        return normalized.Length == 0 ? null : normalized;
    }

    internal static string NormalizeFileName(string fileName)
    {
        if (fileName is null) throw new InvalidDataException("文件名不能为空。");
        string normalized = fileName.Trim();
        if (normalized.Length == 0 || normalized is "." or ".." || Encoding.UTF8.GetByteCount(normalized) > 255 ||
            normalized.Contains('/') || normalized.Contains('\\') || HasControlCharacter(normalized))
            throw new InvalidDataException("文件名无效：不能为空、包含路径分隔符或控制字符，且最多 255 字节。");
        return normalized;
    }

    internal static string? NormalizeMediaType(string? mediaType)
    {
        if (mediaType is null) return null;
        string normalized = mediaType.Trim();
        if (normalized.Length == 0 || Encoding.UTF8.GetByteCount(normalized) > 127 ||
            !normalized.Contains('/') || normalized.Any(character => character is < '!' or > '~'))
            throw new InvalidDataException("媒体类型格式无效或过长。");
        return normalized;
    }

    internal static string? NormalizeReason(string? reason)
    {
        if (reason is null) return null;
        string normalized = reason.Trim();
        if (normalized.Length == 0 || Encoding.UTF8.GetByteCount(normalized) > 2048 || HasControlCharacter(normalized))
            throw new InvalidDataException("回执原因不能为空、不能含控制字符，且最多 2048 字节。");
        return normalized;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter<GroupControlKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<GroupMessageKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UnixMillisecondsDateTimeOffsetConverter());
        return options;
    }

    private static void WriteCanonical(JsonElement element, Stream output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.WriteByte((byte)'{');
                bool firstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) output.WriteByte((byte)',');
                    firstProperty = false;
                    WriteJsonString(output, property.Name);
                    output.WriteByte((byte)':');
                    WriteCanonical(property.Value, output);
                }
                output.WriteByte((byte)'}');
                break;
            case JsonValueKind.Array:
                output.WriteByte((byte)'[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem) output.WriteByte((byte)',');
                    firstItem = false;
                    WriteCanonical(item, output);
                }
                output.WriteByte((byte)']');
                break;
            case JsonValueKind.String:
                WriteJsonString(output, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                output.Write(Encoding.UTF8.GetBytes(element.GetRawText()));
                break;
            case JsonValueKind.True:
                WriteAscii(output, "true");
                break;
            case JsonValueKind.False:
                WriteAscii(output, "false");
                break;
            case JsonValueKind.Null:
                WriteAscii(output, "null");
                break;
            default:
                throw new InvalidDataException("群组 JSON 包含不支持的值。");
        }
    }

    private static void WriteJsonString(Stream output, string value)
    {
        output.WriteByte((byte)'"');
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x22: output.Write([0x5c, 0x22]); break;
                case 0x5c: output.Write([0x5c, 0x5c]); break;
                case 0x08: output.Write([0x5c, (byte)'b']); break;
                case 0x0c: output.Write([0x5c, (byte)'f']); break;
                case 0x0a: output.Write([0x5c, (byte)'n']); break;
                case 0x0d: output.Write([0x5c, (byte)'r']); break;
                case 0x09: output.Write([0x5c, (byte)'t']); break;
                default:
                    if (rune.Value < 0x20)
                    {
                        const string hex = "0123456789abcdef";
                        output.Write([0x5c, (byte)'u', (byte)'0', (byte)'0',
                            (byte)hex[(rune.Value >> 4) & 0x0f], (byte)hex[rune.Value & 0x0f]]);
                    }
                    else
                    {
                        int length = rune.EncodeToUtf8(utf8);
                        output.Write(utf8[..length]);
                    }
                    break;
            }
        }
        output.WriteByte((byte)'"');
    }

    private static void WriteAscii(Stream output, string value)
        => output.Write(Encoding.ASCII.GetBytes(value));

    private static void ValidateRequiredFields<T>(JsonElement root)
    {
        Type modelType = typeof(T);
        if (modelType == typeof(GroupControlMessage))
        {
            RequireProperties(root, "version", "kind", "eventID", "groupID", "ownerFingerprint", "epoch");
            ValidateMembersProperty(root, "roster");
        }
        else if (modelType == typeof(GroupMessageEnvelope))
        {
            RequireProperties(root, "id", "groupID", "senderFingerprint", "memberRevision", "date", "kind");
            if (root.TryGetProperty("fileMetadata", out JsonElement metadata) && metadata.ValueKind != JsonValueKind.Null)
                RequireProperties(metadata, "transferID", "fileName", "size");
        }
        else if (modelType == typeof(GroupTextAck))
        {
            RequireProperties(root, "groupID", "messageID", "epoch", "accepted");
        }
        else if (modelType == typeof(GroupConversation))
        {
            RequireProperties(root, "id", "name", "ownerFingerprint", "revision", "members");
            ValidateMembersProperty(root, "members");
        }
        else if (modelType == typeof(GroupMember))
        {
            RequireProperties(root, "fingerprint");
        }
        else if (modelType == typeof(GroupFileMetadata))
        {
            RequireProperties(root, "transferID", "fileName", "size");
        }
    }

    private static void ValidateMembersProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement members) || members.ValueKind == JsonValueKind.Null)
            return;
        if (members.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{propertyName} 必须是数组。");
        foreach (JsonElement member in members.EnumerateArray())
            RequireProperties(member, "fingerprint");
    }

    private static void RequireProperties(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("群组 JSON 对象格式无效。");
        foreach (string propertyName in required)
        {
            if (!element.TryGetProperty(propertyName, out _))
                throw new InvalidDataException($"群组 JSON 缺少必需字段：{propertyName}。");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("群组 JSON 包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static bool HasControlCharacter(string value)
        => value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control);

    private sealed class UnixMillisecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long milliseconds))
                throw new JsonException("date 必须是 Unix epoch 毫秒整数。");
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException exception) { throw new JsonException("date 超出支持范围。", exception); }
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}
