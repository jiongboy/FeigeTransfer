using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feige.Core.Updates;

// Read-only update pipeline probe: fetch signed manifest, select a release, download it,
// then run the same pinned-Authenticode verifier the app uses. Never installs anything.
// Usage: Feige.UpdateProbe <source.json> <targetVersion> <targetBuild> [downloadDir]

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Feige.UpdateProbe <source.json> <targetVersion> <targetBuild> [downloadDir]");
    return 2;
}

string sourcePath = Path.GetFullPath(args[0]);
string targetVersion = args[1];
long targetBuild = long.Parse(args[2]);
string downloadDir = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(Path.GetTempPath(), "feige-update-probe");

byte[] sourceBytes = File.ReadAllBytes(sourcePath);
if (sourceBytes.Length >= 3 && sourceBytes[0] == 0xEF && sourceBytes[1] == 0xBB && sourceBytes[2] == 0xBF)
    sourceBytes = sourceBytes[3..];
// The probe exercises the same ordered-source fallback the app uses.
var sources = JsonSerializer.Deserialize<UpdateSources>(sourceBytes, UpdateManifest.Json)
    ?? throw new InvalidDataException("source config empty");
sources.Validate();
Console.WriteLine($"sources: {string.Join(", ", sources.Sources.Select(s => s.Label ?? s.BaseUri))} channel={sources.Channel}");

using var client = new UpdateClient(sources);
var check = await client.CheckAsync(new UpdateTarget("x64", sources.Channel, "exe", targetVersion, targetBuild, Environment.OSVersion.Version));
var manifest = check.Manifest;
Console.WriteLine($"source: {check.Source.Id} {check.Source.BaseUri}");
Console.WriteLine($"manifest: {manifest.Releases.Count} release(s)");

var selection = check.Selection;
Console.WriteLine($"selection: {selection.Kind} build={selection.Release?.Build} version={selection.Release?.Version}");
if (selection.Kind != UpdateCheckKind.Available)
{
    Console.Error.WriteLine("EXPECTED_AVAILABLE_BUT_GOT_" + selection.Kind);
    return 3;
}

Directory.CreateDirectory(downloadDir);
var verifier = new PinnedAuthenticodeVerifier(Path.Combine(AppContext.BaseDirectory, "VerifyPinnedAuthenticode.ps1"), sources.PublisherCertificateSha256);
var package = await client.DownloadAsync(selection.Release!, downloadDir, verifier, new Progress<UpdateDownloadProgress>(p => { }));
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package.Path)));
Console.WriteLine($"downloaded: {package.Path}");
Console.WriteLine($"size={new FileInfo(package.Path).Length} sha256={hash}");

if (hash != selection.Release!.Sha256) { Console.Error.WriteLine("HASH_MISMATCH"); return 1; }
Console.WriteLine("UPDATE_PIPELINE_OK");
return 0;

sealed class PinnedAuthenticodeVerifier(string scriptPath, string[] pins) : IUpdatePackageVerifier
{
    public async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath)) throw new InvalidDataException("verifier script missing: " + scriptPath);
        // PowerShell blocks dot-sourcing from a UNC share, so use a local copy.
        string localScript = Path.Combine(Path.GetTempPath(), "feige-probe-verifier.ps1");
        File.Copy(scriptPath, localScript, overwrite: true);
        scriptPath = localScript;
        string script = "& { param($p,$s,$pins) . $s; Test-FeigePinnedAuthenticode -Path $p -Pins $pins | Out-Null; Write-Output 'VERIFIED' } " +
                        $"'{Escape(path)}' '{Escape(scriptPath)}' @({string.Join(',', pins.Select(pin => "'" + Escape(pin) + "'"))})";
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(psi) ?? throw new IOException("cannot start PowerShell verifier");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await stdout;
        string errors = await stderr;
        bool verified = output.Split('\n').Any(line => line.Trim() == "VERIFIED");
        // PowerShell writes module-progress CLIXML to stderr, so only exit code and the
        // exact VERIFIED marker decide the result (mirrors the app verifier).
        if (process.ExitCode != 0 || !verified)
            throw new InvalidDataException("pinned Authenticode verification failed: " + errors.Trim() + output.Trim());
    }
    static string Escape(string value) => value.Replace("'", "''");
}
