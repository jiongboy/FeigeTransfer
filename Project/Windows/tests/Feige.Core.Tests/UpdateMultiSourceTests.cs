using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Feige.Core.Updates;

namespace Feige.Core.Tests;

public sealed class UpdateMultiSourceTests
{
    private const string Lan = "http://192.168.50.229:8080/updates";
    private const string Upstream = "http://10.80.10.188:8080/updates";
    private const string Releases = "https://github.com/jiongboy/FeigeTransfer/releases";
    private const string GitHub = "https://github.com/jiongboy/FeigeTransfer/releases/latest/download";
    private const string AssetHost = "release-assets.githubusercontent.com";

    private sealed class Fixture : IDisposable
    {
        public RSA Key { get; } = RSA.Create(3072);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

        public UpdateSource LanSource => new("stable", [], [], Lan, ["192.168.50.229"], true, "lan");
        public UpdateSource GitHubSource => new("stable", [], [], GitHub, ["github.com"], false, "github",
            RedirectHosts: ["objects.githubusercontent.com", AssetHost], Layout: UpdateSource.GitHubReleaseLayout);
        public UpdateSource UpstreamSource => new("stable", [], [], Upstream, ["10.80.10.188"], true, "lan-upstream");
        public UpdateSources Sources => new("stable", new() { ["release-1"] = PublicKey }, [], [LanSource, GitHubSource]);

        public byte[] Sign(string baseUri, string file = "setup.exe", UpdateRelease[]? releases = null)
        {
            var tag = $"{baseUri}/windows/stable/versions/v1.10.0-b20";
            var release = new UpdateRelease("windows", "x64", "stable", "exe", "1.10.0", 20,
                "2026-10-24T12:00:00Z", "10.0.17763.0", "修复名称同步", $"{tag}/notes", $"{tag}/{file}", 4,
                Convert.ToHexString(SHA256.HashData("test"u8)), 3);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new { schemaVersion = 1, releases = releases ?? [release] }, UpdateManifest.Json);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = 1,
                keyId = "release-1",
                payload = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(Key.SignData(UpdateManifest.SigningBytes("release-1", payload),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
            });
        }
        public void Dispose() => Key.Dispose();
    }

    private static UpdateTarget Target() => new("x64", "stable", "exe", "1.9.0", 19, new Version(10, 0, 26100, 0));

    [Fact] public void MultipleOrigins_AreAcceptedAndResolvedInOrder()
    {
        using var f = new Fixture();
        f.Sources.Validate();
        var origins = f.Sources.Resolve();
        Assert.Equal(["lan", "github"], origins.Select(o => o.Id));
        Assert.All(origins, o => Assert.Single(o.PublicKeys));
    }

    [Fact] public void DuplicateLabels_AreRejected()
    {
        using var f = new Fixture();
        var duplicated = f.Sources with { Sources = [f.LanSource, f.GitHubSource with { Label = "lan" }] };
        Assert.Throws<InvalidDataException>(() => duplicated.Validate());
    }

    [Fact] public void MoreThanFourOrigins_AreRejected()
    {
        using var f = new Fixture();
        var many = f.Sources with { Sources = [f.LanSource, f.GitHubSource, f.LanSource with { Label = "a" },
            f.LanSource with { Label = "b" }, f.LanSource with { Label = "c" }] };
        Assert.Throws<InvalidDataException>(() => many.Validate());
    }

    [Fact] public void LegacySingleOriginJson_IsParsedAndNormalized()
    {
        using var f = new Fixture();
        var legacy = f.LanSource with
        {
            PublicKeys = new() { ["release-1"] = f.PublicKey },
            PublisherCertificateSha256 = [],
            Label = null,
            Hosts = null,
            RedirectHosts = null,
        };
        string json = JsonSerializer.Serialize(new
        {
            legacy.Channel,
            legacy.PublicKeys,
            legacy.PublisherCertificateSha256,
            legacy.BaseUri,
            legacy.TrustedHosts,
            legacy.AllowInsecurePrivateNetwork,
        }, UpdateManifest.Json);

        var parsed = UpdateSources.Parse(json);
        var source = Assert.Single(parsed.Resolve());
        Assert.Equal("stable", parsed.Channel);
        Assert.Equal(["192.168.50.229"], source.Hosts ?? []);
        Assert.Equal(["192.168.50.229"], source.TrustedHosts ?? []);
        Assert.Equal(f.PublicKey, source.PublicKeys["release-1"]);
        Assert.True(source.AllowInsecurePrivateNetwork);
    }

    [Fact] public void MultiSourceJson_IsParsedInConfiguredOrder()
    {
        using var f = new Fixture();
        string json = JsonSerializer.Serialize(f.Sources, UpdateManifest.Json);

        var parsed = UpdateSources.Parse(json);
        Assert.Equal(["lan", "github"], parsed.Resolve().Select(source => source.Id));
    }

    [Fact] public void EmptyMultiSourceConfig_RemainsUnconfigured()
    {
        const string json = """{"channel":"stable","publicKeys":{},"publisherCertificateSha256":[],"sources":[]}""";
        Assert.False(UpdateSources.Parse(json).IsConfigured);
    }

    [Fact] public void InvalidMultiSourceJson_DoesNotFallBackToLegacyFields()
    {
        const string json = """{"channel":"stable","publicKeys":{},"publisherCertificateSha256":[],"sources":[],"baseUri":"https://updates.example","trustedHosts":["updates.example"]}""";
        Assert.Throws<JsonException>(() => UpdateSources.Parse(json));
    }

    [Fact] public void GitHubReleaseUrls_AreAcceptedOnlyOnTheGitHubOrigin()
    {
        using var f = new Fixture();
        var github = f.Sources.Resolve()[1];
        var release = new UpdateRelease("windows", "x64", "stable", "exe", "1.10.0", 20, "2026-10-24T12:00:00Z",
            "10.0.17763.0", "notes", $"{Releases}/tag/v1.10.0", $"{Releases}/download/v1.10.0/FeigeSetup-1.10.0-x64.exe", 4,
            Convert.ToHexString(SHA256.HashData("test"u8)), 3);
        release.Validate(github);
        Assert.Throws<InvalidDataException>(() => release.Validate(f.Sources.Resolve()[0]));
        // GitHub assets are flat: the versions/ tree and mismatched tags do not exist there.
        Assert.Throws<InvalidDataException>(() => (release with
            { DownloadUrl = $"{GitHub}/windows/stable/versions/v1.10.0-b20/x.exe" }).Validate(github));
        Assert.Throws<InvalidDataException>(() => (release with { NotesUrl = $"{Releases}/tag/v1.9.0" }).Validate(github));
        Assert.Throws<InvalidDataException>(() => (release with
            { DownloadUrl = $"https://github.com/other/repo/releases/download/v1.10.0/x.exe" }).Validate(github));
    }

    [Fact] public void GitHubManifest_IsReadFromTheLatestReleaseAssets()
    {
        using var f = new Fixture();
        Assert.Equal($"{GitHub}/feige-windows-update.json", f.Sources.Resolve()[1].ManifestUri.AbsoluteUri);
        Assert.Equal($"{Lan}/windows/stable/feige-windows-update.json", f.Sources.Resolve()[0].ManifestUri.AbsoluteUri);
        // The GitHub layout only works from latest/download, where the newest manifest is always found.
        Assert.Throws<InvalidDataException>(() =>
            (f.Sources with { Sources = [f.GitHubSource with { BaseUri = Releases + "/download/v1.0.0" }] }).Validate());
    }

    [Fact] public void OneSignedLanManifest_IsRebasedOntoEveryOrigin()
    {
        using var f = new Fixture();
        var sources = f.Sources with { Sources = [f.LanSource, f.UpstreamSource, f.GitHubSource] };
        var origins = sources.Resolve();
        byte[] manifest = f.Sign(Lan);

        var lan = Assert.Single(UpdateManifest.Verify(manifest, origins[0]).Releases);
        Assert.Equal($"{Lan}/windows/stable/versions/v1.10.0-b20/setup.exe", lan.DownloadUrl);
        var upstream = Assert.Single(UpdateManifest.Verify(manifest, origins[1]).Releases);
        Assert.Equal($"{Upstream}/windows/stable/versions/v1.10.0-b20/setup.exe", upstream.DownloadUrl);
        Assert.Equal($"{Upstream}/windows/stable/versions/v1.10.0-b20/notes", upstream.NotesUrl);
        var github = Assert.Single(UpdateManifest.Verify(manifest, origins[2]).Releases);
        Assert.Equal($"{Releases}/download/v1.10.0/setup.exe", github.DownloadUrl);
        Assert.Equal($"{Releases}/tag/v1.10.0", github.NotesUrl);
        Assert.Equal(lan.Sha256, github.Sha256);
    }

    [Fact] public void ShippedConfiguration_ListsMappedLanAndGitHub()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Feige.App", "Updates", "UpdateSource.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sources = UpdateSources.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "Feige.App", "Updates", "UpdateSource.json")));
        var origins = sources.Resolve();
        Assert.Equal(["lan", "github"], origins.Select(o => o.Id));
        // The Mac mini is reached through the router's port mapping, which works from both networks.
        Assert.Equal("http://10.80.10.188:8080/updates", origins[0].BaseUri);
        Assert.True(origins[1].IsGitHubRelease);
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/release/x.exe")]
    [InlineData("https://" + AssetHost + "/x.exe")]
    [InlineData("https://" + AssetHost + "/github-production-release-asset/1/e7?sp=r&sig=%2Bdf&rscd=attachment%3B+filename%3Dx.exe")]
    public void ListedReleaseCdn_IsAcceptedAsRedirectTarget(string location)
    {
        using var f = new Fixture();
        f.Sources.Resolve()[1].RequireRedirectUri(new Uri(location));
    }

    [Fact] public void QueryOnTheOriginItself_IsRejected()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => f.Sources.Resolve()[1].RequireRedirectUri(new Uri($"{Releases}/download/v1/x.exe?a=1")));
        Assert.Throws<InvalidDataException>(() => f.Sources.Resolve()[0].RequireRedirectUri(new Uri($"{Lan}/windows/stable/x?a=1")));
    }

    [Theory]
    [InlineData("https://evil.example/x.exe")]
    [InlineData("http://objects.githubusercontent.com/x.exe")]
    [InlineData("https://objects.githubusercontent.com:8443/x.exe")]
    [InlineData("https://user@objects.githubusercontent.com/x.exe")]
    public void UnlistedOrMalformedRedirect_IsRejected(string location)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => f.Sources.Resolve()[1].RequireRedirectUri(new Uri(location)));
        Assert.Throws<InvalidDataException>(() => f.Sources.Resolve()[0].RequireRedirectUri(new Uri(location)));
    }

    [Fact] public async Task Check_FallsBackToNextOriginWhenLanIsUnreachable()
    {
        using var f = new Fixture(); var hosts = new List<string>();
        using var client = new UpdateClient(f.Sources, new Handler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "192.168.50.229"
                ? throw new HttpRequestException("局域网不可达")
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Sign(GitHub)) };
        }));
        var result = await client.CheckAsync(Target());
        Assert.Equal(UpdateCheckKind.Available, result.Selection.Kind);
        Assert.Equal("github", result.Source.Id);
        Assert.Equal(["192.168.50.229", "github.com"], hosts);
    }

    [Fact] public async Task Check_StopsAtFirstOriginThatAnswers()
    {
        using var f = new Fixture(); var hosts = new List<string>();
        using var client = new UpdateClient(f.Sources, new Handler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Sign(Lan)) };
        }));
        var result = await client.CheckAsync(Target());
        Assert.Equal("lan", result.Source.Id);
        Assert.Equal(["192.168.50.229"], hosts);
    }

    [Fact] public async Task Check_DoesNotFallBackWhenTheAnsweredManifestIsInvalid()
    {
        using var f = new Fixture(); int calls = 0;
        using var client = new UpdateClient(f.Sources, new Handler(_ =>
        {
            calls++;
            // A served manifest that fails signature verification must fail closed instead of
            // silently trying the next origin, which could hide a tampered release.
            var bytes = f.Sign(Lan);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([.. bytes[..^8], .. "AA=="u8]) };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(Target()));
        Assert.Equal(1, calls);
    }

    [Fact] public async Task Check_ReportsNotFoundWhenEveryOriginIsUnreachable()
    {
        using var f = new Fixture(); int calls = 0;
        using var client = new UpdateClient(f.Sources, new Handler(_ => { calls++; return new(HttpStatusCode.NotFound); }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync(Target()));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact] public async Task Check_ReportsUnreachableWhenAnyOriginCouldNotBeReached()
    {
        using var f = new Fixture();
        using var client = new UpdateClient(f.Sources, new Handler(request => request.RequestUri!.Host == "192.168.50.229"
            ? throw new HttpRequestException("局域网不可达") : new(HttpStatusCode.NotFound)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync(Target()));
        Assert.NotEqual(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact] public async Task Check_FollowsGitHubRedirectsToTheSignedCdnUrl()
    {
        using var f = new Fixture(); var seen = new List<string>();
        var cdn = $"https://{AssetHost}/github-production-release-asset/1/abc?sp=r&sig=x";
        using var client = new UpdateClient(f.Sources, new Handler(request =>
        {
            var uri = request.RequestUri!; seen.Add(uri.AbsoluteUri);
            if (uri.Host == "192.168.50.229") throw new HttpRequestException("局域网不可达");
            if (uri.AbsoluteUri == $"{GitHub}/feige-windows-update.json")
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri($"{Releases}/download/v1.10.0/feige-windows-update.json") } };
            if (uri.Host == "github.com") return new(HttpStatusCode.Found) { Headers = { Location = new Uri(cdn) } };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Sign(Lan)) };
        }));
        var result = await client.CheckAsync(Target());
        Assert.Equal("github", result.Source.Id);
        Assert.Equal(UpdateCheckKind.Available, result.Selection.Kind);
        Assert.Equal(cdn, seen[^1]);
    }

    [Fact] public async Task DownloadUsesTheOriginThatServedTheManifest()
    {
        using var f = new Fixture(); var seen = new List<string>(); string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        byte[] body = "test"u8.ToArray();
        try
        {
            using var client = new UpdateClient(f.Sources, new Handler(request =>
            {
                seen.Add(request.RequestUri!.Host);
                return request.RequestUri.Host == "192.168.50.229"
                    ? throw new HttpRequestException("局域网不可达")
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".exe") ? body : f.Sign(Lan)) };
            }));
            var result = await client.CheckAsync(Target());
            Assert.Equal("github", result.Source.Id);
            Assert.Equal($"{Releases}/download/v1.10.0/setup.exe", result.Selection.Release!.DownloadUrl);
            var package = await client.DownloadAsync(result.Selection.Release!, directory, new AcceptingVerifier());
            Assert.Equal(body, await File.ReadAllBytesAsync(package.Path));
            // LAN probe, GitHub manifest, then the package download served by the same GitHub origin.
            Assert.Equal(["192.168.50.229", "github.com", "github.com"], seen);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }

    private sealed class AcceptingVerifier : IUpdatePackageVerifier
    { public Task VerifyAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask; }
}
