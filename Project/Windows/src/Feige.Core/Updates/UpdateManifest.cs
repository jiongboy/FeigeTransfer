using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Feige.Core.Updates;

public sealed record UpdateRelease(
    [property: JsonRequired] string Platform, [property: JsonRequired] string Architecture,
    [property: JsonRequired] string Channel, [property: JsonRequired] string Installation,
    [property: JsonRequired] string Version, [property: JsonRequired] long Build,
    [property: JsonRequired] string PublishedAt, [property: JsonRequired] string MinimumSystemVersion,
    [property: JsonRequired] string Summary, [property: JsonRequired] string NotesUrl,
    [property: JsonRequired] string DownloadUrl, [property: JsonRequired] long Size,
    [property: JsonRequired] string Sha256, [property: JsonRequired] int ProtocolVersion)
{
    public void Validate(UpdateSource source)
    {
        if (Platform != "windows" || Architecture is not ("x64" or "arm64") || Channel is not ("stable" or "preview") ||
            Installation is not ("exe" or "msix") || Build <= 0 || ProtocolVersion <= 0 || Size is <= 0 or > 8L * 1024 * 1024 * 1024 ||
            !UpdateManifest.IsHash(Sha256) || Summary is null || Summary.Length > 12000 ||
            Summary.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ||
            PublishedAt is null || !Regex.IsMatch(PublishedAt, "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$") ||
            !DateTimeOffset.TryParse(PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !Regex.IsMatch(MinimumSystemVersion ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$") ||
            !System.Version.TryParse(MinimumSystemVersion, out _)) throw new InvalidDataException("更新条目格式无效。");
        _ = UpdateManifest.ParseVersion(Version);
        if (!Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var download) || !Uri.TryCreate(NotesUrl, UriKind.Absolute, out var notes))
            throw new InvalidDataException("更新地址无效。");
        source.RequireInitialUri(download); source.RequireInitialUri(notes, notes: true);
        var downloadParts = download.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var notesParts = notes.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!StringComparer.OrdinalIgnoreCase.Equals(download.Authority, notes.Authority) ||
            downloadParts.Length != notesParts.Length || downloadParts.Length < 2 || downloadParts[^2] != notesParts[^2])
            throw new InvalidDataException("安装包与说明的发布标签不一致。");
    }
}

public sealed record UpdateTarget(string Architecture, string Channel, string Installation, string Version, long Build,
    Version SystemVersion, int ProtocolVersion = 3)
{
    public string Scope => $"windows/{Architecture}/{Channel}/{Installation}";
}
public enum UpdateCheckKind { Available, UpToDate, NoCompatibleUpdate }
public sealed record UpdateSelection(UpdateCheckKind Kind, UpdateRelease? Release, long HighestCompatibleBuild = 0);

public sealed class UpdateManifest
{
    public const int MaximumEnvelopeBytes = 256 * 1024;
    public const int MaximumPayloadBytes = 128 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };
    private sealed record Envelope([property: JsonRequired] int Format, [property: JsonRequired] string KeyId,
        [property: JsonRequired] string Payload, [property: JsonRequired] string Signature);
    private sealed record Payload([property: JsonRequired] int SchemaVersion, [property: JsonRequired] UpdateRelease[] Releases);
    private UpdateManifest(UpdateRelease[] releases) => Releases = releases;
    public IReadOnlyList<UpdateRelease> Releases { get; }

    public static byte[] SigningBytes(string keyId, ReadOnlySpan<byte> payload)
        => [.. Encoding.UTF8.GetBytes($"Feige.Windows.Update.v1\n{keyId}\n"), .. payload];

    public static UpdateManifest Verify(ReadOnlyMemory<byte> bytes, UpdateSource source)
    {
        source.Validate();
        if (bytes.Length is 0 or > MaximumEnvelopeBytes) throw new InvalidDataException("更新清单大小无效。");
        try
        {
            RejectDuplicateProperties(bytes);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes.Span, Json) ?? throw new InvalidDataException("更新清单为空。");
            if (envelope.Format != 1 || envelope.KeyId is null || !source.PublicKeys.TryGetValue(envelope.KeyId, out var publicKey))
                throw new InvalidDataException("更新清单格式或签名密钥不受信任。");
            byte[] payload = Convert.FromBase64String(envelope.Payload);
            if (payload.Length is 0 or > MaximumPayloadBytes) throw new InvalidDataException("更新内容大小无效。");
            using var rsa = RSA.Create();
            byte[] key = Convert.FromBase64String(publicKey);
            rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
            if (consumed != key.Length || rsa.KeySize < 3072 || !rsa.VerifyData(SigningBytes(envelope.KeyId, payload),
                Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("更新清单签名验证失败。");
            RejectDuplicateProperties(payload);
            var document = JsonSerializer.Deserialize<Payload>(payload, Json) ?? throw new InvalidDataException("更新内容为空。");
            if (document.SchemaVersion != 1 || document.Releases is null || document.Releases.Length > 64)
                throw new InvalidDataException("更新内容格式不受支持。");
            var scopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in document.Releases)
            {
                if (release is null) throw new InvalidDataException("更新条目为空。");
                release.Validate(source);
                if (!scopes.Add($"{release.Platform}/{release.Architecture}/{release.Channel}/{release.Installation}/{release.Build}"))
                    throw new InvalidDataException("更新构建号重复。");
            }
            return new(document.Releases);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("更新清单格式或签名验证失败。", ex); }
    }

    public UpdateSelection Select(UpdateTarget target, long highestSeen)
    {
        var candidates = Releases.Where(r => r.Architecture == target.Architecture && r.Channel == target.Channel &&
            r.Installation == target.Installation && r.ProtocolVersion == target.ProtocolVersion &&
            System.Version.Parse(r.MinimumSystemVersion) <= target.SystemVersion).ToArray();
        if (candidates.Length == 0) return new(UpdateCheckKind.NoCompatibleUpdate, null);
        var latest = candidates.OrderByDescending(r => r.Build).First();
        if (latest.Build < highestSeen) throw new InvalidDataException("发布信息早于本机已验证的版本，请稍后重试。");
        var current = ParseVersion(target.Version);
        if (ParseVersion(latest.Version) < current || latest.Build <= target.Build)
            return new(UpdateCheckKind.UpToDate, null, latest.Build);
        return new(UpdateCheckKind.Available, latest, latest.Build);
    }

    public static Version ParseVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$") ||
            !System.Version.TryParse(value, out var version)) throw new InvalidDataException("产品版本格式无效。");
        return version;
    }
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    public static void RejectDuplicateProperties(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        Walk(document.RootElement);
        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) throw new InvalidDataException("更新清单包含重复字段。");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Walk(item);
        }
    }
}
