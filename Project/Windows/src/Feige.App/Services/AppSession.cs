using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Discovery;
using Feige.Core.Net;
using Feige.Core.Storage;
using Feige.Core.Updates;
using Microsoft.UI.Dispatching;

namespace Feige.App.Services;

public sealed record ReceivedItem(string Name, string Path, long Size, string From, DateTimeOffset At)
{
    public string TimeText => At.ToLocalTime().ToString("MM-dd HH:mm");
    public string Detail => $"{UiPromptService.FormatSize(Size)} · {From} · {TimeText}";
}
public sealed record TransferProgress(string FileName, long Transferred, long Total, string Stage)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Transferred * 100.0 / Total, 0, 100);
}

/// <summary>One application-owned v3 conversation. Pages observe state; they never own sockets or read loops.</summary>
public sealed class AppSession
{
    public static AppSession Current { get; } = new();
    private DispatcherQueue? _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startup = new(1);
    private CancellationTokenSource? _connectionCts;
    private V3Connection? _connection;
    private Task? _acceptLoop, _runner;
    private CancellationTokenSource? _networkCts, _networkRefreshDelay;
    private int _connectionSlot, _sendingFiles, _sendingText;
    private bool _networkReady;
    private readonly UpdateWorkGate _updateWork = new();
    private readonly Dictionary<(string Fingerprint, Guid TransferId), TaskCompletionSource<bool>> _incomingDecisions = [];
    private readonly object _incomingDecisionGate = new();
    public bool IsInstallingUpdate => _updateWork.IsInstalling;
    public bool HasUpdateWork => _updateWork.HasActiveWork;
    private ConversationHistory? _messages;
    private bool _draftsLoaded;
    private V3TextInbox _inbox = new();
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<ReceivedItem>? FileReceived, FileSent;
    public event EventHandler<string>? Failed;
    public event EventHandler? StateChanged;
    public ObservableCollection<DiscoveredPeer> Peers { get; } = [];
    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ObservableCollection<ReceivedItem> ReceivedFiles { get; } = [];
    public ObservableCollection<ReceivedItem> SentFiles { get; } = [];
    public TransferHistory History { get; private set; } = TransferHistory.Load(AppSettings.AppDataFolder);
    public IdentityKey? Identity { get; private set; }
    public LocalDevice? Device { get; private set; }
    public V3PeerListener? Listener { get; private set; }
    public LanDiscovery? Discovery { get; private set; }
    public AppSettings Settings { get; private set; } = AppSettings.Load();
    public string Status { get; private set; } = "正在准备安全连接…";
    public string? ActiveFingerprint { get; private set; }
    public int ListenPort => Listener?.Port ?? 0;
    public bool IsRunning => _networkReady && Listener is not null && FolderReady && Identity is not null;
    public bool FolderReady { get; private set; }
    public string IdentityStatus { get; private set; } = "等待准备";
    public string FolderStatus { get; private set; } = "等待检查";
    public string NetworkStatus { get; private set; } = "等待身份准备";
    public string? DiscoveryIssue { get; private set; }
    public bool NotificationInvitationPending { get; private set; }
    public bool FirstTransferNoticePending { get; private set; }
    public bool IsStarting { get; private set; }
    public bool IsRefreshingPeers { get; private set; }
    public DateTimeOffset? LastPeerRefreshAt { get; private set; }
    public bool PeerRefreshFailed { get; private set; }
    public bool IsConnecting => _connectionSlot != 0 && _connection is null;
    public bool IsSendingFiles => _sendingFiles != 0;
    public bool IsSendingText => _sendingText != 0;
    public bool CanConnect => IsRunning && !IsStarting && !IsInstallingUpdate && Settings.Setup.Ended && _connectionSlot == 0;
    public bool CanSend(ConversationItem? conversation) => !IsInstallingUpdate && conversation is not null && _connection is not null && conversation.Fingerprint == ActiveFingerprint;
    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_lifetime.IsCancellationRequested) return;
        _networkRefreshDelay?.Cancel();
        _networkRefreshDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = RefreshAfterNetworkChangeAsync(_networkRefreshDelay);
    });
    private async Task RefreshAfterNetworkChangeAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(750, delay.Token); await RefreshDiscoveryAsync(delay.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("network refresh: " + ex.GetType().Name); }
        finally { if (ReferenceEquals(_networkRefreshDelay, delay)) _networkRefreshDelay = null; delay.Dispose(); }
    }

    public void ReloadSettings() { Settings = AppSettings.Load(); Changed(); }

    public void SaveGuideSettings(string name, string folder, bool automaticUpdates)
    {
        Settings.SaveSetup(name, folder, automaticUpdates);
        FolderReady = true; FolderStatus = "可以保存文件"; Changed();
    }

    public void EndGuide(bool deferred)
    {
        Settings.SaveProgress(Settings.Setup with { Step = 2, Ended = true, Deferred = deferred });
        Changed();
    }

    public async Task LoadLocalHistoryAsync()
    {
        await _startup.WaitAsync();
        try { await ReadLocalHistoryAsync(_lifetime.Token); }
        catch (Exception ex) { NetworkStatus = "本机资料需要处理"; Fail("本机记录未能读取，原资料已保留：" + ErrorText.Describe(ex)); }
        finally { Changed(); _startup.Release(); }
    }
    private async Task ReadLocalHistoryAsync(CancellationToken ct)
    {
        if (_messages is not null) return;
        var history = await Task.Run(() =>
        {
            string path = Path.Combine(AppSettings.AppDataFolder, TransferHistory.FileName);
            if (File.Exists(path))
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out int historyVersion) || historyVersion != 1 ||
                    !json.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new InvalidDataException("已有传输记录格式或版本无效，请保留资料并检查备份。");
                foreach (var row in entries.EnumerateArray())
                    if (row.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !row.TryGetProperty("direction", out var direction) || direction.GetString() is not ("sent" or "received") ||
                        !row.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()) ||
                        !row.TryGetProperty("path", out var filePath) || string.IsNullOrWhiteSpace(filePath.GetString()) ||
                        !row.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes) || bytes < 0 ||
                        !row.TryGetProperty("peer", out var peer) || string.IsNullOrWhiteSpace(peer.GetString()) ||
                        !row.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out _))
                        throw new InvalidDataException("已有传输记录包含无效内容，请保留资料并检查备份。");
            }
            return (messages: ConversationHistory.Load(AppSettings.AppDataFolder), transfers: TransferHistory.Load(AppSettings.AppDataFolder));
        });
        ct.ThrowIfCancellationRequested(); _messages = history.messages; History = history.transfers;
        Conversations.Clear(); ReceivedFiles.Clear(); SentFiles.Clear();
        _inbox = new V3TextInbox(received: _messages.Messages.Where(m => m.Direction == TransferDirection.Received && m.Kind == MessageKind.Text && m.State == MessageState.Received).Select(m => (m.Fingerprint, m.Id)));
        foreach (var message in _messages.Messages) ApplyMessage(message);
        foreach (var draft in ConversationDrafts.Load(AppSettings.AppDataFolder))
            FindConversation(ConversationHistory.NormalizeFingerprint(draft.Fingerprint), draft.Name).Draft = draft.Text;
        _draftsLoaded = true;
        foreach (var entry in History.Entries)
            (entry.Direction == TransferDirection.Sent ? SentFiles : ReceivedFiles).Add(new(entry.Name, entry.Path, entry.Size, entry.Peer, entry.At));
        Changed();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var ct = attempt.Token;
        await _startup.WaitAsync(ct);
        string stage = "history";
        try
        {
            if (IsRunning || _lifetime.IsCancellationRequested) return;
            if (Settings.LoadError is not null) throw new InvalidDataException(Settings.LoadError);
            if (!Settings.Setup.Ended && Settings.Setup.Step != 2)
                throw new InvalidOperationException("请先保存本机设置。");
            IsStarting = true; Changed();
            await ReadLocalHistoryAsync(ct);
            stage = "identity";
            Publish("正在准备本机身份…");
            IdentityStatus = "正在准备"; NetworkStatus = "等待身份准备"; Changed();
            if (Identity is null)
            {
                var identity = await Task.Run(() => new IdentityStore().LoadOrCreate());
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var challenge = RandomNumberGenerator.GetBytes(32);
                    await Task.Run(() => IdentityKey.VerifyOrThrow(identity.SubjectPublicKeyInfo, challenge, identity.Sign(challenge)));
                    ct.ThrowIfCancellationRequested();
                    Identity = identity;
                }
                catch { identity.Dispose(); throw; }
            }
            IdentityStatus = "已就绪"; Changed();
            stage = "folder";
            if (!FolderReady)
            {
                FolderStatus = "检查中"; Changed();
                await Task.Run(() => SetupValidation.ReceiveFolder(Settings.ReceiveFolder));
                ct.ThrowIfCancellationRequested(); FolderReady = true;
            }
            FolderStatus = "可以保存文件"; Changed();
            stage = "network"; NetworkStatus = "正在启动"; Changed();
            ct.ThrowIfCancellationRequested();
            Device = new LocalDevice(Identity, Settings.DeviceName);
            _networkCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            using var stopNetwork = ct.Register(() => _networkCts?.Cancel());
            Listener = new V3PeerListener(); Listener.Start();
            await StartDiscoveryAsync(Device, _networkCts.Token);
            ct.ThrowIfCancellationRequested();
            _networkReady = true;
            NetworkStatus = DiscoveryIssue is null ? "查找附近设备" : "直连已就绪，自动发现暂不可用";
            var networkToken = _networkCts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(networkToken));
            Publish(DiscoveryIssue is null ? $"已就绪 · {Device.DeviceName} · 端口 {ListenPort}" : "自动发现暂不可用，可刷新或手动连接。");
            AppLog.Write($"started: protocol=3 listener={ListenPort} messages={_messages!.Messages.Count}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopNetworkAsync();
            NetworkStatus = "尚未启动"; Publish("连接准备已暂停，可稍后继续。");
        }
        catch (Exception ex)
        {
            await StopNetworkAsync();
            if (stage == "identity") IdentityStatus = "需要处理";
            else if (stage == "folder") { FolderReady = false; FolderStatus = "需要更改"; }
            NetworkStatus = stage == "history" ? "本机资料需要处理" : stage == "network" ? "需要处理" : "等待本机准备";
            if (!_lifetime.IsCancellationRequested) Fail("连接未就绪：" + ErrorText.Describe(ex));
        }
        finally { IsStarting = false; Changed(); _startup.Release(); }
    }

    public async Task PausePreparationAsync()
    {
        await _startup.WaitAsync();
        try { await StopNetworkAsync(); NetworkStatus = "尚未启动"; Publish("连接准备已暂停，可稍后继续。"); Changed(); }
        finally { _startup.Release(); }
    }

    private async Task StopNetworkAsync()
    {
        _networkReady = false; _networkCts?.Cancel();
        if (Discovery is not null) await Discovery.DisposeAsync();
        if (Listener is not null) await Listener.DisposeAsync();
        if (_acceptLoop is not null) { try { await _acceptLoop; } catch (OperationCanceledException) { } }
        Discovery = null; Listener = null; _acceptLoop = null;
        _networkCts?.Dispose(); _networkCts = null; DiscoveryIssue = null; Peers.Clear();
    }

    private async Task StartDiscoveryAsync(LocalDevice device, CancellationToken ct)
    {
        DiscoveryIssue = null;
        var discovery = CreateDiscovery(device); Discovery = discovery;
        try { await discovery.StartAsync(ct); }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            Discovery = null;
            try { await discovery.DisposeAsync(); } catch (Exception cleanup) { AppLog.Write("discovery cleanup: " + cleanup.GetType().Name); }
            DiscoveryIssue = "自动发现暂不可用，请检查 VPN/TUN、网络隔离或 UDP 5353 是否可用。仍可手动输入对方的局域网地址和端口。";
            AppLog.Write("discovery unavailable: " + ex.GetType().Name);
        }
    }
    private async Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        await _startup.WaitAsync(ct);
        try
        {
            // Rebuild only discovery. Established TCP sessions and their consent remain untouched.
            if (!IsRunning || Device is null || _lifetime.IsCancellationRequested) return;
            var old = Discovery; Discovery = null;
            if (old is not null)
                try { await old.DisposeAsync(); } catch (Exception ex) { AppLog.Write("discovery refresh cleanup: " + ex.GetType().Name); }
            Peers.Clear(); ct.ThrowIfCancellationRequested();
            await StartDiscoveryAsync(Device, _networkCts!.Token);
            NetworkStatus = DiscoveryIssue is null ? "查找附近设备" : "直连已就绪，自动发现暂不可用";
            AppLog.Write("discovery refreshed: available=" + (DiscoveryIssue is null));
            Changed();
        }
        finally { _startup.Release(); }
    }

    private LanDiscovery CreateDiscovery(LocalDevice device)
    {
        var discovery = new LanDiscovery(device, ListenPort, version: 3);
        discovery.PeerFound += (_, peer) => Post(() =>
        {
            if (!ReferenceEquals(Discovery, discovery)) return;
            // IPv4/IPv6 callbacks may reach the dispatcher out of order; always apply the current snapshot.
            var current = discovery.Peers.FirstOrDefault(p => p.Instance == peer.Instance);
            if (current is null) return;
            foreach (var conversation in Conversations) conversation.ApplyDiscoveredName(current);
            var existing = Peers.FirstOrDefault(p => p.Instance == peer.Instance);
            if (existing is not null) Peers[Peers.IndexOf(existing)] = current; else Peers.Add(current);
        });
        discovery.PeerLost += (_, peer) => Post(() =>
        {
            if (!ReferenceEquals(Discovery, discovery) || discovery.Peers.Any(p => p.Instance == peer.Instance)) return;
            var existing = Peers.FirstOrDefault(p => p.Instance == peer.Instance);
            if (existing is not null) Peers.Remove(existing);
        });
        return discovery;
    }

    public async Task RefreshPeersAsync()
    {
        if (IsRefreshingPeers || !IsRunning || _lifetime.IsCancellationRequested) return;
        IsRefreshingPeers = true; PeerRefreshFailed = false; Changed();
        try
        {
            await RefreshDiscoveryAsync(_lifetime.Token);
            if (DiscoveryIssue is not null) { PeerRefreshFailed = true; return; }
            // Replies are asynchronous. Keep the progress visible through the initial discovery response window;
            // periodic discovery continues after this user-requested refresh completes.
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (IsRunning && Discovery is not null && DiscoveryIssue is null) LastPeerRefreshAt = DateTimeOffset.Now;
            else PeerRefreshFailed = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { PeerRefreshFailed = true; throw; }
        finally { IsRefreshingPeers = false; Changed(); }
    }

    public Task ConnectAsync(IPEndPoint endpoint) => ConnectAsync(new[] { endpoint });
    public Task ConnectAsync(DiscoveredPeer peer) => ConnectAsync(peer.EndPoints);
    private async Task ConnectAsync(IReadOnlyList<IPEndPoint> endpoints)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后连接。");
        if (!IsRunning || !Settings.Setup.Ended || Device is null) throw new InvalidOperationException("安全连接尚未准备好，请稍后重试。");
        if (Interlocked.CompareExchange(ref _connectionSlot, 1, 0) != 0) throw new InvalidOperationException("请先断开当前连接。");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connectionCts = cancellation; Changed();
        bool attached = false;
        try
        {
            Publish("正在建立网络、交换并验证设备身份，随后等待对方同意…");
            var connection = await Task.Run(() => V3PeerConnector.ConnectAsync(endpoints, Device, cancellation.Token));
            try { cancellation.Token.ThrowIfCancellationRequested(); await AttachAsync(connection, cancellation); attached = true; }
            catch { connection.Close(); throw; }
        }
        catch (Exception ex) { Fail(ErrorText.Describe(ex)); throw; }
        finally
        {
            if (!attached) { _connectionCts = null; cancellation.Dispose(); Interlocked.Exchange(ref _connectionSlot, 0); Changed(); }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken networkToken)
    {
        while (!networkToken.IsCancellationRequested)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(networkToken);
            bool reserved = false, attached = false;
            IDisposable? acceptingWork = null;
            try
            {
                var connection = await Listener!.AcceptAsync(() => Device!, async (name, ct) =>
                {
                    if (!Settings.Setup.Ended || !IsRunning) return false;
                    acceptingWork = _updateWork.TryEnter();
                    if (acceptingWork is null) return false;
                    if (Interlocked.CompareExchange(ref _connectionSlot, 1, 0) != 0) return false;
                    reserved = true; _connectionCts = cancellation;
                    Publish("收到连接请求，等待本机同意…"); Changed();
                    return await UiPromptService.Current.ConfirmConnectionAsync(name, Settings.ReceiveFolder, ct);
                }, cancellation.Token);
                try { cancellation.Token.ThrowIfCancellationRequested(); await AttachAsync(connection, cancellation); attached = true; }
                catch { connection.Close(); throw; }
            }
            catch (OperationCanceledException) when (networkToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppLog.Write($"accept: {ex.GetType().Name}");
                if (reserved) Publish("连接未建立：" + ErrorText.Describe(ex));
            }
            finally
            {
                acceptingWork?.Dispose();
                if (!attached)
                {
                    if (reserved) { _connectionCts = null; Interlocked.Exchange(ref _connectionSlot, 0); Changed(); }
                    cancellation.Dispose();
                }
            }
        }
    }

    private async Task AttachAsync(V3Connection connection, CancellationTokenSource cancellation)
    {
        connection.BeginIncomingWork = _updateWork.TryEnter;
        string fingerprint = ConversationHistory.NormalizeFingerprint(connection.PeerFingerprint);
        connection.TextReceived = message => SaveAsync(new(message.Id, fingerprint, connection.PeerName, TransferDirection.Received,
            MessageKind.Text, MessageState.Received, DateTimeOffset.Now, Text: message.Text));
        connection.FileReceiving = async file =>
        {
            var message = new ConversationMessage(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                file.Kind == "image" ? MessageKind.Image : MessageKind.File, MessageState.Sending, DateTimeOffset.Now, FileName: file.FileName, Size: file.Size, Error: "等待确认接收");
            await SaveAsync(message);
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_incomingDecisionGate)
            {
                if (!_incomingDecisions.TryAdd((fingerprint, file.TransferID), decision))
                    throw new InvalidDataException("重复的文件接收请求。");
            }
            await OnUiAsync(() =>
            {
                var conversation = FindConversation(fingerprint, connection.PeerName);
                var item = conversation.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Entry.Direction == TransferDirection.Received);
                if (item is null)
                {
                    item = new MessageItem(message);
                    conversation.Messages.Add(item);
                }
                Changed();
            });
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_connection, connection)) decision.TrySetCanceled(cancellation.Token);
            using var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            decisionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                bool accepted;
                try { accepted = await decision.Task.WaitAsync(decisionTimeout.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    var timedOut = _messages!.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Fingerprint == fingerprint && m.Direction == TransferDirection.Received) ?? message;
                    await SaveAsync(timedOut with { State = MessageState.Failed, Error = "等待接收确认超时。" });
                    throw new IOException("等待接收确认超时。");
                }
                var current = _messages!.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Fingerprint == fingerprint && m.Direction == TransferDirection.Received) ?? message;
                if (!accepted) await SaveAsync(current with { State = MessageState.Declined, Error = "已拒绝接收" });
                else if (current.Error != "接收已确认") await SaveAsync(current with { Error = "接收已确认" });
                return accepted;
            }
            catch (OperationCanceledException)
            {
                var current = _messages!.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Fingerprint == fingerprint && m.Direction == TransferDirection.Received) ?? message;
                await SaveAsync(current with { State = MessageState.Failed, Error = "连接已断开。" });
                throw;
            }
            catch (Exception ex)
            {
                var current = _messages!.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Fingerprint == fingerprint && m.Direction == TransferDirection.Received) ?? message;
                await SaveAsync(current with { State = MessageState.Failed, Error = ErrorText.Describe(ex) });
                throw;
            }
            finally
            {
                lock (_incomingDecisionGate) _incomingDecisions.Remove((fingerprint, file.TransferID));
            }
        };
        long lastReceiveUpdate = 0, receiveSampleBytes = 0;
        long receiveSampleAt = Environment.TickCount64;
        double receiveSpeed = 0;
        connection.ReceiveProgress = (file, bytes) =>
        {
            long now = Environment.TickCount64;
            long elapsed = now - receiveSampleAt;
            if (elapsed >= 500)
            {
                receiveSpeed = Math.Max(0, bytes - receiveSampleBytes) * 1000.0 / elapsed;
                receiveSampleBytes = bytes;
                receiveSampleAt = now;
            }
            if (bytes != 0 && bytes != file.Size && now - lastReceiveUpdate < 150) return;
            lastReceiveUpdate = now;
            double sampledSpeed = receiveSpeed;
            Post(() =>
            {
                var conversation = FindConversation(fingerprint, connection.PeerName);
                var item = conversation.Messages.FirstOrDefault(m => m.Id == file.TransferID && m.Entry.Direction == TransferDirection.Received);
                if (item is null)
                {
                    item = new MessageItem(new(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                        file.Kind == "image" ? MessageKind.Image : MessageKind.File, MessageState.Sending, DateTimeOffset.Now, FileName: file.FileName, Size: file.Size));
                    conversation.Messages.Add(item);
                }
                else if (item.Entry.Error == "接收已确认") item.Apply(item.Entry with { Error = "" });
                item.SetProgress(bytes, sampledSpeed);
            });
        };
        connection.FileReceived = async (file, path) =>
        {
            await SaveAsync(new(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                file.Kind == "image" ? MessageKind.Image : MessageKind.File, MessageState.Received, DateTimeOffset.Now, FileName: file.FileName, Path: path, Size: file.Size));
            await RecordFileAsync(TransferDirection.Received, new(file.FileName, path, file.Size, connection.PeerName, DateTimeOffset.Now));
        };
        await Task.Run(() => _messages!.SetHidden(fingerprint, false));
        await OnUiAsync(() =>
        {
            _connection = connection; ActiveFingerprint = fingerprint;
            var conversation = FindConversation(fingerprint, connection.PeerName); conversation.Name = connection.PeerName; conversation.State = "已连接";
            Publish("已连接 · " + connection.PeerName); Changed();
        });
        _runner = Task.Run(() => RunConnectionAsync(connection, fingerprint, cancellation));
    }

    private async Task RunConnectionAsync(V3Connection connection, string fingerprint, CancellationTokenSource cancellation)
    {
        bool failed = false;
        try { await connection.RunAsync(() => Settings.ReceiveFolder, cancellation.Token, _inbox); }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) { failed = true; Fail("连接已中断：" + ErrorText.Describe(ex)); } }
        finally
        {
            lock (_incomingDecisionGate)
            {
                foreach (var pending in _incomingDecisions.Where(kv => kv.Key.Fingerprint == fingerprint).Select(kv => kv.Value))
                    pending.TrySetCanceled();
            }
            connection.Close();
            try
            {
                var interrupted = await Task.Run(() => _messages!.FailActive(fingerprint, "连接已断开。"));
                await OnUiAsync(() => { foreach (var message in interrupted) ApplyMessage(message); });
            }
            catch (Exception ex) { AppLog.Write($"history: fail active {ex.GetType().Name}"); }
            await OnUiAsync(() =>
            {
                var conversation = Conversations.FirstOrDefault(c => c.Fingerprint == fingerprint);
                if (conversation is not null)
                {
                    conversation.State = "未连接";
                    foreach (var item in conversation.Messages.Where(m => m.Entry.IsActive)) item.Apply(item.Entry with { State = MessageState.Failed, Error = "连接已断开。" });
                }
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null; _connectionCts = null; ActiveFingerprint = null;
                    Interlocked.Exchange(ref _connectionSlot, 0);
                    if (!failed) Publish("已断开连接");
                    Changed();
                }
            });
            cancellation.Dispose();
        }
    }

    public async Task DisconnectAsync()
    {
        try { _connectionCts?.Cancel(); } catch (ObjectDisposedException) { }
        _connection?.Close();
        var runner = _runner;
        if (runner is not null) await runner;
    }

    public bool DecideIncomingFile(ConversationItem conversation, MessageItem item, bool accept)
    {
        if (item.Entry.Direction != TransferDirection.Received || item.Entry.Kind == MessageKind.Text ||
            item.Entry.State != MessageState.Sending || item.Entry.Error == "接收已确认")
            return false;
        TaskCompletionSource<bool>? decision;
        lock (_incomingDecisionGate)
        {
            var key = (ConversationHistory.NormalizeFingerprint(conversation.Fingerprint), item.Id);
            _incomingDecisions.TryGetValue(key, out decision);
        }
        if (decision is null || !decision.TrySetResult(accept)) return false;
        if (accept)
        {
            var confirmed = item.Entry with { Error = "接收已确认" };
            _ = SaveAsync(confirmed);
        }
        return true;
    }

    private V3Connection RequireConnection(ConversationItem conversation)
    {
        if (!CanSend(conversation)) throw new InvalidOperationException("请先连接当前设备；查看历史不会切换活动连接。");
        return _connection!;
    }

    public async Task SendTextAsync(ConversationItem conversation, string text, MessageItem? retry = null)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后发送。");
        var connection = RequireConnection(conversation);
        if (retry is not null && (retry.Entry.Fingerprint != conversation.Fingerprint || retry.Entry.Direction != TransferDirection.Sent || retry.Entry.Kind != MessageKind.Text))
            throw new InvalidOperationException("这条消息属于其他会话。");
        var id = retry?.Id ?? Guid.NewGuid(); new V3Text(id, text).Validate();
        if (Interlocked.CompareExchange(ref _sendingText, 1, 0) != 0) throw new InvalidOperationException("上一条文字仍在等待回执。");
        Changed();
        var message = new ConversationMessage(id, conversation.Fingerprint, connection.PeerName, TransferDirection.Sent, MessageKind.Text,
            MessageState.Sending, retry?.Entry.At ?? DateTimeOffset.Now, Text: text);
        try
        {
            await SaveAsync(message);
            await connection.SendTextAsync(text, id, _lifetime.Token);
            await SaveAsync(message with { State = MessageState.Delivered });
        }
        catch (Exception ex) { await MarkFailedAsync(message, ex); throw; }
        finally { Interlocked.Exchange(ref _sendingText, 0); Changed(); }
    }

    public async Task SendFilesAsync(ConversationItem conversation, IReadOnlyList<string> paths, bool images = false)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后发送。");
        var connection = RequireConnection(conversation);
        if (Interlocked.CompareExchange(ref _sendingFiles, 1, 0) != 0) throw new InvalidOperationException("请等当前这批附件发送完成。");
        Changed();
        var queued = new List<ConversationMessage>();
        try
        {
            foreach (string path in paths)
            {
                var info = new FileInfo(path);
                var message = new ConversationMessage(Guid.NewGuid(), conversation.Fingerprint, connection.PeerName, TransferDirection.Sent,
                    images || IsImage(path) ? MessageKind.Image : MessageKind.File, MessageState.Queued, DateTimeOffset.Now,
                    FileName: info.Name, Path: path, Size: info.Length);
                await SaveAsync(message); queued.Add(message);
            }
            foreach (var message in queued)
            {
                try
                {
                    if (!ReferenceEquals(_connection, connection)) throw new IOException("连接已断开，请重新连接后重试。");
                    await SaveAsync(message with { State = MessageState.Sending, Error = "等待对方确认接收" });
                    long lastUpdate = 0, sampleBytes = 0;
                    long sampleAt = Environment.TickCount64;
                    double sendSpeed = 0;
                    var sent = await connection.SendFileAsync(message.Path, message.Kind == MessageKind.Image ? "image" : "file", message.Id,
                        (bytes, total) =>
                        {
                            long now = Environment.TickCount64;
                            long elapsed = now - sampleAt;
                            if (elapsed >= 500)
                            {
                                sendSpeed = Math.Max(0, bytes - sampleBytes) * 1000.0 / elapsed;
                                sampleBytes = bytes;
                                sampleAt = now;
                            }
                            if (bytes != total && now - lastUpdate < 150) return;
                            lastUpdate = now;
                            double sampledSpeed = sendSpeed;
                            Post(() => conversation.Messages.FirstOrDefault(m => m.Id == message.Id && m.Entry.Direction == TransferDirection.Sent)?.SetProgress(bytes, sampledSpeed));
                            Post(() => ProgressChanged?.Invoke(this, new(message.FileName, bytes, total, "正在发送…")));
                        }, _lifetime.Token);
                    var currentMessage = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent) ?? message;
                    await SaveAsync(currentMessage with { State = MessageState.Delivered, Error = "", Size = sent.Size });
                    await RecordFileAsync(TransferDirection.Sent, new(message.FileName, message.Path, sent.Size, connection.PeerName, DateTimeOffset.Now));
                }
                catch (Exception ex)
                {
                    var current = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent) ?? message;
                    if (ex is IOException { Message: "已拒绝接收文件。" })
                        await SaveAsync(current with { State = MessageState.Declined, Error = "" });
                    else if (current.State != MessageState.Declined) await MarkFailedAsync(current, ex);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var message in queued)
            {
                var current = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent);
                if (current?.IsActive == true) await MarkFailedAsync(current, ex);
            }
            throw;
        }
        finally { Interlocked.Exchange(ref _sendingFiles, 0); Changed(); }
    }

    public static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private async Task MarkFailedAsync(ConversationMessage message, Exception error)
    {
        var failed = message with { State = MessageState.Failed, Error = ErrorText.Describe(error) };
        try { await SaveAsync(failed); }
        catch { await OnUiAsync(() => ApplyMessage(failed)); }
    }
    private async Task SaveAsync(ConversationMessage message)
    {
        await Task.Run(() => _messages!.Upsert(message));
        await OnUiAsync(() =>
        {
            ApplyMessage(message);
            if (message.State is MessageState.Delivered or MessageState.Received) RecordFirstCompletion();
        });
    }
    private void RecordFirstCompletion()
    {
        if (Settings.Setup.TransferTipSeen) return;
        try
        {
            bool invite = !Settings.NotificationsEnabled && !Settings.Setup.NotificationInvitationSeen && Notifier.IsSupported;
            Settings.SaveProgress(Settings.Setup with { TransferTipSeen = true, NotificationInvitationSeen = true });
            FirstTransferNoticePending = true; NotificationInvitationPending = invite; Changed();
        }
        catch (Exception ex) { AppLog.Write("first transfer hint: " + ex.GetType().Name); }
    }
    public void DismissTransferNotice() { FirstTransferNoticePending = false; Changed(); }
    public void DismissNotificationInvitation() { NotificationInvitationPending = false; Changed(); }
    public void EnableNotifications()
    {
        Settings.SaveNotificationsEnabled(true);
        Notifier.Current.Initialize(); NotificationInvitationPending = false; Changed();
    }
    public void UpdateProgress(Func<SetupProgress, SetupProgress> update)
    { Settings.SaveProgress(update(Settings.Setup)); Changed(); }

    private ConversationItem FindConversation(string fingerprint, string name)
    {
        var conversation = Conversations.FirstOrDefault(c => c.Fingerprint == fingerprint);
        if (conversation is null)
        {
            conversation = new(fingerprint, name);
            foreach (var peer in Peers) conversation.ApplyDiscoveredName(peer);
            Conversations.Add(conversation);
        }
        return conversation;
    }
    private void ApplyMessage(ConversationMessage message)
    {
        var conversation = FindConversation(message.Fingerprint, message.PeerName);
        var existing = conversation.Messages.FirstOrDefault(m => m.Id == message.Id && m.Entry.Direction == message.Direction);
        if (existing is null) conversation.Messages.Add(new(message)); else existing.Apply(message);
        conversation.Summary = message.Kind == MessageKind.Text ? message.Text.ReplaceLineEndings(" ") : message.FileName;
        // Mirror the durable retention window while preserving all active cards.
        var retained = _messages!.Messages.Select(m => (m.Fingerprint, m.Direction, m.Id)).ToHashSet();
        foreach (var peer in Conversations)
            foreach (var old in peer.Messages.Where(m => !m.Entry.IsActive && !retained.Contains((m.Entry.Fingerprint, m.Entry.Direction, m.Id))).ToArray()) peer.Messages.Remove(old);
    }
    private async Task RecordFileAsync(TransferDirection direction, ReceivedItem item)
    {
        await Task.Run(() => { lock (History) History.Add(new(direction, item.Name, item.Path, item.Size, item.From, item.At)); });
        await OnUiAsync(() =>
        {
            (direction == TransferDirection.Sent ? SentFiles : ReceivedFiles).Insert(0, item);
            if (direction == TransferDirection.Sent) FileSent?.Invoke(this, item); else FileReceived?.Invoke(this, item);
        });
        Notifier.Current.Show(direction == TransferDirection.Sent ? $"已发送 {item.Name}" : $"收到 {item.Name}", item.From);
    }
    public bool IsConversationHidden(ConversationItem conversation) => _messages?.IsHidden(conversation.Fingerprint) == true;
    public bool CanDeleteConversation(ConversationItem conversation) => _messages is not null && ActiveFingerprint != conversation.Fingerprint && !conversation.Messages.Any(m => m.Entry.IsActive);

    public async Task SetConversationHiddenAsync(ConversationItem conversation, bool hidden)
    {
        if (_messages is null) throw new InvalidOperationException("本机记录尚未准备好。");
        await Task.Run(() => _messages.SetHidden(conversation.Fingerprint, hidden));
        Changed();
    }

    public async Task RestoreAllHiddenConversationsAsync()
    {
        if (_messages is null) return;
        await Task.Run(_messages.RestoreAllHidden);
        Changed();
    }

    public async Task DeleteConversationAsync(ConversationItem conversation)
    {
        if (!CanDeleteConversation(conversation)) throw new InvalidOperationException("请先断开此会话的连接，并等待传输结束后再删除。");
        await Task.Run(() => _messages!.DeleteConversation(conversation.Fingerprint));
        await OnUiAsync(() =>
        {
            // A new incoming message can race with deletion; keep anything persisted after the delete.
            var retained = _messages!.Messages.Where(m => m.Fingerprint == conversation.Fingerprint).ToArray();
            var current = Conversations.FirstOrDefault(c => c.Fingerprint == conversation.Fingerprint);
            if (current is not null)
            {
                if (retained.Length == 0 && ActiveFingerprint != conversation.Fingerprint)
                {
                    // Remove the row before changing its summary, so deletion never briefly reorders it.
                    Conversations.Remove(current); current.Messages.Clear(); current.Draft = "";
                }
                else
                {
                    current.Messages.Clear();
                    foreach (var entry in retained) current.Messages.Add(new MessageItem(entry));
                    current.Summary = retained.LastOrDefault() is not { } last ? "暂无消息" : last.Kind == MessageKind.Text ? last.Text.ReplaceLineEndings(" ") : last.FileName;
                }
            }
            Changed();
        });
    }

    public void ClearHistory()
    {
        lock (History) History.Clear(); ReceivedFiles.Clear(); SentFiles.Clear(); Changed();
    }
    public async Task UpdateDeviceNameAsync(string value)
    {
        string name = SetupValidation.DeviceName(value);
        Settings.SaveDeviceName(name);
        if (Identity is not null) Device = new LocalDevice(Identity, name);
        if (IsRunning) await RefreshDiscoveryAsync(_lifetime.Token);
        Changed();
    }
    public void UpdateReceiveFolder(string folder)
    {
        SetupValidation.ReceiveFolder(folder);
        Settings.SaveReceiveFolder(folder); FolderReady = true; FolderStatus = "可以保存文件"; Changed();
    }
    public async Task StopAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _networkRefreshDelay?.Cancel();
        _lifetime.Cancel(); await DisconnectAsync();
        await _startup.WaitAsync();
        try
        {
            await StopNetworkAsync();
            Identity?.Dispose(); Identity = null; Listener = null; Discovery = null;
        }
        finally { _startup.Release(); }
    }
    public async Task<bool> PrepareForUpdateAsync()
    {
        if (!_draftsLoaded) throw new InvalidOperationException("本机草稿尚未正确读取，不能退出安装更新。");
        if (!_updateWork.TryBeginInstall()) return false;
        Changed();
        try
        {
            var drafts = Conversations.Where(c => c.Draft.Length > 0).Select(c => new ConversationDraft(c.Fingerprint, c.Name, c.Draft)).ToArray();
            await Task.Run(() => ConversationDrafts.Save(AppSettings.AppDataFolder, drafts));
            return true;
        }
        catch { CancelUpdatePreparation(); throw; }
    }
    public void CancelUpdatePreparation() { _updateWork.CancelInstall(); Changed(); }
    private void Changed() => Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
    private void Publish(string status) => Post(() => { Status = status; StatusChanged?.Invoke(this, status); });
    private void Fail(string message) { Publish(message); Post(() => Failed?.Invoke(this, message)); }
    private void Post(Action action)
    {
        if (_dispatcher?.HasThreadAccess != false) action(); else _dispatcher.TryEnqueue(() => action());
    }
    private Task OnUiAsync(Action action)
    {
        if (_dispatcher?.HasThreadAccess != false) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() => { try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } }))
            completion.TrySetException(new OperationCanceledException("窗口已退出。"));
        return completion.Task;
    }
}
