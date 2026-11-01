using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feige.Core.Conversations;

public enum V4GroupFileKind
{
    File,
    Image
}

/// <summary>Portable file metadata matching macOS FileMetadata's v4 JSON representation.</summary>
public sealed record V4GroupFileMetadata
{
    [JsonPropertyName("transferID")]
    public Guid TransferID { get; }

    [JsonPropertyName("fileName")]
    public string FileName { get; }

    [JsonPropertyName("size")]
    public ulong Size { get; }

    [JsonPropertyName("kind")]
    public V4GroupFileKind Kind { get; }

    [JsonConstructor]
    public V4GroupFileMetadata(Guid transferID, string fileName, ulong size,
        V4GroupFileKind kind = V4GroupFileKind.File)
    {
        if (fileName is null || fileName.Length == 0 || fileName is "." or ".." ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control) ||
            Encoding.UTF8.GetByteCount(fileName) > 255)
            throw new InvalidDataException("文件名无效。");
        if (size > long.MaxValue) throw new InvalidDataException("文件大小超出支持范围。");
        if (kind is not (V4GroupFileKind.File or V4GroupFileKind.Image))
            throw new InvalidDataException("文件类型无效。");

        TransferID = transferID;
        FileName = fileName;
        Size = size;
        Kind = kind;
    }
}

/// <summary>Strict group file manifest matching the current macOS v4 wire schema.</summary>
public sealed record V4GroupFileManifest
{
    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("messageID")]
    public Guid MessageID { get; }

    [JsonPropertyName("senderFingerprint")]
    public string SenderFingerprint { get; }

    [JsonPropertyName("memberRevision")]
    public ulong MemberRevision { get; }

    [JsonPropertyName("metadata")]
    public V4GroupFileMetadata Metadata { get; }

    private readonly byte[] _sha256;

    [JsonPropertyName("sha256")]
    public byte[] Sha256 => _sha256.ToArray();

    [JsonConstructor]
    public V4GroupFileManifest(Guid groupID, Guid messageID, string senderFingerprint,
        ulong memberRevision, V4GroupFileMetadata metadata, byte[] sha256)
    {
        if (senderFingerprint is null || senderFingerprint.Length == 0 ||
            Encoding.UTF8.GetByteCount(senderFingerprint) > 96)
            throw new InvalidDataException("发送者指纹无效。");
        if (metadata is null) throw new InvalidDataException("文件元数据不能为空。");
        if (sha256 is null) throw new InvalidDataException("SHA-256 摘要不能为空。");
        if (sha256.Length != 32) throw new InvalidDataException("SHA-256 摘要必须为 32 字节。");

        GroupID = groupID;
        MessageID = messageID;
        SenderFingerprint = senderFingerprint;
        MemberRevision = memberRevision;
        Metadata = metadata;
        _sha256 = sha256.ToArray();
    }
}

/// <summary>Strict per-recipient group file approval decision.</summary>
public sealed record V4GroupFileDecision
{
    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("transferID")]
    public Guid TransferID { get; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; }

    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; }

    [JsonConstructor]
    public V4GroupFileDecision(Guid groupID, Guid transferID, bool accepted, string? reason = null)
    {
        if (accepted ? reason is not null : string.IsNullOrWhiteSpace(reason))
            throw new InvalidDataException("群组文件决定的原因与 accepted 状态不匹配。");
        if ((reason is not null ? Encoding.UTF8.GetByteCount(reason) : 0) > 256)
            throw new InvalidDataException("群组文件决定原因不能超过 256 字节。");

        GroupID = groupID;
        TransferID = transferID;
        Accepted = accepted;
        Reason = reason;
    }
}

/// <summary>Verified per-recipient group file receipt.</summary>
public sealed record V4GroupFileReceipt
{
    [JsonPropertyName("groupID")]
    public Guid GroupID { get; }

    [JsonPropertyName("transferID")]
    public Guid TransferID { get; }

    private readonly byte[] _sha256;

    [JsonPropertyName("sha256")]
    public byte[] Sha256 => _sha256.ToArray();

    [JsonConstructor]
    public V4GroupFileReceipt(Guid groupID, Guid transferID, byte[] sha256)
    {
        if (sha256 is null) throw new InvalidDataException("SHA-256 摘要不能为空。");
        if (sha256.Length != 32) throw new InvalidDataException("SHA-256 摘要必须为 32 字节。");
        GroupID = groupID;
        TransferID = transferID;
        _sha256 = sha256.ToArray();
    }
}

/// <summary>JSON codec for the macOS v4 group file manifest, decision and receipt schemas.</summary>
public static class V4GroupFileWire
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public static byte[] EncodeManifest(V4GroupFileManifest value) => Encode(value);
    public static byte[] EncodeDecision(V4GroupFileDecision value) => Encode(value);
    public static byte[] EncodeReceipt(V4GroupFileReceipt value) => Encode(value);

    public static V4GroupFileManifest DecodeManifest(ReadOnlySpan<byte> utf8Json)
        => Decode<V4GroupFileManifest>(utf8Json,
            ["groupID", "messageID", "senderFingerprint", "memberRevision", "metadata", "sha256"],
            ["groupID", "messageID", "senderFingerprint", "memberRevision", "metadata", "sha256"]);

    public static V4GroupFileDecision DecodeDecision(ReadOnlySpan<byte> utf8Json)
        => Decode<V4GroupFileDecision>(utf8Json,
            ["groupID", "transferID", "accepted", "reason"],
            ["groupID", "transferID", "accepted"]);

    public static V4GroupFileReceipt DecodeReceipt(ReadOnlySpan<byte> utf8Json)
        => Decode<V4GroupFileReceipt>(utf8Json,
            ["groupID", "transferID", "sha256"], ["groupID", "transferID", "sha256"]);

    private static byte[] Encode<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try { return JsonSerializer.SerializeToUtf8Bytes(value, Json); }
        catch (JsonException exception) { throw new InvalidDataException("群组文件消息无效。", exception); }
    }

    private static T Decode<T>(ReadOnlySpan<byte> utf8Json, string[] allowed, string[] required)
    {
        byte[] jsonBytes = utf8Json.ToArray();
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonBytes);
            JsonElement root = document.RootElement;
            ValidateObject(root, allowed, required);
            RejectDuplicateProperties(root);
            if (typeof(T) == typeof(V4GroupFileManifest))
                ValidateObject(root.GetProperty("metadata"),
                    ["transferID", "fileName", "size", "kind"], ["transferID", "fileName", "size", "kind"]);
            return JsonSerializer.Deserialize<T>(jsonBytes, Json)
                ?? throw new InvalidDataException("群组文件消息不能为 null。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("群组文件 JSON 无效。", exception);
        }
    }

    private static void ValidateObject(JsonElement element, string[] allowed, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("群组文件 JSON 对象格式无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("群组文件 JSON 包含重复字段。");
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("群组文件 JSON 包含未知字段。");
        }
        foreach (string name in required)
            if (!seen.Contains(name)) throw new InvalidDataException($"群组文件 JSON 缺少必需字段：{name}。");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("群组文件 JSON 包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter<V4GroupFileKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
