using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Feige.Core.Updates;

namespace Feige.App.Services;

public sealed class WindowsPackageVerifier(string[] publishers) : IUpdatePackageVerifier
{
    public async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        if (publishers.Length == 0 || publishers.Length > 8 || publishers.Any(pin => !UpdateManifest.IsHash(pin)))
            throw new InvalidOperationException("未配置有效的更新发布者指纹；不能下载或安装更新。");
        string verifierPath = Path.Combine(AppContext.BaseDirectory, "Updates", "VerifyPinnedAuthenticode.ps1");
        string data = JsonSerializer.Serialize(new { path, publishers = publishers.Select(p => p.ToUpperInvariant()).ToArray(), verifierPath });
        string script = WindowsUpdateProcess.DecodeData(data, "$data") +
            ". (Resolve-Path -LiteralPath $data.verifierPath).Path`n" +
            "$result=Test-FeigePinnedAuthenticode -Path $data.path -Pins $data.publishers; Write-Output ('VERIFIED:' + $result.Status)\n";
        using var process = Process.Start(WindowsUpdateProcess.Command(script)) ?? throw new IOException("无法启动更新签名验证。");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string standardOutput = await output;
            string standardError = await errors;
            if (process.ExitCode != 0 || !standardOutput.Trim().StartsWith("VERIFIED:", StringComparison.Ordinal))
                throw new InvalidDataException("更新包签名完整性、证书链状态或固定发布者指纹校验失败。 " + standardError.Trim());
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
}

public static class WindowsUpdateProcess
{
    public sealed record Handoff(string CancelPath, string CommitPath);
    internal static string DecodeData(string json, string variable)
        => variable + "=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "')) | ConvertFrom-Json;\n";
    internal static ProcessStartInfo Command(string script) => new()
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        // Dot-sourcing the shipped verifier script is blocked by the default "Restricted"
        // policy on Windows clients, so bypass it for this controlled, data-only invocation.
        ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
    };

    public static async Task<Handoff> PrepareAsync(VerifiedUpdatePackage package, string[] publishers, long expectedBuild,
        Action<string> registerCancel, CancellationToken ct)
    {
        if (publishers.Length == 0 || publishers.Length > 8 || publishers.Any(pin => !UpdateManifest.IsHash(pin)))
            throw new InvalidOperationException("未配置有效的更新发布者指纹，取消安装交接。");
        string directory = Path.Combine(AppSettings.AppDataFolder, "Updates", "handoff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "ready"), cancel = Path.Combine(directory, "cancel");
        string commit = Path.Combine(directory, "commit");
        registerCancel(cancel);
        using var current = Process.GetCurrentProcess();
        var task = new {
            processId = Environment.ProcessId, processStartTicks = current.StartTime.ToUniversalTime().Ticks,
            packagePath = package.Path, package.Release.Size, sha256 = package.Release.Sha256,
            publishers = publishers.Select(p => p.ToUpperInvariant()).ToArray(), verifierPath = Path.Combine(AppContext.BaseDirectory, "Updates", "VerifyPinnedAuthenticode.ps1"),
            applicationPath = Environment.ProcessPath,
            expectedVersion = package.Release.Version, expectedBuild,
            resultPath = Path.Combine(AppSettings.AppDataFolder, "Updates", "install-result.json"), readyPath = ready, cancelPath = cancel, commitPath = commit,
        };
        string script = DecodeData(JsonSerializer.Serialize(task, UpdateManifest.Json), "$task") +
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Updates", "UpdateHandoff.ps1"), ct);
        var command = Command(script);
        using var helper = Process.Start(command) ?? throw new IOException("无法启动更新安装交接。");
        // Drain both pipes: the helper is a child of a GUI process with no console, and an
        // inherited invalid stdout handle can make PowerShell exit before it writes "ready".
        var helperOutput = helper.StandardOutput.ReadToEndAsync();
        var helperError = helper.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
            while (!File.Exists(ready))
            {
                if (helper.HasExited)
                {
                    string detail = (await helperError).Trim();
                    AppLog.Write($"update: handoff exited code={helper.ExitCode} detail={detail.Replace('\n', ' ')}");
                    throw new IOException("更新安装交接未就绪，当前版本已保留。" + (detail.Length > 0 ? " " + detail : ""));
                }
                await Task.Delay(100, deadline.Token);
            }
            return new(cancel, commit);
        }
        catch (Exception ex)
        {
            File.WriteAllText(cancel, "cancel");
            _ = helperOutput;
            if (ex is not IOException) AppLog.Write($"update: handoff wait error={ex.GetType().Name}");
            throw;
        }
    }
}
