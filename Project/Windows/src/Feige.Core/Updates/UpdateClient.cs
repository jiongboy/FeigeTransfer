using System.Net;
using System.Security.Cryptography;

namespace Feige.Core.Updates;

public interface IUpdatePackageVerifier
{
    Task VerifyAsync(string path, CancellationToken cancellationToken);
}
public sealed record UpdateDownloadProgress(long Received, long Total);
public sealed record VerifiedUpdatePackage(string Path, UpdateRelease Release);

/// <summary>Anonymous update downloads are independent of all LAN discovery and transfer sockets.</summary>
public sealed class UpdateClient : IDisposable
{
    private readonly UpdateSource _source;
    private readonly HttpClient _http;

    public UpdateClient(UpdateSource source, HttpMessageHandler? handler = null)
    {
        _source = source;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false });
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Feige-Update/1");
    }

    public async Task<UpdateManifest> CheckAsync(CancellationToken cancellationToken = default)
    {
        _source.Validate();
        using var response = await GetAsync(_source.ManifestUri, cancellationToken);
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
        return UpdateManifest.Verify(bytes.ToArray(), _source);
    }

    public async Task<VerifiedUpdatePackage> DownloadAsync(UpdateRelease release, string cacheDirectory,
        IUpdatePackageVerifier verifier, IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        release.Validate(_source);
        ArgumentNullException.ThrowIfNull(verifier);
        Directory.CreateDirectory(cacheDirectory);
        string partial = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".partial");
        string completed = Path.ChangeExtension(partial, release.Installation);
        bool retained = false;
        try
        {
            using var response = await GetAsync(new Uri(release.DownloadUrl), cancellationToken);
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

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        _source.RequireInitialUri(uri);
        for (int redirects = 0; ; redirects++)
        {
            _source.RequireRedirectUri(uri);
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
