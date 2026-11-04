using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Feige.Core.Updates;

/// <summary>
/// A single configured update origin. Every origin carries its own host allowlist, so a signed
/// manifest from one origin can only reference package and notes addresses on that same origin.
/// </summary>
public sealed record UpdateSource(string Channel,
    Dictionary<string, string> PublicKeys, string[] PublisherCertificateSha256,
    string? BaseUri = null, string[]? TrustedHosts = null, bool AllowInsecurePrivateNetwork = false,
    string? Label = null, string[]? Hosts = null, string[]? RedirectHosts = null, string? Layout = null)
{
    /// <summary>GitHub Releases keep assets flat under a tag, so that origin cannot mirror the versions/ tree.</summary>
    public const string GitHubReleaseLayout = "github-release";
    private const string GitHubLatestSuffix = "/releases/latest/download";
    public bool IsGitHubRelease => Layout == GitHubReleaseLayout;

    /// <summary>Stable identifier used for per-origin state and messages.</summary>
    public string Id => string.IsNullOrWhiteSpace(Label) ? BaseUri ?? "unconfigured" : Label!;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUri) && PublicKeys is { Count: > 0 };

    public Uri BaseAddress => new(BaseUri!, UriKind.Absolute);
    public Uri BaseDirectory => new(BaseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    public string NormalizedBasePath => BaseDirectory.AbsolutePath.TrimEnd('/');
    public string ReleaseBasePath => new Uri(BaseDirectory, $"windows/{Channel}/").AbsoluteUri.TrimEnd('/');
    public Uri ManifestUri => IsGitHubRelease ? new(BaseDirectory, "feige-windows-update.json")
        : new(BaseDirectory, $"windows/{Channel}/feige-windows-update.json");
    /// <summary>For the GitHub layout: <c>/owner/repo/releases</c>, under which tags live.</summary>
    private string GitHubReleasesPath => NormalizedBasePath[..^GitHubLatestSuffix.Length] + "/releases";

    /// <summary>
    /// Points a signed release's package and notes at this origin. One signed manifest is published to every
    /// origin, so its absolute addresses name only one of them; the tag and file name select the same content
    /// here, and the size, SHA-256 and publisher pin are still checked after download.
    /// </summary>
    public UpdateRelease Rebase(UpdateRelease release)
    {
        if (!TryVersionsPath(release.DownloadUrl, out var tag, out var file)) return release;
        if (IsGitHubRelease)
        {
            var root = $"{BaseAddress.Scheme}://{BaseAddress.Authority}{GitHubReleasesPath}";
            var githubTag = "v" + release.Version;
            return release with { DownloadUrl = $"{root}/download/{githubTag}/{file}", NotesUrl = $"{root}/tag/{githubTag}" };
        }
        var versions = $"{BaseDirectory.AbsoluteUri}windows/{Channel}/versions/{tag}/";
        return release with { DownloadUrl = versions + file, NotesUrl = versions + "notes" };
    }

    private bool TryVersionsPath(string url, out string tag, out string file)
    {
        tag = file = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length < 5 || parts[^5] != "windows" || parts[^4] != Channel || parts[^3] != "versions") return false;
        tag = parts[^2]; file = parts[^1];
        return Regex.IsMatch(tag, "^[A-Za-z0-9._-]+$") && Regex.IsMatch(file, "^[A-Za-z0-9._-]+$") && tag is not ("." or "..") && file is not ("." or "..");
    }

    /// <summary>Validates a redirect hop: an allowlisted host on this origin, or a listed release CDN.</summary>
    public void RequireRedirectUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri) throw new InvalidDataException("更新地址或重定向不受信任。");
        if (IsThisOrigin(uri))
        {
            if (uri.OriginalString.Contains('?')) throw new InvalidDataException("更新地址或重定向不受信任。");
            RequireOrigin(uri); return;
        }
        // Release CDNs answer only short-lived signed URLs, so their query string is allowed; content is still
        // verified against the signed manifest.
        if (RedirectHosts is not { Length: > 0 } || !RedirectHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.Contains("..", StringComparison.Ordinal) || uri.AbsolutePath.Contains('%'))
            throw new InvalidDataException("更新地址或重定向不受信任。");
    }

    private bool IsThisOrigin(Uri uri) => uri.Scheme.Equals(BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == BaseAddress.Port && string.Equals(uri.IdnHost, BaseAddress.IdnHost, StringComparison.OrdinalIgnoreCase);

    private void RequireOrigin(Uri uri)
    {
        var hosts = Hosts ?? TrustedHosts ?? [];
        var allowed = IsValidOrigin(uri) && hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) &&
            (uri.Scheme != Uri.UriSchemeHttp ||
             AllowInsecurePrivateNetwork && IPAddress.TryParse(uri.Host, out var ip) && IsPrivateAddress(ip));
        if (!uri.IsAbsoluteUri || !allowed) throw new InvalidDataException("更新地址或重定向不受信任。");
    }

    private bool IsAllowedScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttps ||
        AllowInsecurePrivateNetwork && uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var ip) && IsPrivateAddress(ip);
    private static bool IsPrivateAddress(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        (ip.GetAddressBytes()[0] == 10 || ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] is >= 16 and <= 31 ||
         ip.GetAddressBytes()[0] == 192 && ip.GetAddressBytes()[1] == 168 || ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254 ||
         ip.GetAddressBytes()[0] == 127);
    private bool IsAllowedPort(Uri uri) => uri.Scheme == Uri.UriSchemeHttps ? uri.Port == 443 : uri.Port == 8080;
    private bool IsValidOrigin(Uri uri) => IsAllowedScheme(uri) && IsAllowedPort(uri) && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsValidHost(string host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        (IPAddress.TryParse(host, out var ip) ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork : Regex.IsMatch(host, "^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\\.([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?))*$"));

    public void Validate() => Normalize().ValidateCore();

    private void ValidateCore()
    {
        var hosts = Hosts ?? TrustedHosts;
        if (Channel is not ("stable" or "preview") || PublicKeys is null || PublicKeys.Count is < 1 or > 16 ||
            PublicKeys.Keys.Any(key => !Regex.IsMatch(key, "^[A-Za-z0-9_-]{1,64}$")) ||
            PublisherCertificateSha256 is null || PublisherCertificateSha256.Length > 8 ||
            PublisherCertificateSha256.Any(hash => !UpdateManifest.IsHash(hash)) ||
            PublisherCertificateSha256.Distinct(StringComparer.OrdinalIgnoreCase).Count() != PublisherCertificateSha256.Length ||
            !Uri.TryCreate(BaseUri, UriKind.Absolute, out var baseUri) || !baseUri.IsAbsoluteUri || !IsValidOrigin(baseUri) ||
            BaseUri!.Contains('%') || BaseUri!.Contains('\\') ||
            baseUri.AbsolutePath.Contains("..", StringComparison.Ordinal) || hosts is null || hosts.Length is < 1 or > 8 ||
            hosts.Any(host => !IsValidHost(host)) || !hosts.Contains(baseUri.IdnHost, StringComparer.OrdinalIgnoreCase) ||
            RedirectHosts is { Length: > 8 } || RedirectHosts?.Any(host => !IsValidHost(host)) == true ||
            baseUri.Scheme == Uri.UriSchemeHttp && (!AllowInsecurePrivateNetwork || !IPAddress.TryParse(baseUri.Host, out var baseIp) || !IsPrivateAddress(baseIp)) ||
            Layout is not (null or GitHubReleaseLayout) ||
            IsGitHubRelease && (baseUri.Scheme != Uri.UriSchemeHttps || !NormalizedBasePath.EndsWith(GitHubLatestSuffix, StringComparison.Ordinal) ||
                NormalizedBasePath.Count(c => c == '/') != 5))
            throw new InvalidDataException("更新来源尚未正确配置。");
    }

    public void RequireInitialUri(Uri uri, bool notes = false)
    {
        RequireOrigin(uri);
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("更新地址不属于配置的受信任服务。");
        var prefix = $"{NormalizedBasePath}/windows/{Channel}/";
        if (uri.AbsolutePath == ManifestUri.AbsolutePath) return;
        if (IsGitHubRelease) { RequireGitHubReleaseUri(uri, notes); return; }
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("更新地址不属于配置的发布渠道。");
        var parts = uri.AbsolutePath[prefix.Length..].Split('/');
        if (parts.Length != 3 || parts[0] != "versions" ||
            parts.Skip(1).Any(p => p is "." or ".." || !Regex.IsMatch(p, "^[A-Za-z0-9._-]+$")))
            throw new InvalidDataException("更新地址格式无效。");
        if (notes && parts[2] != "notes") throw new InvalidDataException("更新说明地址格式无效。");
        if (!notes && (parts[2] == "notes" || !parts[2].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新包地址格式无效。");
    }

    /// <summary>Package: <c>/owner/repo/releases/download/tag/file.exe</c>; notes: <c>/owner/repo/releases/tag/tag</c>.</summary>
    private void RequireGitHubReleaseUri(Uri uri, bool notes)
    {
        var prefix = GitHubReleasesPath + "/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("更新地址不属于配置的发布渠道。");
        var parts = uri.AbsolutePath[prefix.Length..].Split('/');
        if (parts.Any(p => p is "." or ".." || !Regex.IsMatch(p, "^[A-Za-z0-9._-]+$"))) throw new InvalidDataException("更新地址格式无效。");
        if (notes ? parts is not ["tag", _] : parts is not ["download", _, var file] || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(notes ? "更新说明地址格式无效。" : "更新包地址格式无效。");
    }

    /// <summary>The release tag a validated package or notes address belongs to.</summary>
    public string ReleaseTag(Uri uri, bool notes)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return IsGitHubRelease && notes ? parts[^1] : parts[^2];
    }

    /// <summary>Copies legacy single-origin fields onto the current shape.</summary>
    public UpdateSource Normalize() => this with
    {
        Hosts = Hosts ?? TrustedHosts?.ToArray() ?? [],
        TrustedHosts = TrustedHosts ?? Hosts?.ToArray() ?? [],
        RedirectHosts = RedirectHosts ?? [],
    };
}

/// <summary>
/// Ordered list of update origins. Origins are tried in order; the first one that returns a valid
/// signed manifest wins, and an unreachable or incomplete origin falls through to the next.
/// </summary>
public sealed record UpdateSources(string Channel, Dictionary<string, string> PublicKeys,
    string[] PublisherCertificateSha256, UpdateSource[] Sources)
{
    /// <summary>Parses current multi-source or legacy single-source JSON, normalizing legacy fields.</summary>
    public static UpdateSources Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("更新来源必须是 JSON 对象。");

        if (document.RootElement.TryGetProperty("sources", out _))
        {
            var current = JsonSerializer.Deserialize<UpdateSources>(json, UpdateManifest.Json)
                ?? throw new InvalidDataException("更新来源为空。");
            return current;
        }

        var legacy = JsonSerializer.Deserialize<UpdateSource>(json, UpdateManifest.Json)
            ?? throw new InvalidDataException("更新来源为空。");
        var normalized = legacy.Normalize();
        return new UpdateSources(normalized.Channel, normalized.PublicKeys,
            normalized.PublisherCertificateSha256, [normalized]);
    }

    public bool IsConfigured => Sources is { Length: > 0 } && PublicKeys is { Count: > 0 };

    /// <summary>Validated origins with the shared channel, keys and pins applied.</summary>
    public IReadOnlyList<UpdateSource> Resolve()
    {
        Validate();
        var resolved = new List<UpdateSource>(Sources.Length);
        foreach (var source in Sources)
            resolved.Add(Complete(source));
        return resolved;
    }

    /// <summary>Applies the shared channel, keys and pins, then fills legacy single-origin fields.</summary>
    internal UpdateSource Complete(UpdateSource source) => (source with
    {
        Channel = Channel,
        PublicKeys = PublicKeys,
        PublisherCertificateSha256 = PublisherCertificateSha256,
    }).Normalize();

    public void Validate()
    {
        if (Channel is not ("stable" or "preview")) throw new InvalidDataException("更新渠道无效。");
        if (PublicKeys is null || PublicKeys.Count is < 1 or > 16) throw new InvalidDataException("更新签名公钥尚未配置。");
        if (PublisherCertificateSha256 is null || PublisherCertificateSha256.Length > 8) throw new InvalidDataException("更新发布者指纹尚未配置。");
        if (Sources is null || Sources.Length is < 1 or > 4) throw new InvalidDataException("更新来源尚未正确配置。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            Complete(source).Validate();
            if (!ids.Add(source.Id)) throw new InvalidDataException("更新来源标识重复。");
        }
    }
}

