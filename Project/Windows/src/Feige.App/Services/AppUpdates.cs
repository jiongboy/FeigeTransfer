using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Feige.Core.Updates;
using Microsoft.UI.Dispatching;

namespace Feige.App.Services;

/// <summary>App-owned update state. Automatic checks never download, install, or open a modal dialog.</summary>
public sealed class AppUpdates
{
    public static AppUpdates Current { get; } = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requests = new();
    private DispatcherQueue? _dispatcher;
    private UpdateSources? _sources;
    private UpdateSource? _source; // origin that served the current candidate
    private UpdateClient? _client;
    private UpdateStateFile? _states;
    private UpdateTarget? _target;
    private Task? _check, _download;
    private CancellationTokenSource? _operation, _waiting;
    private bool _automaticRequest, _initialized;
    private VerifiedUpdatePackage? _package;
    private string? _handoffCancelPath;
    public event EventHandler? Changed;
    public string Status { get; private set; } = "更新来源尚未配置";
    public string VersionText { get; private set; } = "";
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsWaitingToInstall { get; private set; }
    public bool IsInstalling { get; private set; }
    public bool BannerVisible { get; private set; }
    public string? InstallNotice { get; private set; }
    public bool InstallNoticeIsSuccess { get; private set; }
    public void DismissInstallNotice() { InstallNotice = null; Notify(); }
    public bool CanCheck => _client is not null && _states is not null && !IsDownloading && !IsWaitingToInstall && !IsInstalling;
    public bool CanDownload => Candidate is not null && _sources?.PublisherCertificateSha256.Length > 0 &&
        !IsChecking && !IsDownloading && !IsWaitingToInstall && !IsInstalling && _package is null;
    public bool CanInstall => _package is not null && !IsWaitingToInstall && !IsInstalling;
    public long Downloaded { get; private set; }
    public UpdateRelease? Candidate { get; private set; }
    public string ReleaseText => Candidate is { } r ? $"{r.Version} · 构建 {r.Build} · {r.PublishedAt}\n{UiPromptService.FormatSize(r.Size)}\n{r.Summary}" : "";
    private string Scope => $"{_sources?.Sources.FirstOrDefault()?.BaseUri}/{_target!.Scope}";

    public void Initialize(DispatcherQueue dispatcher)
    {
        if (_initialized) return;
        _initialized = true; _dispatcher = dispatcher;
        try
        {
            var assembly = typeof(App).Assembly;
            string version = assembly.GetName().Version?.ToString(3) ?? "1.2.0";
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            long build = long.TryParse(metadata.GetValueOrDefault("FeigeBuildNumber"), out var parsed) ? parsed : 0;
            string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string installation = AppEnvironment.IsPackaged ? "msix" : "exe";
            _sources = JsonSerializer.Deserialize<UpdateSources>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "UpdateSource.json")), UpdateManifest.Json);
            _target = new(architecture, _sources?.Channel ?? "stable", installation, version, build, Environment.OSVersion.Version);
            VersionText = $"版本 {version} · 构建 {build} · {architecture.ToUpperInvariant()} · {(_target.Channel == "stable" ? "正式版" : "测试版")} · {installation.ToUpperInvariant()}";
            if (AppEnvironment.IsPackaged) Status = "MSIX 请通过对应发行渠道更新，不会转换为 EXE 安装。";
            else if (_sources?.IsConfigured != true) Status = "更新服务器或更新签名公钥尚未配置，暂不联网检查。";
            else
            {
                _sources.Validate();
                _states = new UpdateStateFile(Path.Combine(AppSettings.AppDataFolder, "Updates"));
                _client = new(_sources);
                Status = _states.Get(Scope).LastResult;
            }
            ReadInstallResult();
        }
        catch (Exception ex) { Status = "更新配置或记录无法读取，原文件已保留。"; Log("initialize", ex); }
        AppSession.Current.StateChanged += OnSessionChanged;
        _ = AutomaticLoopAsync();
        Notify();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => MaybeCheckAutomatically();
    public void PreferencesChanged()
    {
        if (!AppSession.Current.Settings.AutoCheckUpdates && _automaticRequest) _operation?.Cancel();
        MaybeCheckAutomatically(); Notify();
    }
    private void MaybeCheckAutomatically()
    {
        if (CanCheck && !IsChecking && !_lifetime.IsCancellationRequested && AppSession.Current.Settings.LoadError is null &&
            AppSession.Current.Settings.Setup.Ended && AppSession.Current.Settings.AutoCheckUpdates && _states!.Get(Scope).IsDue(DateTimeOffset.UtcNow))
            _ = CheckAsync(manual: false);
    }
    private async Task AutomaticLoopAsync()
    {
        try
        {
            MaybeCheckAutomatically();
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token)) await OnUiAsync(MaybeCheckAutomatically);
        }
        catch (OperationCanceledException) { }
    }

    public Task CheckAsync(bool manual = true)
    {
        lock (_requests)
        {
            if (_check is { IsCompleted: false }) return _check;
            return _check = CheckCoreAsync(manual);
        }
    }
    private async Task CheckCoreAsync(bool manual)
    {
        if (!CanCheck || _target is null) { Notify(); return; }
        if (!AppSession.Current.Settings.Setup.Ended) { Status = "请先结束首次设置，再检查更新。"; Notify(); return; }
        IsChecking = true; BannerVisible = false; Candidate = null; _package = null;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _automaticRequest = !manual;
        Status = "正在检查更新…"; Notify();
        try
        {
            var state = _states!.Get(Scope) with { LastAttempt = DateTimeOffset.UtcNow, LastResult = "上次检查未完成" };
            _states.Save(Scope, state); // Failures and cancellation also count toward the automatic interval.
            var result = await _client!.CheckAsync(_target, state.HighestSeen, _operation.Token);
            _source = result.Source;
            var selection = result.Selection;
            Candidate = selection.Release;
            var originSuffix = result.Source.Id is "github" ? "（GitHub 来源）" : "";
            Status = selection.Kind switch { UpdateCheckKind.Available => $"发现新版本 {Candidate!.Version}{originSuffix}",
                UpdateCheckKind.NoCompatibleUpdate => "当前系统、架构或安装渠道暂无兼容更新", _ => $"已是最新版本{originSuffix}" };
            state = state with { LastResult = Status, HighestSeen = Math.Max(state.HighestSeen, selection.HighestCompatibleBuild) };
            if (!manual && Candidate is { } candidate && state.ShouldPrompt(candidate.Build, DateTimeOffset.UtcNow))
            {
                state = state with { LastPromptAt = DateTimeOffset.UtcNow, LastPromptBuild = candidate.Build };
                BannerVisible = true;
            }
            _states.Save(Scope, state);
        }
        catch (Exception ex)
        {
            Candidate = null; BannerVisible = false;
            Status = ex is InvalidOperationException ? "更新发布者指纹未配置，暂不能安全下载；仍可手动检查更新" :
                ex is OperationCanceledException ? "更新检查已取消，可手动重试" : Describe(ex);
            try { _states!.Save(Scope, _states.Get(Scope) with { LastResult = Status }); }
            catch (Exception save) { Status = "更新记录未能保存，原文件已保留。"; Log("save-check", save); }
            Log("check", ex);
        }
        finally { IsChecking = false; _automaticRequest = false; _operation.Dispose(); _operation = null; Notify(); }
    }

    public void Later() { BannerVisible = false; Notify(); }
    public void Skip()
    {
        if (Candidate is null || IsDownloading || IsWaitingToInstall || IsInstalling) return;
        _states!.Save(Scope, _states.Get(Scope) with { SkippedBuild = Candidate.Build });
        Status = $"已跳过 {Candidate.Version}（构建 {Candidate.Build}），手动检查仍可查看";
        BannerVisible = false; Notify();
    }
    public Task DownloadAsync()
    {
        lock (_requests)
        {
            if (_download is { IsCompleted: false }) return _download;
            return _download = DownloadCoreAsync();
        }
    }
    private async Task DownloadCoreAsync()
    {
        if (!CanDownload || Candidate is not { } candidate) return;
        IsDownloading = true; Downloaded = 0; BannerVisible = false;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); Notify();
        try
        {
            Status = "正在下载更新…";
            long lastReport = 0;
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (!IsDownloading) return;
                Downloaded = p.Received;
                if (p.Received == p.Total) Status = "正在验证更新包大小、摘要和发布者…";
                if (p.Received != p.Total && Environment.TickCount64 - lastReport < 100) return;
                lastReport = Environment.TickCount64; Notify();
            });
            _package = await _client!.DownloadAsync(candidate, Path.Combine(AppSettings.AppDataFolder, "Updates", "Downloads"),
                new WindowsPackageVerifier(_sources!.PublisherCertificateSha256), progress, _operation.Token);
            Status = "更新包大小、摘要与发布者已验证，可以安装";
        }
        catch (Exception ex) { Status = ex is OperationCanceledException ? "下载已取消，可重新下载" : Describe(ex); Log("download", ex); }
        finally { IsDownloading = false; _operation.Dispose(); _operation = null; Notify(); }
    }
    public void Cancel()
    {
        try { if (_handoffCancelPath is not null) File.WriteAllText(_handoffCancelPath, "cancel"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("cancel-handoff", ex); }
        _operation?.Cancel(); _waiting?.Cancel();
    }
    public async Task DownloadAndInstallAsync(MainWindow window)
    {
        if (Candidate is null) return;
        if (_package is null) await DownloadAsync();
        if (_package is null) return; // download failed or was cancelled; Status already explains why
        await InstallWhenReadyAsync(window);
    }

    public async Task InstallWhenReadyAsync(MainWindow window)
    {
        if (!CanInstall || _package is not { } package) return;
        IsWaitingToInstall = true;
        _handoffCancelPath = null;
        _waiting = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            Status = "正在等待收发结束，可取消等待"; Notify();
            while (!await window.PrepareForUpdateAsync()) await Task.Delay(500, _waiting.Token);
            _waiting.Token.ThrowIfCancellationRequested();
            IsWaitingToInstall = false; IsInstalling = true; Status = "正在准备安装并重启…"; Notify();
            var handoff = await WindowsUpdateProcess.PrepareAsync(package, _sources!.PublisherCertificateSha256, package.Release.Build,
                path => _handoffCancelPath = path, _waiting.Token);
            _waiting.Token.ThrowIfCancellationRequested();
            await window.ExitForUpdateAsync(handoff.CommitPath);
        }
        catch (Exception ex)
        {
            Cancel();
            AppSession.Current.CancelUpdatePreparation();
            Status = ex is OperationCanceledException ? "已取消等待安装" : "安装准备失败，当前版本已保留。";
            Log("handoff", ex);
        }
        finally { IsWaitingToInstall = IsInstalling = false; _waiting.Dispose(); _waiting = null; Notify(); }
    }

    private void ReadInstallResult()
    {
        string path = Path.Combine(AppSettings.AppDataFolder, "Updates", "install-result.json");
        if (!File.Exists(path) || _target is null) return;
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("更新安装结果无效。");
        using var result = JsonDocument.Parse(File.ReadAllBytes(path));
        string? version = result.RootElement.GetProperty("version").GetString();
        long build = result.RootElement.GetProperty("build").GetInt64();
        string? stage = result.RootElement.GetProperty("stage").GetString();
        if ((stage is "installed-awaiting-restart" or "installed-restart-failed") &&
            version == _target.Version && build == _target.Build)
        {
            Status = "更新完成，当前运行版本已确认";
            InstallNotice = $"已更新到 {version}（构建 {build}）。";
            InstallNoticeIsSuccess = true;
            File.Delete(path);
            AppLog.Write($"update: completed version={version} build={build} stage={stage}");
        }
        else if (stage != "cancelled")
        {
            Status = "上次更新未完成版本确认，可重试或手动安装。";
            InstallNotice = "上次更新未能确认版本，可重试或手动安装。";
            InstallNoticeIsSuccess = false;
            AppLog.Write($"update: unconfirmed stage={stage} version={version} build={build}");
        }
    }
    public void Stop(bool preserveHandoff = false)
    {
        if (!preserveHandoff) Cancel();
        _lifetime.Cancel();
    }
    private static string Describe(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        HttpRequestException h when h.StatusCode == System.Net.HttpStatusCode.NotFound => "尚无可用的更新发布信息",
        HttpRequestException => "无法连接更新服务，请稍后重试；局域网收发不受影响",
        IOException => "更新文件无法读写，请检查磁盘空间和访问权限",
        _ => "检查或处理更新失败，请稍后重试",
    };
    private static void Log(string stage, Exception ex) => AppLog.Write($"update: stage={stage} error={ex.GetType().Name}");
    private void Notify() => _dispatcher?.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    private Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcher?.TryEnqueue(() => { try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } }) != true)
            completion.TrySetCanceled();
        return completion.Task;
    }
}
