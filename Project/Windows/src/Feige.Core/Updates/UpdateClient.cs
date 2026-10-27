using System.Net;
using System.Security.Cryptography;

namespace Feige.Core.Updates;

public interface IUpdatePackageVerifier
{
    Task VerifyAsync(string path, CancellationToken cancellationToken);
}
public sealed record UpdateDownloadProgress(long Received, long Total);
public sealed record VerifiedUpdatePackage(string Path, UpdateRelease Release);

/// <summary>Result of a successful check, including the origin that served the manifest.</summary>
public sealed record UpdateCheckResult(UpdateManifest Manifest, UpdateSource Source, UpdateSelection Selection);

/// <summary>Anonymous update downloads are independent of all LAN discovery and transfer sockets.</summary>
public sealed class UpdateClient : IDisposable
{
    private readonly UpdateSources _sources;
    private readonly IReadOnlyList<UpdateSource> _origins;
    private UpdateSource? _active;
    private readonly HttpClient _http;

    /// <summary>Creates a client that tries each configured origin in order until one answers.</summary>
    public UpdateClient(UpdateSources sources, HttpMessageHandler? handler = null)
    {
        _sources = sources;
        _origins = sources.Resolve();
        _http = CreateHttp(handler);
    }

    /// <summary>Creates a client bound to a single origin, kept for callers that already picked one.</summary>
    public UpdateClient(UpdateSource source, HttpMessageHandler? handler = null)
        : this(new UpdateSources(source.Channel, source.PublicKeys, source.PublisherCertificateSha256, [source]), handler) { }

    private static HttpClient CreateHttp(HttpMessageHandler? handler)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false });
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Feige-Update/1");
        return http;
    }

    public IReadOnlyList<UpdateSource> Origins => _origins;
    public UpdateSource? ActiveSource => _active;

    /// <summary>
    /// Tries each origin in order and returns the first signed manifest. Transport failures and missing
    /// releases fall through to the next origin; a present but invalid manifest fails closed instead,
    /// because silently falling back could hide a tampered or misconfigured release.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(UpdateTarget target, long highestSeen = 0,
        CancellationToken cancellationToken = default)
    {
        _sources.Validate();
        Exception? lastFallback = null;
        foreach (var origin in _origins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await FetchManifestAsync(origin, cancellationToken);
                _active = origin;
                return new(manifest, origin, manifest.Select(target, highestSeen));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                lastFallback = ex;
            }
        }
        _active = null;
        throw lastFallback ?? new HttpRequestException("没有可用的更新来源。", null, HttpStatusCode.NotFound);
    }

    /// <summary>Fetch and verify one manifest from one origin without any fallback.</summary>
    public async Task<UpdateManifest> CheckAsync(CancellationToken cancellationToken = default)
        => await FetchManifestAsync(_origins[0], cancellationToken);

    private async Task<UpdateManifest> FetchManifestAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        source.Validate();
        using var response = await GetAsync(source, source.ManifestUri, cancellationToken);
        if (response.Content.Headers.ContentLength > UpdateManifest.MaximumEnvelopeBytes)
            throw new InvalidDataException("更新清单超出大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > UpdateManifest.MaximumEnvelopeBytes) throw new InvalidDataException("更新清单超出大小限制。");
            bytes.Write(buffer, 0, read);
        }
        return UpdateManifest.Verify(bytes.ToArray(), source);
    }

    public async Task<VerifiedUpdatePackage> DownloadAsync(UpdateRelease release, string cacheDirectory,
        IUpdatePackageVerifier verifier, IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = _active ?? _origins[0];
        release.Validate(source);
        ArgumentNullException.ThrowIfNull(verifier);
        Directory.CreateDirectory(cacheDirectory);
        string partial = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".partial");
        string completed = Path.ChangeExtension(partial, release.Installation);
        bool retained = false;
        try
        {
            using var response = await GetAsync(source, new Uri(release.DownloadUrl), cancellationToken);
            if (response.Content.Headers.ContentLength is { } declared && declared != release.Size)
                throw new InvalidDataException("更新包大小与发布信息不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536]; long received = 0;
                progress?.Report(new(0, release.Size));
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (read > release.Size - received) throw new InvalidDataException("更新包超出声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read); received += read;
                    progress?.Report(new(received, release.Size));
                }
                if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("更新包大小或 SHA-256 校验失败。");
                await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await verifier.VerifyAsync(partial, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, completed);
            retained = true;
            return new(completed, release);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (!retained && File.Exists(completed)) File.Delete(completed);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(UpdateSource source, Uri uri, CancellationToken ct)
    {
        source.RequireInitialUri(uri);
        for (int redirects = 0; ; redirects++)
        {
            source.RequireRedirectUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (redirects >= 5 || location is null) throw new InvalidDataException("更新重定向次数或目标无效。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new HttpRequestException(status == HttpStatusCode.NotFound ? "尚无可用的更新发布信息。" : "更新服务暂不可用。", null, status);
            }
            return response;
        }
    }
    public void Dispose() => _http.Dispose();
}
