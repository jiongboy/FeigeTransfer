using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Feige.Core.Updates;

namespace Feige.Core.Tests;

public sealed class UpdateMultiSourceTests
{
    private const string Lan = "http://192.168.50.229:8080/updates";
    private const string GitHub = "https://github.com/jiongboy/FeigeTransfer/releases/latest/download";
    private const string AssetHost = "release-assets.githubusercontent.com";

    private sealed class Fixture : IDisposable
    {
        public RSA Key { get; } = RSA.Create(3072);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

        public UpdateSource LanSource => new("stable", [], [], Lan, ["192.168.50.229"], true, "lan");
        public UpdateSource GitHubSource => new("stable", [], [], GitHub, ["github.com"], false, "github",
            RedirectHosts: ["objects.githubusercontent.com", AssetHost]);
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

    [Fact] public void LegacySingleOriginFields_StillValidate()
    {
        using var f = new Fixture();
        var legacy = f.LanSource with
        {
            PublicKeys = new() { ["release-1"] = f.PublicKey },
            PublisherCertificateSha256 = [],
            Label = null,
        };
        legacy.Validate();
        var resolved = (f.Sources with { Sources = [legacy] }).Resolve()[0];
        Assert.Equal(["192.168.50.229"], resolved.Hosts);
    }

    [Fact] public void GitHubReleaseUrls_AreAcceptedOnlyOnTheGitHubOrigin()
    {
        using var f = new Fixture();
        var github = f.Sources.Resolve()[1];
        var release = new UpdateRelease("windows", "x64", "stable", "exe", "1.10.0", 20, "2026-10-24T12:00:00Z",
            "10.0.17763.0", "notes", $"{GitHub}/windows/stable/versions/v1.10.0-b20/notes",
            $"{GitHub}/windows/stable/versions/v1.10.0-b20/FeigeSetup-1.10.0-x64.exe", 4,
            Convert.ToHexString(SHA256.HashData("test"u8)), 3);
        release.Validate(github);
        Assert.Throws<InvalidDataException>(() => release.Validate(f.Sources.Resolve()[0]));
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/release/x.exe")]
    [InlineData("https://" + AssetHost + "/x.exe")]
    public void ListedReleaseCdn_IsAcceptedAsRedirectTarget(string location)
    {
        using var f = new Fixture();
        f.Sources.Resolve()[1].RequireRedirectUri(new Uri(location));
    }

    [Theory]
    [InlineData("https://evil.example/x.exe")]
    [InlineData("http://objects.githubusercontent.com/x.exe")]
    [InlineData("https://objects.githubusercontent.com:8443/x.exe")]
    [InlineData("https://objects.githubusercontent.com/x.exe?token=1")]
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
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".exe") ? body : f.Sign(GitHub)) };
            }));
            var result = await client.CheckAsync(Target());
            Assert.Equal("github", result.Source.Id);
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
