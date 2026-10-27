using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feige.Core.Updates;

namespace Feige.Core.Tests;

public sealed class UpdateTests
{
    private sealed class Fixture : IDisposable
    {
        public RSA Key { get; } = RSA.Create(3072);
        public UpdateSource Source => new("stable",
            new() { ["release-1"] = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()) }, [],
            "http://192.168.50.229:8080/updates", ["192.168.50.229"], true);
        public byte[] Sign(UpdateRelease[]? releases = null, byte[]? raw = null)
        {
            byte[] payload = raw ?? JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, releases = releases ?? [Release()] }, UpdateManifest.Json);
            return JsonSerializer.SerializeToUtf8Bytes(new { format = 1, keyId = "release-1", payload = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(Key.SignData(UpdateManifest.SigningBytes("release-1", payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
        }
        public void Dispose() => Key.Dispose();
    }
    private static UpdateRelease Release() => new("windows", "x64", "stable", "exe", "1.10.0", 20,
        "2026-10-24T12:00:00Z", "10.0.17763.0", "修复名称同步", "http://192.168.50.229:8080/updates/windows/stable/versions/v1.10.0-b20/notes",
        "http://192.168.50.229:8080/updates/windows/stable/versions/v1.10.0-b20/setup.exe", 4, Convert.ToHexString(SHA256.HashData("test"u8)), 3);
    private static UpdateTarget Target() => new("x64", "stable", "exe", "1.9.0", 19, new Version(10, 0, 26100, 0));

    [Fact] public void PublisherPins_AreOptionalForManifestCheckButMustBeValid()
    {
        using var f = new Fixture();
        var manifest = UpdateManifest.Verify(f.Sign(), f.Source with { PublisherCertificateSha256 = [] });
        Assert.Single(manifest.Releases);
        Assert.Throws<InvalidDataException>(() => (f.Source with { PublisherCertificateSha256 = [new string('A', 64), new string('a', 64)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (f.Source with { PublisherCertificateSha256 = ["bad"] }).Validate());
    }

    [Fact] public void SignedManifest_SelectsByNumericVersionAndBuild()
    {
        using var f = new Fixture();
        var manifest = UpdateManifest.Verify(f.Sign(), f.Source);
        Assert.Equal(UpdateCheckKind.Available, manifest.Select(Target(), 0).Kind);
        Assert.Equal(UpdateCheckKind.Available, manifest.Select(Target() with { Version = "1.10.0" }, 0).Kind);
        Assert.Equal(UpdateCheckKind.UpToDate, manifest.Select(Target() with { Build = 20 }, 20).Kind);
        Assert.Equal(UpdateCheckKind.UpToDate, manifest.Select(Target() with { Version = "1.11.0" }, 0).Kind);
        Assert.Throws<InvalidDataException>(() => manifest.Select(Target(), 21));
    }

    [Theory]
    [InlineData("arm64")] [InlineData("preview")] [InlineData("msix")] [InlineData("old-system")] [InlineData("protocol")]
    public void IncompatibleTarget_IsNotReportedAsLatest(string mismatch)
    {
        using var f = new Fixture(); var target = Target();
        target = mismatch switch {
            "arm64" => target with { Architecture = "arm64" }, "preview" => target with { Channel = "preview" },
            "msix" => target with { Installation = "msix" }, "old-system" => target with { SystemVersion = new Version(10,0,10000,0) },
            _ => target with { ProtocolVersion = 4 } };
        Assert.Equal(UpdateCheckKind.NoCompatibleUpdate, UpdateManifest.Verify(f.Sign(), f.Source).Select(target, 0).Kind);
    }

    [Theory]
    [InlineData("payload")] [InlineData("signature")] [InlineData("keyId")]
    public void AlteredSignedEnvelope_IsRejected(string field)
    {
        using var f = new Fixture();
        var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.Sign())!;
        var altered = envelope.ToDictionary(row => row.Key, row => (object)row.Value);
        altered[field] = field == "keyId" ? "attacker" : Convert.ToBase64String("tampered"u8);
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(JsonSerializer.SerializeToUtf8Bytes(altered), f.Source));
    }

    [Fact] public void DuplicateUnknownAndUnsupportedFields_AreRejected()
    {
        using var f = new Fixture(); var bytes = f.Sign(); string envelope = Encoding.UTF8.GetString(bytes);
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(Encoding.UTF8.GetBytes(envelope.Replace("{", "{\"format\":1,", StringComparison.Ordinal)), f.Source));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(Encoding.UTF8.GetBytes(envelope.Replace("{", "{\"extra\":1,", StringComparison.Ordinal)), f.Source));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(f.Sign(raw: "{\"schemaVersion\":1,\"schemaVersion\":1,\"releases\":[]}"u8.ToArray()), f.Source));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(f.Sign(raw: "{\"schemaVersion\":2,\"releases\":[]}"u8.ToArray()), f.Source));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(f.Sign([Release(), Release()]), f.Source));
    }

    [Theory]
    [InlineData("http://192.168.50.229:8080/updates/windows/stable/versions/v1/setup.exe?token=secret")]
    [InlineData("http://192.168.50.230:8080/updates/windows/stable/versions/v1/setup.exe")]
    [InlineData("https://192.168.50.229:8080/updates/windows/stable/versions/v1/setup.exe")]
    [InlineData("http://192.168.50.229:8081/updates/windows/stable/versions/v1/setup.exe")]
    [InlineData("http://192.168.50.229:8080/updates/windows/stable/versions/v1/setup.exe?token=x")]
    [InlineData("http://user@192.168.50.229:8080/updates/windows/stable/versions/v1/setup.exe")]
    [InlineData("http://192.168.50.229:8080/updates/windows/preview/versions/v1/setup.exe")]
    [InlineData("http://192.168.50.229:8080/updates/windows/stable/versions/v1/../setup.exe")]
    [InlineData("https://evil.example/updates/windows/stable/versions/v1/setup.exe")]
    public void SignedButUntrustedPackageAddress_IsRejected(string url)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(f.Sign([Release() with { DownloadUrl = url }]), f.Source));
    }

    [Fact] public void HttpSourceRequiresExplicitPrivateNetworkFlagAndRejectsPublicAddresses()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => (f.Source with { AllowInsecurePrivateNetwork = false }).Validate());
        Assert.Throws<InvalidDataException>(() => (f.Source with { BaseUri = "http://8.8.8.8:8080/updates", TrustedHosts = ["8.8.8.8"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (f.Source with { BaseUri = "http://updates.example:8080/updates", TrustedHosts = ["updates.example"] }).Validate());
        Assert.Throws<InvalidDataException>(() => (f.Source with { BaseUri = "http://192.168.50.229:9090/updates" }).Validate());
    }

    [Theory]
    [InlineData("http://192.168.50.229:8080/upd%61tes")]
    [InlineData("http://192.168.50.229:8080/updates/%2e%2e/escape")]
    public void PercentEncodedOrTraversingBasePath_IsRejected(string baseUri)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => (f.Source with { BaseUri = baseUri }).Validate());
    }

    [Fact] public void SignedManifestCannotReferToAlternateConfiguredHost()
    {
        using var f = new Fixture();
        var source = f.Source with { TrustedHosts = ["192.168.50.229", "192.168.50.230"] };
        var malicious = Release() with { DownloadUrl = "http://192.168.50.230:8080/updates/windows/stable/versions/v1.10.0-b20/setup.exe" };
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(f.Sign([malicious]), source));
    }

    [Fact] public async Task Check_FollowsOnlyConfiguredLanRedirectsAndSendsNoCredentialsOrDeviceData()
    {
        using var f = new Fixture(); var requests = new List<string>();
        using var client = new UpdateClient(f.Source, new Handler(request => {
            requests.Add(request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization); Assert.Null(request.RequestUri.Query is "" ? null : request.RequestUri.Query);
            Assert.Equal("Feige-Update/1", request.Headers.UserAgent.ToString());
            return requests.Count == 1 ? Redirect("http://192.168.50.229:8080/updates/redirected") : new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Sign()) };
        }));
        Assert.Equal(UpdateCheckKind.Available, (await client.CheckAsync()).Select(Target(), 0).Kind);
        Assert.Equal(new[] { "192.168.50.229", "192.168.50.229" }, requests);
    }

    [Theory]
    [InlineData("http://192.168.50.230:8080/unsafe")]
    [InlineData("https://192.168.50.229:8080/package")]
    [InlineData("http://8.8.8.8:8080/package")]
    [InlineData("http://192.168.50.229:8081/package")]
    [InlineData("http://192.168.50.229:8080/unsafe#fragment")]
    public async Task UnsafeRedirect_IsRejectedBeforeContactingDestination(string location)
    {
        using var f = new Fixture(); int requests = 0;
        using var client = new UpdateClient(f.Source, new Handler(_ => { requests++; return Redirect(location); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync()); Assert.Equal(1, requests);
    }

    [Fact] public async Task HttpErrorsAndOversizedManifest_AreFailures()
    {
        using var f = new Fixture();
        using var missing = new UpdateClient(f.Source, new Handler(_ => new(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => missing.CheckAsync());
        using var oversized = new UpdateClient(f.Source, new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UpdateManifest.MaximumEnvelopeBytes + 1]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => oversized.CheckAsync());
    }

    [Theory]
    [InlineData("hash")] [InlineData("size")] [InlineData("publisher")]
    public async Task FailedDownloadVerification_LeavesNoInstallableFile(string failure)
    {
        using var f = new Fixture(); string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var client = new UpdateClient(f.Source, new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent("test"u8.ToArray()) }));
        var release = failure == "hash" ? Release() with { Sha256 = new string('0', 64) } : failure == "size" ? Release() with { Size = 5 } : Release();
        var verifier = new Verifier(failure == "publisher");
        try {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, directory, verifier));
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(failure == "publisher" ? 1 : 0, verifier.Calls);
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public async Task VerifiedDownload_UsesRealBytesAndPublisherVerification()
    {
        using var f = new Fixture(); string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var client = new UpdateClient(f.Source, new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent("test"u8.ToArray()) }));
        var verifier = new Verifier(false); var reports = new List<UpdateDownloadProgress>();
        try {
            var package = await client.DownloadAsync(Release(), directory, verifier, new ProgressSink(reports));
            Assert.Equal("test", await File.ReadAllTextAsync(package.Path)); Assert.Equal(1, verifier.Calls);
            Assert.Equal(new UpdateDownloadProgress(4, 4), reports.Last());
            Assert.Single(Directory.GetFiles(directory));
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public void AttemptAndSkipState_SurviveRestartAndEnforce24HoursAfterFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); var now = DateTimeOffset.UtcNow;
        try {
            var file = new UpdateStateFile(directory);
            var value = new UpdateScopeState(LastAttempt: now, HighestSeen: 20, SkippedBuild: 20, LastResult: "检查失败");
            file.Save("lan/windows/x64/stable/exe", value);
            var restored = new UpdateStateFile(directory).Get("lan/windows/x64/stable/exe");
            Assert.False(restored.IsDue(now.AddHours(23))); Assert.True(restored.IsDue(now.AddHours(24)));
            Assert.False(restored.ShouldPrompt(20, now)); Assert.True(restored.ShouldPrompt(21, now));
            Assert.Equal(0, new UpdateStateFile(directory).Get("other/windows/x64/stable/exe").HighestSeen);
            File.WriteAllText(Path.Combine(directory, "update-state.json"), "{\"version\":2,\"scopes\":{}}");
            Assert.Throws<InvalidDataException>(() => new UpdateStateFile(directory));
            Assert.Contains("\"version\":2", File.ReadAllText(Path.Combine(directory, "update-state.json")));
        } finally { Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    private sealed class Verifier(bool fail) : IUpdatePackageVerifier
    {
        public int Calls;
        public Task VerifyAsync(string path, CancellationToken ct) { Calls++; if (fail) throw new InvalidDataException("发布者不匹配"); return Task.CompletedTask; }
    }
    private sealed class ProgressSink(List<UpdateDownloadProgress> reports) : IProgress<UpdateDownloadProgress>
    { public void Report(UpdateDownloadProgress value) => reports.Add(value); }
}
