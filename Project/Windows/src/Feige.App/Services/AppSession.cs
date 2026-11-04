using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Discovery;
using Feige.Core.Net;
using Feige.Core.Profiles;
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
    private const int V3SlotFree = 0;
    private const int V3SlotActive = 1;
    // Keeps outbound connects from claiming the slot during an approved inbound handoff.
    private const int V3SlotReplacement = 2;

    public static AppSession Current { get; } = new();
    private DispatcherQueue? _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startup = new(1);
    private CancellationTokenSource? _connectionCts;
    private string? _connectionSlotFingerprint;
    private int _networkPathRefreshPending;
    private long _networkPathGeneration;
    private V3Connection? _connection;
    private V4Connection? _v4Connection;
    private PeerConnectionRegistry<V4Connection>? _v4Connections;
    public V4GroupAppService? Groups { get; private set; }
    private sealed class V4PeerRuntime(V4Connection connection, CancellationTokenSource cancellation)
    {
        public V4Connection Connection { get; } = connection;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public V4GroupConnectionRouter? GroupRouter { get; set; }
        public Task? Runner { get; set; }
    }
    private readonly object _v4Gate = new();
    private readonly Dictionary<string, V4PeerRuntime> _v4Runtimes = new(StringComparer.Ordinal);
    // Prevents repeated forced replacement while the newly accepted peer channel stays active.
    private readonly HashSet<string> _v4ReconnectReplacementPrimed = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _v4Pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _v4PendingByPeer = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sendingTextPeers = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sendingFilePeers = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _flushingOutbox = new(StringComparer.Ordinal);
    private Task? _outboxExpiry;
    private int _v4Connecting;
    private int _v4GroupConnecting;
    private bool _groupRosterSubscribed;
    private Task? _acceptLoop, _v4AcceptLoop, _runner;
    private V3Connection? _runnerConnection;
    private readonly object _v3SlotGate = new();
    private TaskCompletionSource<bool>? _v3SlotCompletion;
    private readonly object _v3RunnerGate = new();
    private readonly object _v3AcceptGate = new();
    private CancellationTokenSource? _v3PendingAcceptCts;
    private TaskCompletionSource<bool>? _v3PendingAcceptCompletion;
    private string? _v3PendingAcceptFingerprint;
    private CancellationTokenSource? _networkCts, _networkRefreshDelay;
    private UnicastProbe? _probe;
    private CancellationTokenSource? _probeCts;
    private Task? _probeLoop;
    private readonly Dictionary<string, ProbedPeer> _probedPeers = new(StringComparer.Ordinal);
    private int _connectionSlot, _sendingFiles, _sendingText;
    private bool V4GroupRoutingReady => Groups is not null;
    private bool _networkReady;
    private readonly UpdateWorkGate _updateWork = new();
    private readonly Dictionary<(string Fingerprint, Guid TransferId), TaskCompletionSource<bool>> _incomingDecisions = [];
    private readonly Dictionary<(string Fingerprint, Guid TransferId), IDisposable> _v4IncomingWork = [];
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
    public V4PeerListener? V4Listener { get; private set; }
    public LanDiscovery? Discovery { get; private set; }
    public AppSettings Settings { get; private set; } = AppSettings.Load();
    public string Status { get; private set; } = "正在准备安全连接…";
    public string? ActiveFingerprint { get; private set; }
    public int ListenPort => Listener?.Port ?? 0;
    public int V4ListenPort => V4Listener?.Port ?? 0;
    public bool IsRunning => _networkReady && Listener is not null && V4Listener is not null && FolderReady && Identity is not null;
    public bool FolderReady { get; private set; }
    public string IdentityStatus { get; private set; } = "等待准备";
    public string FolderStatus { get; private set; } = "等待检查";
    public string NetworkStatus { get; private set; } = "等待身份准备";
    public string? DiscoveryIssue { get; private set; }
    /// <summary>True when UDP 52526 was taken, so other subnets cannot find this device.</summary>
    public bool ProbePortUnavailable { get; private set; }

    private UnreadCounter? _unread;
    private UnreadCounter Unread => _unread ??= new(Path.Combine(AppSettings.AppDataFolder, "unread.json"));
    /// <summary>The conversation the main window shows: a fingerprint, a group key, or null.</summary>
    public string? VisibleConversationKey { get; private set; }
    /// <summary>True while the main window is the foreground window.</summary>
    public bool WindowActive { get; private set; }
    /// <summary>Raised on the UI thread whenever any unread count changes.</summary>
    public event EventHandler? UnreadChanged;
    public int UnreadCount(string key) => _messages?.IsHidden(key) == true ? 0 : Unread.Get(key);
    public int DirectUnreadTotal => Unread.Total(key => !key.StartsWith("group:", StringComparison.Ordinal) && _messages?.IsHidden(key) != true);
    public int GroupUnreadTotal => Unread.Total(key => key.StartsWith("group:", StringComparison.Ordinal));
    public int UnreadTotal => DirectUnreadTotal + GroupUnreadTotal;

    public void SetVisibleConversation(string? key)
    {
        VisibleConversationKey = key;
        if (key is not null && WindowActive) MarkConversationRead(key);
    }

    public void SetWindowActive(bool active)
    {
        WindowActive = active;
        if (active && VisibleConversationKey is { } key) MarkConversationRead(key);
    }

    public void MarkConversationRead(string key)
    {
        if (!Unread.Clear(key)) return;
        RefreshUnread(key);
    }

    /// <summary>Counts one arrival unless the user is looking at it, and shows a toast for text while the window is inactive.</summary>
    private void NoteIncoming(string key, string title, string body, bool notify)
    {
        if (WindowActive && VisibleConversationKey == key) return;
        Unread.Increment(key);
        RefreshUnread(key);
        if (notify && !WindowActive && Settings.NotificationsEnabled && Notifier.Current.IsAvailable)
            Notifier.Current.Show(title, body.Length > 120 ? body[..120] : body);
    }

    private void RefreshUnread(string key)
    {
        Conversations.FirstOrDefault(conversation => conversation.Fingerprint == key)?.SetUnread(UnreadCount(key));
        UnreadChanged?.Invoke(this, EventArgs.Empty);
    }
    public bool NotificationInvitationPending { get; private set; }
    public bool FirstTransferNoticePending { get; private set; }
    public bool IsStarting { get; private set; }
    public bool IsRefreshingPeers { get; private set; }
    public DateTimeOffset? LastPeerRefreshAt { get; private set; }
    public bool PeerRefreshFailed { get; private set; }
    public bool IsConnecting => (Volatile.Read(ref _connectionSlot) != V3SlotFree && _connection is null) || Volatile.Read(ref _v4Connecting) != 0 || Volatile.Read(ref _v4GroupConnecting) != 0;
    public bool IsSendingFiles => _sendingFiles != 0;
    public bool IsSendingText => _sendingText != 0;
    public bool CanConnect => IsRunning && !IsStarting && !IsInstallingUpdate && Settings.Setup.Ended && Volatile.Read(ref _connectionSlot) == V3SlotFree;
    public bool CanConnectV4 => IsRunning && !IsStarting && !IsInstallingUpdate && Settings.Setup.Ended &&
        (_v4Connections?.DirectPeerCount ?? 0) + Volatile.Read(ref _v4Connecting) < PeerConnectionRegistry<V4Connection>.MaximumDirectPeers;
    public bool IsConnectingTo(ConversationItem? conversation) => conversation is null
        ? IsConnecting
        : _v4PendingByPeer.ContainsKey(ConversationHistory.NormalizeFingerprint(conversation.Fingerprint)) ||
          (Volatile.Read(ref _connectionSlot) != V3SlotFree && _connection is null && _v4PendingByPeer.Count == 0);
    /// <summary>The discovered entry advertising this conversation's full fingerprint, if any (an unauthenticated hint).</summary>
    public DiscoveredPeer? NearbyPeer(ConversationItem? conversation)
        => conversation is null || AvatarIds.NormalizeFingerprint(conversation.Fingerprint) is not { } key ? null
            : Peers.Where(peer => AvatarIds.NormalizeFingerprint(peer.Fingerprint) == key).OrderByDescending(peer => peer.ProtocolVersion).FirstOrDefault();
    public bool IsConnected(ConversationItem? conversation)
    {
        if (conversation is null) return false;
        string fingerprint = ConversationHistory.NormalizeFingerprint(conversation.Fingerprint);
        return (_connection is not null && SameFingerprint(_connection.PeerFingerprint, fingerprint)) || (_v4Connections?.TryGet(fingerprint, out _) ?? false);
    }
    public bool IsSendingTextTo(ConversationItem? conversation) => conversation is not null && _sendingTextPeers.ContainsKey(ConversationHistory.NormalizeFingerprint(conversation.Fingerprint));
    public bool IsSendingFilesTo(ConversationItem? conversation) => conversation is not null && _sendingFilePeers.ContainsKey(ConversationHistory.NormalizeFingerprint(conversation.Fingerprint));
    public bool CanSend(ConversationItem? conversation) => !IsInstallingUpdate && conversation is not null && IsConnected(conversation);
    /// <summary>A known direct conversation also accepts messages offline; they wait in the outbox until the peer connects.</summary>
    public bool CanCompose(ConversationItem? conversation) => !IsInstallingUpdate && conversation is not null && _messages is not null &&
        (IsConnected(conversation) || AvatarIds.NormalizeFingerprint(conversation.Fingerprint) is not null);
    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        long generation = Interlocked.Increment(ref _networkPathGeneration);
        Interlocked.Exchange(ref _networkPathRefreshPending, 1);
        CancelV3PendingAccept();
        Post(() =>
        {
            if (_lifetime.IsCancellationRequested || generation != Volatile.Read(ref _networkPathGeneration)) return;
            _networkRefreshDelay?.Cancel();
            var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _networkRefreshDelay = delay;
            _ = RefreshAfterNetworkChangeAsync(delay, generation);
        });
    }
    private async Task RefreshAfterNetworkChangeAsync(CancellationTokenSource delay, long generation)
    {
        try
        {
            await Task.Delay(750, delay.Token);
            await DisconnectAsync();
            delay.Token.ThrowIfCancellationRequested();
            await RefreshDiscoveryAsync(delay.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("network refresh: " + ex.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_networkRefreshDelay, delay))
            {
                _networkRefreshDelay = null;
                if (!delay.IsCancellationRequested && generation == Volatile.Read(ref _networkPathGeneration))
                    Interlocked.Exchange(ref _networkPathRefreshPending, 0);
            }
            delay.Dispose();
        }
    }

    public void ReloadSettings() { Settings = AppSettings.Load(); Changed(); }

    public void SaveGuideSettings(string name, string folder, bool automaticUpdates, string? avatarId = null)
    {
        Settings.SaveSetup(name, folder, automaticUpdates, avatarId);
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
        if (_outboxExpiry is null) _outboxExpiry = RunOutboxExpiryAsync(_lifetime.Token);
        else _ = ExpireOutboxAsync();
        UnreadChanged?.Invoke(this, EventArgs.Empty);
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
            PeerConnectionRegistry<V4Connection> registry = _v4Connections ??= new PeerConnectionRegistry<V4Connection>(ConversationHistory.NormalizeFingerprint(Identity!.Fingerprint));
            V4GroupAppService groups = Groups ??= new V4GroupAppService(AppSettings.AppDataFolder, Identity!.Fingerprint,
                peer => registry.TryGet(peer, out var connected) ? connected : null);
            if (!_groupRosterSubscribed)
            {
                groups.Groups.CollectionChanged += (_, _) => Post(ConnectEligibleGroupPeers);
                _groupRosterSubscribed = true;
            }
            groups.SetRegistry(registry);
            groups.EventReceived -= OnGroupEventReceived;
            groups.EventReceived += OnGroupEventReceived;
            IdentityStatus = "已就绪"; Changed();
            stage = "folder";
            if (!FolderReady)
            {
                FolderStatus = "检查中"; Changed();
                await Task.Run(() => SetupValidation.ReceiveFolder(Settings.ReceiveFolder));
                ct.ThrowIfCancellationRequested(); FolderReady = true;
            }
            FolderStatus = "可以保存文件"; Changed();
            _ = Task.Run(() => ResumableStorageCleanup.RemoveExpired(Settings.ReceiveFolder));
            stage = "network"; NetworkStatus = "正在启动"; Changed();
            ct.ThrowIfCancellationRequested();
            Device = new LocalDevice(Identity, Settings.DeviceName);
            _networkCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            using var stopNetwork = ct.Register(() => _networkCts?.Cancel());
            Listener = new V3PeerListener(); Listener.Start();
            V4Listener = new V4PeerListener(); V4Listener.Start();
            await StartDiscoveryAsync(Device, _networkCts.Token);
            ct.ThrowIfCancellationRequested();
            _networkReady = true;
            ConnectEligibleGroupPeers();
            NetworkStatus = DiscoveryIssue is null ? "查找附近设备" : "直连已就绪，自动发现暂不可用";
            var networkToken = _networkCts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(networkToken));
            _v4AcceptLoop = Task.Run(() => V4AcceptLoopAsync(networkToken));
            Publish(DiscoveryIssue is null ? $"已就绪 · {Device.DeviceName} · v3/v4 端口 {ListenPort}/{V4ListenPort}" : "自动发现暂不可用，可刷新或手动连接。");
            AppLog.Write($"started: protocol={FeigeService.Capabilities} listeners={ListenPort},{V4ListenPort} messages={_messages!.Messages.Count}");
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
        await StopProbeAsync();
        if (Discovery is not null) await Discovery.DisposeAsync();
        if (Listener is not null) await Listener.DisposeAsync();
        if (V4Listener is not null) await V4Listener.DisposeAsync();
        if (_acceptLoop is not null) { try { await _acceptLoop; } catch (OperationCanceledException) { } }
        if (_v4AcceptLoop is not null) { try { await _v4AcceptLoop; } catch (OperationCanceledException) { } }
        Discovery = null; Listener = null; V4Listener = null; _acceptLoop = null; _v4AcceptLoop = null;
        _networkCts?.Dispose(); _networkCts = null; DiscoveryIssue = null; Peers.Clear();
    }

    private async Task StartDiscoveryAsync(LocalDevice device, CancellationToken ct)
    {
        DiscoveryIssue = null;
        var discovery = CreateDiscovery(device); Discovery = discovery;
        try
        {
            if (V4GroupRoutingReady) await discovery.StartAsync(ct);
            else
            {
                await discovery.V3Advertiser!.StartAsync(ct);
                await discovery.Browser.StartAsync(ct);
            }
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            Discovery = null;
            try { await discovery.DisposeAsync(); } catch (Exception cleanup) { AppLog.Write("discovery cleanup: " + cleanup.GetType().Name); }
            DiscoveryIssue = "自动发现暂不可用，请检查 VPN/TUN、网络隔离或 UDP 5353 是否可用。仍可手动输入对方的局域网地址和端口。";
            AppLog.Write("discovery unavailable: " + ex.GetType().Name);
        }
        StartProbe(device, ct);
    }

    private sealed class ProbedPeer(DiscoveredPeer peer)
    {
        public DiscoveredPeer Peer { get; set; } = peer;
        public int MissedRounds { get; set; }
        public bool SeenThisRound { get; set; } = true;
    }

    private void StartProbe(LocalDevice device, CancellationToken ct)
    {
        string[] saved = Settings.ProbeTargets.ToArray();
        if (!Settings.AllowsProbes && ProbeTargets.RoundHosts(saved, FeigeService.LocalAddresses()).Count == 0 || V4ListenPort == 0) return;
        string fingerprint = device.Fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var probe = new UnicastProbe(new(device.DeviceName, fingerprint, FeigeService.Capabilities, V4ListenPort,
            AvatarIds.IsKnown(Settings.AvatarId) ? Settings.AvatarId : null), Settings.AllowsProbes);
        probe.PeerFound += peer => Post(() => RecordProbedPeer(probe, peer));
        try { probe.Start(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            AppLog.Write("unicast probe unavailable: " + ex.GetType().Name);
            _ = probe.DisposeAsync();
            return;
        }
        _probe = probe;
        ProbePortUnavailable = Settings.AllowsProbes && !probe.CanRespond;
        if (ProbePortUnavailable) AppLog.Write("unicast probe: port unavailable");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _probeCts = cancellation;
        _probeLoop = RunProbeLoopAsync(probe, saved, cancellation.Token);
    }

    private async Task RunProbeLoopAsync(UnicastProbe probe, string[] saved, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Recomputed each round: a laptop joining or leaving the company network starts or stops the company subnets.
                var hosts = ProbeTargets.RoundHosts(saved, FeigeService.LocalAddresses());
                if (hosts.Count > 0) await probe.ScanAsync(hosts, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                Post(() => { if (ReferenceEquals(_probe, probe)) FinishProbeRound(); });
                await Task.Delay(TimeSpan.FromSeconds(58), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RecordProbedPeer(UnicastProbe probe, ProbePeer found)
    {
        if (!ReferenceEquals(_probe, probe)) return;
        var peer = new DiscoveredPeer(new DnsName("Feige4-" + found.Fingerprint[..20]), found.Name, found.Fingerprint, new IPEndPoint(found.Address, found.Port))
        {
            ProtocolVersion = found.ProtocolVersions.Where(version => version <= V4Offer.ResumableVersion).DefaultIfEmpty(0).Max(),
            SupportedVersions = found.ProtocolVersions,
            AvatarId = AvatarIds.IsKnown(found.AvatarId) ? found.AvatarId : null,
        };
        string identity = PeerIdentity(peer);
        if (_probedPeers.TryGetValue(identity, out var entry)) { entry.Peer = peer; entry.SeenThisRound = true; entry.MissedRounds = 0; }
        else _probedPeers[identity] = new(peer);
        var existing = Peers.FirstOrDefault(candidate => PeerIdentity(candidate) == identity);
        if (existing is null) Peers.Add(peer);
        else if (_probedPeers.ContainsKey(identity) && !(Discovery?.Peers.Any(candidate => PeerIdentity(candidate) == identity) ?? false))
            Peers[Peers.IndexOf(existing)] = peer; // mDNS wins whenever it also sees this device.
        else return;
        foreach (var conversation in Conversations) conversation.ApplyDiscoveredName(peer);
        ConnectEligibleGroupPeers();
    }

    /// <summary>A device disappears after three rounds without a reply.</summary>
    private void FinishProbeRound()
    {
        foreach (var (identity, entry) in _probedPeers.ToArray())
        {
            entry.MissedRounds = entry.SeenThisRound ? 0 : entry.MissedRounds + 1;
            entry.SeenThisRound = false;
            if (entry.MissedRounds < 3) continue;
            _probedPeers.Remove(identity);
            if (Discovery?.Peers.Any(candidate => PeerIdentity(candidate) == identity) ?? false) continue;
            var existing = Peers.FirstOrDefault(candidate => PeerIdentity(candidate) == identity);
            if (existing is not null) Peers.Remove(existing);
        }
    }

    private async Task StopProbeAsync()
    {
        _probeCts?.Cancel();
        if (_probeLoop is not null) await _probeLoop;
        if (_probe is not null) await _probe.DisposeAsync();
        _probeCts?.Dispose();
        _probe = null; _probeCts = null; _probeLoop = null;
        _probedPeers.Clear();
    }

    /// <summary>Saves the subnets and restarts discovery so the first round runs at once.</summary>
    public async Task SaveProbeSettingsAsync(string[] targets, bool allowsProbes)
    {
        string[] cleaned = targets.Select(ProbeTargets.Normalize).Where(target => target.Length > 0).ToArray();
        if (ProbeTargets.Hosts(cleaned) is null) throw new InvalidOperationException("只能填写私网 IPv4 地址、三段网段（如 10.80.1）或 /24–/32 网段，最多 16 项。");
        Settings.SaveProbeSettings(cleaned, allowsProbes);
        if (IsRunning) await RefreshDiscoveryAsync(_lifetime.Token);
    }

    private async Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        await _startup.WaitAsync(ct);
        try
        {
            // Rebuild only discovery. Established TCP sessions and their consent remain untouched.
            if (!IsRunning || Device is null || _lifetime.IsCancellationRequested) return;
            await StopProbeAsync();
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
        var discovery = new LanDiscovery(device, ListenPort, V4ListenPort, avatarId: Settings.AvatarId);
        discovery.PeerFound += (_, peer) =>
        {
            Post(() =>
            {
                if (!ReferenceEquals(Discovery, discovery)) return;
                var identity = PeerIdentity(peer);
                var preferred = discovery.Peers
                    .Where(candidate => PeerIdentity(candidate) == identity)
                    .OrderByDescending(candidate => candidate.ProtocolVersion)
                    .FirstOrDefault();
                if (preferred is null) return;
                foreach (var conversation in Conversations) conversation.ApplyDiscoveredName(preferred);
                var existing = Peers.FirstOrDefault(candidate => PeerIdentity(candidate) == identity);
                if (existing is not null) Peers[Peers.IndexOf(existing)] = preferred; else Peers.Add(preferred);
                CacheAvatarForAuthenticatedPeer(preferred.Fingerprint);
                ConnectEligibleGroupPeers();
                ConnectTrustedPeers();
            });
        };
        discovery.PeerLost += (_, peer) => Post(() =>
        {
            if (!ReferenceEquals(Discovery, discovery)) return;
            var identity = PeerIdentity(peer);
            var preferred = discovery.Peers
                .Where(candidate => PeerIdentity(candidate) == identity)
                .OrderByDescending(candidate => candidate.ProtocolVersion)
                .FirstOrDefault();
            var existing = Peers.FirstOrDefault(candidate => PeerIdentity(candidate) == identity);
            if (preferred is null)
            {
                if (existing is not null) Peers.Remove(existing);
            }
            else if (existing is not null) Peers[Peers.IndexOf(existing)] = preferred;
            else Peers.Add(preferred);
        });
        return discovery;
    }

    private void ConnectEligibleGroupPeers()
    {
        if (!V4GroupRoutingReady || !IsRunning || Groups is null || Identity is null || _v4Connections is null)
        {
            return;
        }
        string local = ConversationHistory.NormalizeFingerprint(Identity.Fingerprint);
        foreach (GroupConversation group in Groups.Groups)
        {
            bool localMember = group.Members.Any(member => SameFingerprint(member.Fingerprint, local));
            if (!localMember) continue;
            foreach (GroupMember member in group.Members)
            {
                string peerFingerprint = ConversationHistory.NormalizeFingerprint(member.Fingerprint);
                if (peerFingerprint == local || string.CompareOrdinal(local, peerFingerprint) >= 0) continue;
                if (_v4Connections.TryGet(peerFingerprint, out var active) && active is { IsClosed: false }) continue;
                if (_v4PendingByPeer.ContainsKey(peerFingerprint)) continue;
                DiscoveredPeer? peer = Peers.FirstOrDefault(candidate => candidate.ProtocolVersion >= 4 &&
                    !string.IsNullOrWhiteSpace(candidate.Fingerprint) &&
                    ConversationHistory.NormalizeFingerprint(candidate.Fingerprint) == peerFingerprint);
                if (peer is not null) _ = ConnectGroupPeerAsync(peer);
            }
        }
    }

    // Persistent contacts: Settings.TrustedPeers holds normalized fingerprints; the planner holds per-run dial state.
    private readonly TrustedDialPlanner _trustedDial = new();
    private readonly object _trustedGate = new();
    private CancellationTokenSource? _trustedDialTimer;

    public bool IsTrustedPeer(string? fingerprint)
        => AvatarIds.NormalizeFingerprint(fingerprint) is { } key && Settings.TrustedPeers.Contains(key);

    public void SetTrustedPeer(string fingerprint, bool trusted)
    {
        if (AvatarIds.NormalizeFingerprint(fingerprint) is not { } key) return;
        var next = Settings.TrustedPeers.Where(item => item != key).ToList();
        if (trusted) next.Add(key);
        Settings.SaveTrustedPeers([.. TrustedDialPlanner.Normalize(next)]);
        lock (_trustedGate) _trustedDial.Reset(key, clearSuppression: true);
        Changed();
        if (trusted) ConnectTrustedPeers();
    }

    public string TrustedPeerName(string fingerprint)
    {
        DiscoveredPeer? peer = Peers.FirstOrDefault(candidate => AvatarIds.NormalizeFingerprint(candidate.Fingerprint) == fingerprint);
        if (peer is not null) return peer.DisplayName;
        ConversationItem? conversation = Conversations.FirstOrDefault(item => AvatarIds.NormalizeFingerprint(item.Fingerprint) == fingerprint);
        return conversation?.Name ?? fingerprint[..12];
    }

    /// <summary>Approves a persistent contact without a prompt; file approval is unaffected.</summary>
    private async Task<bool> ConfirmIncomingConnectionAsync(string name, string peerFingerprint, CancellationToken ct)
    {
        string? key = AvatarIds.NormalizeFingerprint(peerFingerprint);
        if (key is not null && Settings.TrustedPeers.Contains(key))
        {
            bool suppressed;
            lock (_trustedGate) suppressed = _trustedDial.IsSuppressed(key);
            if (!suppressed)
            {
                AppLog.Write($"connection auto-approved for persistent contact peer={key[..8]}");
                return true;
            }
        }
        var consent = await UiPromptService.Current.ConfirmConnectionAsync(name, Settings.ReceiveFolder, ct);
        // Approval is asked once per device: an accepted peer becomes a persistent contact.
        if (consent == ConnectionConsent.Accepted && key is not null) Post(() => SetTrustedPeer(key, true));
        return consent != ConnectionConsent.Rejected;
    }

    private void OnTrustedPeerClosed(string fingerprint)
    {
        if (!IsTrustedPeer(fingerprint)) return;
        lock (_trustedGate) _trustedDial.RecordDisconnect(fingerprint, DateTimeOffset.Now);
        ConnectTrustedPeers();
    }

    /// <summary>Dials discovered persistent contacts that have no direct connection, within the 20-peer direct limit.</summary>
    private void ConnectTrustedPeers()
    {
        _trustedDialTimer?.Cancel();
        _trustedDialTimer?.Dispose();
        _trustedDialTimer = null;
        var registry = _v4Connections;
        if (!IsRunning || Identity is null || registry is null || !Settings.Setup.Ended || Settings.TrustedPeers.Count == 0) return;
        string local = ConversationHistory.NormalizeFingerprint(Identity.Fingerprint);
        V3Connection? v3;
        lock (_v3SlotGate) v3 = _connection;
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset? wake = null;
        foreach (string key in Settings.TrustedPeers)
        {
            bool connected = (registry.TryGet(key, out var active) && active is { IsClosed: false } && registry.IsDirect(key)) ||
                (v3 is not null && SameFingerprint(v3.PeerFingerprint, key));
            if (connected || _v4PendingByPeer.ContainsKey(key)) continue;
            DiscoveredPeer? peer = Peers
                .Where(candidate => AvatarIds.NormalizeFingerprint(candidate.Fingerprint) == key && candidate.ProtocolVersion >= 3)
                .OrderByDescending(candidate => candidate.ProtocolVersion)
                .FirstOrDefault();
            DateTimeOffset? due;
            lock (_trustedGate)
            {
                if (peer is null) { _trustedDial.MarkUnseen(key); continue; }
                due = _trustedDial.DueAt(key, local, now);
            }
            if (due is not { } dueAt) continue;
            if (dueAt > now)
            {
                if (wake is null || dueAt < wake) wake = dueAt;
                continue;
            }
            if (registry.DirectPeerCount + Volatile.Read(ref _v4Connecting) >= PeerConnectionRegistry<V4Connection>.MaximumDirectPeers) break;
            _ = DialTrustedPeerAsync(peer!, key);
        }
        if (wake is not { } wakeAt) return;
        var timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _trustedDialTimer = timer;
        _ = WakeTrustedDialAsync(wakeAt - now, timer.Token);
    }

    private async Task WakeTrustedDialAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, ct);
            Post(ConnectTrustedPeers);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task DialTrustedPeerAsync(DiscoveredPeer peer, string key)
    {
        try
        {
            await ConnectAsync(peer.EndPoints, peer.ProtocolVersion, key, direct: true, quiet: true);
            Post(() => { lock (_trustedGate) _trustedDial.Reset(key, clearSuppression: false); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            bool rejected = ex is ConnectionRejectedException;
            AppLog.Write($"persistent contact dial failed peer={key[..8]} type={ex.GetType().Name}");
            Post(() =>
            {
                lock (_trustedGate) _trustedDial.RecordFailure(key, rejected, DateTimeOffset.Now);
                ConnectTrustedPeers();
            });
        }
    }

    private async Task ConnectGroupPeerAsync(DiscoveredPeer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Fingerprint)) return;
        string fingerprint = ConversationHistory.NormalizeFingerprint(peer.Fingerprint);
        try { await ConnectAsync(peer.EndPoints, peer.ProtocolVersion, fingerprint, direct: false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private static bool SameFingerprint(string first, string second)
        => ConversationHistory.NormalizeFingerprint(first) == ConversationHistory.NormalizeFingerprint(second);

    private static string PeerIdentity(DiscoveredPeer peer)
        => string.IsNullOrWhiteSpace(peer.Fingerprint)
            ? "instance:" + peer.Instance
            : "fingerprint:" + ConversationHistory.NormalizeFingerprint(peer.Fingerprint);

    /// <summary>Persist cosmetic metadata only when a live authenticated v4 connection proves this full fingerprint.</summary>
    private void CacheAvatarForAuthenticatedPeer(string? peerFingerprint)
    {
        string? fingerprint = AvatarIds.NormalizeFingerprint(peerFingerprint);
        if (fingerprint is null || _v4Connections is null || !_v4Connections.TryGet(fingerprint, out var connection) ||
            connection is not { IsClosed: false } || AvatarIds.NormalizeFingerprint(connection.PeerFingerprint) != fingerprint)
            return;
        DiscoveredPeer? advertisement = Peers.FirstOrDefault(peer =>
            AvatarIds.NormalizeFingerprint(peer.Fingerprint) == fingerprint && AvatarIds.IsKnown(peer.AvatarId));
        if (advertisement is null || !Settings.SavePeerAvatarId(fingerprint, advertisement.AvatarId)) return;
        foreach (ConversationItem conversation in Conversations.Where(item => AvatarIds.NormalizeFingerprint(item.Fingerprint) == fingerprint))
            conversation.RefreshAvatar();
        Changed();
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

    public Task ConnectAsync(IPEndPoint endpoint) => ConnectAsync([endpoint], 3);
    public Task ConnectAsync(DiscoveredPeer peer)
    {
        // A manual connect lifts a suppression left by a declined dial or a manual disconnect.
        if (AvatarIds.NormalizeFingerprint(peer.Fingerprint) is { } trustedKey)
            lock (_trustedGate) _trustedDial.Reset(trustedKey, clearSuppression: true);
        return ConnectDiscoveredAsync(peer);
    }

    private Task ConnectDiscoveredAsync(DiscoveredPeer peer) => peer.ProtocolVersion switch
    {
        3 => ConnectAsync(peer.EndPoints, 3,
            string.IsNullOrWhiteSpace(peer.Fingerprint) ? null : ConversationHistory.NormalizeFingerprint(peer.Fingerprint)),
        4 when !string.IsNullOrWhiteSpace(peer.Fingerprint) => ConnectAsync(peer.EndPoints, 4, ConversationHistory.NormalizeFingerprint(peer.Fingerprint)),
        4 => Task.FromException(new InvalidOperationException("发现信息未包含有效的 v4 设备身份，请刷新设备列表后重试。")),
        5 when !string.IsNullOrWhiteSpace(peer.Fingerprint) => ConnectAsync(peer.EndPoints, 5, ConversationHistory.NormalizeFingerprint(peer.Fingerprint)),
        5 => Task.FromException(new InvalidOperationException("发现信息未包含有效的 v5 设备身份，请刷新设备列表后重试。")),
        6 when !string.IsNullOrWhiteSpace(peer.Fingerprint) => ConnectAsync(peer.EndPoints, 6, ConversationHistory.NormalizeFingerprint(peer.Fingerprint)),
        6 => Task.FromException(new InvalidOperationException("发现信息未包含有效的 v6 设备身份，请刷新设备列表后重试。")),
        _ => Task.FromException(new InvalidOperationException("对方未公布受支持的协议版本，请刷新设备列表后重试。")),
    };

    private async Task ConnectAsync(IReadOnlyList<IPEndPoint> endpoints, int protocolVersion, string? expectedFingerprint = null,
        bool direct = true, bool quiet = false)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后连接。");
        if (!IsRunning || !Settings.Setup.Ended || Device is null) throw new InvalidOperationException("安全连接尚未准备好，请稍后重试。");
        var existingRegistry = _v4Connections;
        if (protocolVersion >= 4 && expectedFingerprint is not null && existingRegistry is not null)
        {
            V4Connection? reusable = null;
            lock (_v4Gate)
            {
                if (existingRegistry.TryGet(expectedFingerprint, out var current) && current is not null)
                {
                    if (current.IsClosed)
                    {
                        existingRegistry.Remove(expectedFingerprint, current);
                        if (!existingRegistry.TryGet(expectedFingerprint, out _))
                            _v4ReconnectReplacementPrimed.Remove(expectedFingerprint);
                    }
                    else
                    {
                        reusable = current;
                        if (direct && !existingRegistry.IsDirect(expectedFingerprint))
                            reusable = existingRegistry.Register(current, expectedFingerprint, current.InitiatorFingerprint,
                                current.TranscriptDigest, direct: true).Active;
                    }
                }
            }
            if (reusable is not null)
            {
                var reusableConnection = reusable;
                bool stillActive = false;
                if (direct)
                    await OnUiAsync(() =>
                    {
                        lock (_v4Gate)
                        {
                            if (reusableConnection.IsClosed || !existingRegistry.TryGet(expectedFingerprint, out var current) ||
                                !ReferenceEquals(current, reusableConnection)) return;
                            _v4Connection = reusableConnection; ActiveFingerprint = expectedFingerprint;
                            var conversation = FindConversation(expectedFingerprint, reusableConnection.PeerName);
                            conversation.Name = reusableConnection.PeerName; conversation.State = "已连接";
                            Publish("已连接 · " + reusableConnection.PeerName); Changed();
                            stillActive = true;
                        }
                    });
                else
                    lock (_v4Gate)
                        stillActive = !reusableConnection.IsClosed && existingRegistry.TryGet(expectedFingerprint, out var current) &&
                            ReferenceEquals(current, reusableConnection);
                if (stillActive) return;
            }
        }
        bool groupOnlyReservation = protocolVersion >= 4 && !direct;
        var networkToken = _networkCts?.Token ?? _lifetime.Token;
        CancellationTokenSource? v3Cancellation = null;
        if (protocolVersion == 3)
        {
            v3Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, networkToken);
            if (!TryTransitionV3Slot(V3SlotFree, V3SlotActive, expectedFingerprint, v3Cancellation))
            {
                v3Cancellation.Dispose();
                throw new InvalidOperationException("v3 当前仅支持一个活动连接。");
            }
        }
        else if (protocolVersion >= 4)
        {
            if (_v4Connections is null) throw new InvalidOperationException("v4 连接注册表尚未准备好。");
            if (direct)
            {
                if (_v4Connections.DirectPeerCount + Volatile.Read(ref _v4Connecting) >= PeerConnectionRegistry<V4Connection>.MaximumDirectPeers)
                    throw new InvalidOperationException("直连设备已达到 20 台上限。");
                Interlocked.Increment(ref _v4Connecting);
            }
            else
            {
                int groupOnlyCount = _v4Connections.ConnectionCount - _v4Connections.DirectPeerCount;
                if (groupOnlyCount + Volatile.Read(ref _v4GroupConnecting) >= PeerConnectionRegistry<V4Connection>.MaximumGroupOnlyPeers ||
                    _v4Connections.ConnectionCount + Volatile.Read(ref _v4Connecting) + Volatile.Read(ref _v4GroupConnecting) >= PeerConnectionRegistry<V4Connection>.MaximumConnections)
                    throw new InvalidOperationException("群组专用连接已达到容量上限。");
                Interlocked.Increment(ref _v4GroupConnecting);
            }
        }
        else throw new InvalidOperationException("不支持的协议版本。");

        var cancellation = v3Cancellation ?? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, networkToken);
        Guid pendingId = Guid.NewGuid();
        if (protocolVersion >= 4)
        {
            _v4Pending[pendingId] = cancellation;
            if (expectedFingerprint is not null && !_v4PendingByPeer.TryAdd(expectedFingerprint, cancellation))
            {
                _v4Pending.TryRemove(pendingId, out _);
                if (groupOnlyReservation) Interlocked.Decrement(ref _v4GroupConnecting);
                else Interlocked.Decrement(ref _v4Connecting);
                cancellation.Dispose();
                throw new InvalidOperationException("正在连接此设备，请稍后重试。");
            }
        }
        Changed();
        bool attached = false;
        try
        {
            Publish(direct ? "正在建立网络、交换并验证设备身份，随后等待对方同意…" : "正在建立已批准群成员的安全连接…");
            if (protocolVersion == 3)
            {
                var connection = await Task.Run(() => V3PeerConnector.ConnectAsync(endpoints, Device, cancellation.Token));
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (expectedFingerprint is not null &&
                        ConversationHistory.NormalizeFingerprint(connection.PeerFingerprint) != expectedFingerprint)
                        throw new InvalidDataException("连接设备身份与发现信息不一致。");
                    await AttachAsync(connection, cancellation);
                    attached = true;
                }
                catch { connection.Close(); throw; }
            }
            else
            {
                var connection = await Task.Run(() => V4PeerConnector.ConnectAsync(endpoints, Device,
                    checked((ushort)protocolVersion), cancellation.Token));
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (expectedFingerprint is not null && ConversationHistory.NormalizeFingerprint(connection.PeerFingerprint) != expectedFingerprint)
                        throw new InvalidDataException("连接设备身份与发现信息不一致。");
                    attached = await AttachV4Async(connection, cancellation, direct);
                    if (attached) Post(() => CacheAvatarForAuthenticatedPeer(connection.PeerFingerprint));
                }
                catch { connection.Close(); throw; }
            }
        }
        catch (Exception ex)
        {
            if (direct && !quiet) Fail(ErrorText.Describe(ex));
            throw;
        }
        finally
        {
            if (protocolVersion == 3)
            {
                if (!attached)
                {
                    cancellation.Dispose();
                    TryTransitionV3Slot(V3SlotActive, V3SlotFree, null, expectedConnectionCts: cancellation);
                    Changed();
                }
            }
            else
            {
                _v4Pending.TryRemove(pendingId, out _);
                if (expectedFingerprint is not null) _v4PendingByPeer.TryRemove(expectedFingerprint, out _);
                if (groupOnlyReservation) Interlocked.Decrement(ref _v4GroupConnecting);
                else Interlocked.Decrement(ref _v4Connecting);
                if (!attached) cancellation.Dispose();
                Changed();
            }
        }
    }

    // Keep slot state and its peer identity in one short transition; no caller awaits while holding the gate.
    private bool TryTransitionV3Slot(int expected, int next, string? fingerprint,
        CancellationTokenSource? connectionCts = null, CancellationTokenSource? expectedConnectionCts = null,
        string? expectedFingerprint = null)
    {
        lock (_v3SlotGate)
        {
            if (Volatile.Read(ref _connectionSlot) != expected) return false;
            if (expectedConnectionCts is not null && !ReferenceEquals(_connectionCts, expectedConnectionCts)) return false;
            if (expectedFingerprint is not null &&
                (_connectionSlotFingerprint is null || !SameFingerprint(_connectionSlotFingerprint, expectedFingerprint))) return false;
            if (next == V3SlotFree && expectedConnectionCts is null && expectedFingerprint is null) return false;
            if (connectionCts is not null) _connectionCts = connectionCts;
            if (expected == V3SlotFree && next != V3SlotFree)
                _v3SlotCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _connectionSlotFingerprint, next == V3SlotFree ? null : fingerprint);
            Volatile.Write(ref _connectionSlot, next);
            if (next == V3SlotFree)
            {
                _connectionCts = null;
                var completion = _v3SlotCompletion;
                _v3SlotCompletion = null;
                completion?.TrySetResult(true);
            }
            return true;
        }
    }

    private (bool Targets, Task? Completion, CancellationTokenSource? Cancellation, V3Connection? Connection)
        GetV3SlotDrain(string? fingerprint)
    {
        lock (_v3SlotGate)
        {
            if (Volatile.Read(ref _connectionSlot) == V3SlotFree) return (false, null, null, null);
            var active = _connection;
            bool matches = fingerprint is null ||
                (_connectionSlotFingerprint is not null && SameFingerprint(_connectionSlotFingerprint, fingerprint)) ||
                (active is not null && SameFingerprint(active.PeerFingerprint, fingerprint));
            return (matches, matches ? _v3SlotCompletion?.Task : null, matches ? _connectionCts : null,
                matches ? active : null);
        }
    }

    private async Task<(bool Reserved, V3Connection? Active, CancellationTokenSource? ActiveCancellation)>
        TryReserveV3ReplacementSlotAsync(string fingerprint, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slot;
            V3Connection? active;
            CancellationTokenSource? activeCancellation;
            string? pendingFingerprint;
            lock (_v3SlotGate)
            {
                slot = Volatile.Read(ref _connectionSlot);
                active = _connection;
                activeCancellation = _connectionCts;
                pendingFingerprint = _connectionSlotFingerprint;
            }
            if (slot == V3SlotReplacement) return (false, null, null);
            if (slot == V3SlotFree)
            {
                if (TryTransitionV3Slot(V3SlotFree, V3SlotReplacement, fingerprint)) return (true, null, null);
                continue;
            }
            if (slot != V3SlotActive) return (false, null, null);

            if (active is not null)
            {
                if (!SameFingerprint(active.PeerFingerprint, fingerprint) || activeCancellation is null)
                    return (false, null, null);
                if (TryTransitionV3Slot(V3SlotActive, V3SlotReplacement, fingerprint,
                    expectedConnectionCts: activeCancellation, expectedFingerprint: pendingFingerprint))
                    return (true, active, activeCancellation);
                continue;
            }

            // Let a same-peer outbound attempt finish; keep unrelated attempts and reject only this inbound candidate.
            if (pendingFingerprint is not null && !SameFingerprint(pendingFingerprint, fingerprint))
                return (false, null, null);
            await Task.Delay(10, cancellationToken);
        }
    }

    private async Task WaitForV3RunnerAsync(V3Connection connection, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? runner;
            lock (_v3RunnerGate)
                runner = ReferenceEquals(_runnerConnection, connection) ? _runner : null;
            if (runner is not null)
            {
                await runner.WaitAsync(cancellationToken);
                return;
            }
            bool stillAttached;
            lock (_v3SlotGate) stillAttached = ReferenceEquals(_connection, connection);
            if (!stillAttached) return;
            await Task.Delay(10, cancellationToken);
        }
    }

    private bool TryBeginV3PendingAccept(string fingerprint, CancellationTokenSource cancellation,
        TaskCompletionSource<bool> completion)
    {
        lock (_v3AcceptGate)
        {
            if (_v3PendingAcceptCts is not null) return false;
            _v3PendingAcceptCts = cancellation;
            _v3PendingAcceptCompletion = completion;
            _v3PendingAcceptFingerprint = fingerprint;
            return true;
        }
    }

    private Task? CancelV3PendingAccept(string? fingerprint = null)
    {
        CancellationTokenSource? cancellation;
        Task? completion;
        lock (_v3AcceptGate)
        {
            cancellation = _v3PendingAcceptCts;
            if (cancellation is null || (fingerprint is not null &&
                (_v3PendingAcceptFingerprint is null || !SameFingerprint(_v3PendingAcceptFingerprint, fingerprint))))
                return null;
            completion = _v3PendingAcceptCompletion?.Task;
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return completion;
    }

    private void EndV3PendingAccept(CancellationTokenSource cancellation, TaskCompletionSource<bool> completion)
    {
        lock (_v3AcceptGate)
        {
            if (ReferenceEquals(_v3PendingAcceptCts, cancellation))
            {
                _v3PendingAcceptCts = null;
                _v3PendingAcceptCompletion = null;
                _v3PendingAcceptFingerprint = null;
            }
        }
        cancellation.Dispose();
        completion.TrySetResult(true);
    }

    private async Task AcceptLoopAsync(CancellationToken networkToken)
    {
        while (!networkToken.IsCancellationRequested)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(networkToken);
            CancellationTokenSource? pendingAcceptCts = null;
            TaskCompletionSource<bool>? pendingAcceptCompletion = null;
            CancellationTokenRegistration pendingAcceptRegistration = default;
            V3Connection? replacementPrevious = null;
            string? reservedFingerprint = null;
            bool reserved = false, attached = false;
            IDisposable? acceptingWork = null;
            try
            {
                var connection = await Listener!.AcceptAsync(() => Device!, async (name, fingerprint, ct) =>
                {
                    if (!Settings.Setup.Ended || !IsRunning || Volatile.Read(ref _networkPathRefreshPending) != 0) return false;
                    acceptingWork = _updateWork.TryEnter();
                    if (acceptingWork is null) return false;

                    var candidateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, ct);
                    var candidateCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    string peerFingerprint = ConversationHistory.NormalizeFingerprint(fingerprint);
                    if (!TryBeginV3PendingAccept(peerFingerprint, candidateCancellation, candidateCompletion))
                    {
                        candidateCancellation.Dispose();
                        return false;
                    }
                    pendingAcceptCts = candidateCancellation;
                    pendingAcceptCompletion = candidateCompletion;
                    pendingAcceptRegistration = candidateCancellation.Token.Register(static state =>
                    {
                        try { ((CancellationTokenSource)state!).Cancel(); } catch (ObjectDisposedException) { }
                    }, cancellation);

                    bool accepted;
                    if (TryTransitionV3Slot(V3SlotFree, V3SlotActive, peerFingerprint, cancellation))
                    {
                        reserved = true;
                        reservedFingerprint = peerFingerprint;
                        Publish("收到连接请求，等待本机同意…"); Changed();
                        accepted = await ConfirmIncomingConnectionAsync(name, peerFingerprint, candidateCancellation.Token);
                        if (!accepted) return false;
                        candidateCancellation.Token.ThrowIfCancellationRequested();
                        return true;
                    }

                    V3Connection? active;
                    lock (_v3SlotGate) active = _connection;
                    if (active is null || !SameFingerprint(active.PeerFingerprint, fingerprint)) return false;
                    Publish("收到已连接设备的重连请求，等待本机同意…"); Changed();
                    accepted = await ConfirmIncomingConnectionAsync(name, peerFingerprint, candidateCancellation.Token);
                    if (!accepted) return false;
                    candidateCancellation.Token.ThrowIfCancellationRequested();

                    var reservation = await TryReserveV3ReplacementSlotAsync(peerFingerprint, candidateCancellation.Token);
                    if (!reservation.Reserved) return false;
                    reserved = true;
                    reservedFingerprint = peerFingerprint;
                    replacementPrevious = reservation.Active;
                    if (replacementPrevious is { } previous)
                    {
                        try { reservation.ActiveCancellation?.Cancel(); } catch (ObjectDisposedException) { }
                        previous.Close();
                        await WaitForV3RunnerAsync(previous, candidateCancellation.Token);
                    }
                    candidateCancellation.Token.ThrowIfCancellationRequested();
                    if (!TryTransitionV3Slot(V3SlotReplacement, V3SlotActive, peerFingerprint, cancellation))
                        throw new InvalidOperationException("无法完成 v3 重连的连接槽交接。");
                    return true;
                }, cancellation.Token);
                try { cancellation.Token.ThrowIfCancellationRequested(); await AttachAsync(connection, cancellation); attached = true; }
                catch { connection.Close(); throw; }
            }
            catch (OperationCanceledException) when (networkToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (pendingAcceptCts?.IsCancellationRequested == true) { }
            catch (Exception ex)
            {
                AppLog.Write($"accept: {ex.GetType().Name}");
                if (reserved) Publish("连接未建立：" + ErrorText.Describe(ex));
            }
            finally
            {
                acceptingWork?.Dispose();
                try
                {
                    if (!attached && reserved && replacementPrevious is not null)
                    {
                        replacementPrevious.Close();
                        await WaitForV3RunnerAsync(replacementPrevious, CancellationToken.None);
                    }
                }
                finally
                {
                    if (!attached)
                    {
                        if (reserved)
                        {
                            bool released = TryTransitionV3Slot(V3SlotReplacement, V3SlotFree, null,
                                expectedFingerprint: reservedFingerprint);
                            if (!released) released = TryTransitionV3Slot(V3SlotActive, V3SlotFree, null,
                                expectedConnectionCts: cancellation);
                            if (released) Changed();
                        }
                        cancellation.Dispose();
                    }
                    if (pendingAcceptCts is not null && pendingAcceptCompletion is not null)
                    {
                        pendingAcceptRegistration.Dispose();
                        EndV3PendingAccept(pendingAcceptCts, pendingAcceptCompletion);
                    }
                }
            }
        }
    }

    private Task V4AcceptLoopAsync(CancellationToken networkToken)
        => Task.WhenAll(Enumerable.Range(0, PeerConnectionRegistry<V4Connection>.MaximumDirectPeers)
            .Select(_ => V4AcceptWorkerAsync(networkToken)));

    private async Task V4AcceptWorkerAsync(CancellationToken networkToken)
    {
        while (!networkToken.IsCancellationRequested)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(networkToken);
            bool attached = false;
            IDisposable? acceptingWork = null;
            V4Connection? candidate = null;
            try
            {
                candidate = await V4Listener!.AcceptAsync(() => Device!, async (name, peerFingerprint, ct) =>
                {
                    if (!Settings.Setup.Ended || !IsRunning || _v4Connections is null ||
                        Volatile.Read(ref _networkPathRefreshPending) != 0) return false;
                    acceptingWork = _updateWork.TryEnter();
                    if (acceptingWork is null) return false;
                    Publish("收到 v4 连接请求，等待本机同意…"); Changed();
                    bool accepted = await ConfirmIncomingConnectionAsync(name, peerFingerprint, ct);
                    return accepted && Volatile.Read(ref _networkPathRefreshPending) == 0;
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _networkPathRefreshPending) != 0)
                {
                    candidate.Close();
                    continue;
                }
                AppLog.Write($"v4 accept: mounted peer={candidate.PeerFingerprint[..Math.Min(8, candidate.PeerFingerprint.Length)]}");
                attached = await AttachV4Async(candidate, cancellation);
                if (attached) Post(() => CacheAvatarForAuthenticatedPeer(candidate.PeerFingerprint));
                AppLog.Write($"v4 accept: attached={attached}");
                if (!attached) candidate.Close();
            }
            catch (OperationCanceledException) when (networkToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppLog.Write($"v4 accept: {ex.GetType().Name}: {ex.Message}");
                candidate?.Close();
                Publish("v4 连接未建立：" + ErrorText.Describe(ex));
            }
            finally
            {
                acceptingWork?.Dispose();
                if (!attached) cancellation.Dispose();
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
        cancellation.Token.ThrowIfCancellationRequested();
        var runnerStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Task.Run(async () =>
        {
            if (await runnerStart.Task.ConfigureAwait(false))
                await RunConnectionAsync(connection, fingerprint, cancellation).ConfigureAwait(false);
        });
        try
        {
            lock (_v3RunnerGate)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _runner = runner;
                _runnerConnection = connection;
            }
            _ = runner.ContinueWith(_ =>
            {
                lock (_v3RunnerGate)
                {
                    if (ReferenceEquals(_runnerConnection, connection))
                    {
                        _runnerConnection = null;
                        _runner = null;
                    }
                }
                lock (_v3SlotGate)
                {
                    if (ReferenceEquals(_connection, connection)) _connection = null;
                    if (Volatile.Read(ref _connectionSlot) == V3SlotActive && ReferenceEquals(_connectionCts, cancellation) &&
                        _connection is null)
                        TryTransitionV3Slot(V3SlotActive, V3SlotFree, null, expectedConnectionCts: cancellation);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Run(() => _messages!.SetHidden(fingerprint, false));
            cancellation.Token.ThrowIfCancellationRequested();
            bool committed = false;
            await OnUiAsync(() =>
            {
                lock (_v3SlotGate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _connectionSlot) != V3SlotActive || !ReferenceEquals(_connectionCts, cancellation))
                        throw new InvalidOperationException("v3 连接槽已被取消或替换。");
                    if (_connectionSlotFingerprint is not null && !SameFingerprint(_connectionSlotFingerprint, fingerprint))
                        throw new InvalidDataException("连接设备身份与发现信息不一致。");
                    Volatile.Write(ref _connectionSlotFingerprint, fingerprint);
                    var conversation = FindConversation(fingerprint, connection.PeerName);
                    conversation.Name = connection.PeerName; conversation.State = "已连接";
                    _connection = connection; ActiveFingerprint = fingerprint;
                    Publish("已连接 · " + connection.PeerName); Changed();
                    committed = true;
                    KickOutbox(fingerprint);
                }
            });
            runnerStart.TrySetResult(committed);
        }
        catch
        {
            runnerStart.TrySetResult(false);
            throw;
        }
    }

    private async Task<bool> AttachV4Async(V4Connection connection, CancellationTokenSource cancellation, bool? directIntent = null)
    {
        string fingerprint = ConversationHistory.NormalizeFingerprint(connection.PeerFingerprint);
        bool isGroupMember = Groups?.Groups.Any(group => group.Members.Any(member => SameFingerprint(member.Fingerprint, fingerprint))) == true;
        bool hasPrivateHistory = _messages?.Messages.Any(message => message.Fingerprint == fingerprint) == true;
        bool direct = directIntent ?? (!isGroupMember || hasPrivateHistory);
        lock (_v4Gate)
        {
            // Preserve an existing direct relationship even when no private messages exist yet.
            if (_v4Connections?.IsDirect(fingerprint) == true) direct = true;
        }
        if (!direct && !isGroupMember) throw new InvalidDataException("群组专用连接的对端不是本机已确认的群组成员。");
        V4PeerRuntime runtime;
        connection.TextReceived = async message =>
        {
            using var incomingWork = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后接收消息。");
            await SaveAsync(new(message.Id, fingerprint, connection.PeerName, TransferDirection.Received,
                MessageKind.Text, MessageState.Received, DateTimeOffset.Now, Text: message.Text));
        };
        connection.FileReceiving = async file =>
        {
            IDisposable? incomingWork = _updateWork.TryEnter();
            if (incomingWork is null) return false;
            var message = new ConversationMessage(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                file.Kind == "image" && file.Folder is null ? MessageKind.Image : MessageKind.File, MessageState.Sending, DateTimeOffset.Now,
                FileName: file.DisplayName, Size: file.DisplaySize, Error: "等待确认接收");
            try
            {
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
                    if (!conversation.Messages.Any(item => item.Id == file.TransferID && item.Entry.Direction == TransferDirection.Received))
                        conversation.Messages.Add(new MessageItem(message));
                    Changed();
                });
                if (cancellation.IsCancellationRequested || !_v4Connections!.TryGet(fingerprint, out var active) || !ReferenceEquals(active, connection)) decision.TrySetCanceled(cancellation.Token);
                using var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                decisionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                bool accepted;
                try { accepted = await decision.Task.WaitAsync(decisionTimeout.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    await SaveAsync(message with { State = MessageState.Failed, Error = "等待接收确认超时。" });
                    throw new IOException("等待接收确认超时。");
                }
                var current = _messages!.Messages.FirstOrDefault(entry => entry.Id == file.TransferID && entry.Fingerprint == fingerprint && entry.Direction == TransferDirection.Received) ?? message;
                if (!accepted) await SaveAsync(current with { State = MessageState.Declined, Error = "已拒绝接收" });
                else
                {
                    if (current.Error != "接收已确认") await SaveAsync(current with { Error = "接收已确认" });
                    lock (_incomingDecisionGate) _v4IncomingWork[(fingerprint, file.TransferID)] = incomingWork;
                    incomingWork = null;
                }
                return accepted;
            }
            catch (OperationCanceledException)
            {
                var current = _messages!.Messages.FirstOrDefault(entry => entry.Id == file.TransferID && entry.Fingerprint == fingerprint && entry.Direction == TransferDirection.Received) ?? message;
                await SaveAsync(current with { State = MessageState.Failed, Error = "连接已断开。" });
                throw;
            }
            catch (Exception ex)
            {
                var current = _messages!.Messages.FirstOrDefault(entry => entry.Id == file.TransferID && entry.Fingerprint == fingerprint && entry.Direction == TransferDirection.Received) ?? message;
                await SaveAsync(current with { State = MessageState.Failed, Error = ErrorText.Describe(ex) });
                throw;
            }
            finally
            {
                lock (_incomingDecisionGate) _incomingDecisions.Remove((fingerprint, file.TransferID));
                incomingWork?.Dispose();
            }
        };
        long lastReceiveUpdate = 0, receiveSampleBytes = 0, receiveSampleAt = Environment.TickCount64;
        double receiveSpeed = 0;
        connection.ReceiveProgress = (file, bytes) =>
        {
            long now = Environment.TickCount64;
            long elapsed = now - receiveSampleAt;
            if (elapsed >= 500)
            {
                receiveSpeed = Math.Max(0, bytes - receiveSampleBytes) * 1000.0 / elapsed;
                receiveSampleBytes = bytes; receiveSampleAt = now;
            }
            if (bytes != 0 && bytes != file.DisplaySize && now - lastReceiveUpdate < 150) return;
            lastReceiveUpdate = now;
            double sampledSpeed = receiveSpeed;
            Post(() =>
            {
                var conversation = FindConversation(fingerprint, connection.PeerName);
                var item = conversation.Messages.FirstOrDefault(entry => entry.Id == file.TransferID && entry.Entry.Direction == TransferDirection.Received);
                if (item is null)
                {
                    item = new MessageItem(new(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                        file.Kind == "image" && file.Folder is null ? MessageKind.Image : MessageKind.File, MessageState.Sending, DateTimeOffset.Now,
                        FileName: file.DisplayName, Size: file.DisplaySize));
                    conversation.Messages.Add(item);
                }
                else if (item.Entry.Error == "接收已确认") item.Apply(item.Entry with { Error = "" });
                item.SetProgress(bytes, sampledSpeed);
            });
        };
        connection.FileReceived = async (file, path) =>
        {
            // A folder arrives once, as its created root directory.
            string receivedName = file.Folder is null ? file.FileName : Path.GetFileName(path);
            try
            {
                await SaveAsync(new(file.TransferID, fingerprint, connection.PeerName, TransferDirection.Received,
                    file.Kind == "image" && file.Folder is null ? MessageKind.Image : MessageKind.File, MessageState.Received, DateTimeOffset.Now,
                    FileName: receivedName, Path: path, Size: file.DisplaySize));
                await RecordFileAsync(TransferDirection.Received, new(receivedName, path, file.DisplaySize, connection.PeerName, DateTimeOffset.Now));
            }
            finally
            {
                lock (_incomingDecisionGate)
                    if (_v4IncomingWork.Remove((fingerprint, file.TransferID), out var incomingWork)) incomingWork.Dispose();
            }
        };
        if (direct) await Task.Run(() => _messages!.SetHidden(fingerprint, false));
        if (directIntent is null && Volatile.Read(ref _networkPathRefreshPending) != 0)
        {
            connection.Close();
            return false;
        }
        V4PeerRuntime? supersededRuntime = null;
        lock (_v4Gate)
        {
            var registry = _v4Connections ?? throw new InvalidOperationException("v4 连接注册表尚未准备好。");
            if (registry.IsDirect(fingerprint)) direct = true;
            bool replaceExisting = direct && Volatile.Read(ref _v4Connecting) == 0 &&
                !_v4ReconnectReplacementPrimed.Contains(fingerprint) && registry.TryGet(fingerprint, out _);
            var result = registry.Register(connection, fingerprint, connection.InitiatorFingerprint,
                connection.TranscriptDigest, direct, replaceExisting);
            if (replaceExisting && result.Disposition == ConnectionRegistrationDisposition.Replaced)
                _v4ReconnectReplacementPrimed.Add(fingerprint);
            if (result.Disposition == ConnectionRegistrationDisposition.ExistingKept)
            {
                connection.Close();
                return false;
            }
            if (result.Superseded is not null && _v4Runtimes.Remove(fingerprint, out var previous)) supersededRuntime = previous;
            runtime = new V4PeerRuntime(connection, cancellation)
            {
                GroupRouter = (Groups ?? throw new InvalidOperationException("群路由服务尚未准备好。")).CreateRouter()
            };
            _v4Runtimes[fingerprint] = runtime;
        }
        if (supersededRuntime is not null)
        {
            try { supersededRuntime.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            supersededRuntime.Connection.Close();
        }
        try
        {
            await OnUiAsync(() =>
            {
                lock (_v4Gate)
                {
                    if (_v4Connections is null || !_v4Connections.TryGet(fingerprint, out var active) ||
                        !ReferenceEquals(active, connection)) return;
                    if (direct)
                    {
                        _v4Connection = connection; ActiveFingerprint = fingerprint;
                        var conversation = FindConversation(fingerprint, connection.PeerName);
                        conversation.Name = connection.PeerName; conversation.State = "已连接";
                        Publish("已连接 · " + connection.PeerName);
                        KickOutbox(fingerprint);
                    }
                    else
                    {
                        var conversation = Conversations.FirstOrDefault(item => item.Fingerprint == fingerprint);
                        if (conversation is not null) conversation.State = "群组已连接";
                        Publish("群组成员连接已就绪 · " + connection.PeerName);
                    }
                    Changed();
                }
            });
            runtime.Runner = Task.Run(() => RunConnectionAsync(connection, fingerprint, cancellation));
            return true;
        }
        catch
        {
            lock (_v4Gate)
            {
                bool removed = _v4Connections?.Remove(fingerprint, connection) == true;
                if (removed && (_v4Connections?.TryGet(fingerprint, out _) != true))
                    _v4ReconnectReplacementPrimed.Remove(fingerprint);
                if (_v4Runtimes.TryGetValue(fingerprint, out var active) && ReferenceEquals(active, runtime)) _v4Runtimes.Remove(fingerprint);
            }
            connection.Close();
            throw;
        }
    }

    private async Task RunConnectionAsync(V3Connection connection, string fingerprint, CancellationTokenSource cancellation)
    {
        bool failed = false;
        try { await connection.RunAsync(() => Settings.ReceiveFolder, cancellation.Token, _inbox); }
        catch (Exception ex)
        {
            AppLog.Write($"v3 read loop failed type={ex.GetType().Name}");
            if (!cancellation.IsCancellationRequested) { failed = true; Publish("连接已断开"); }
        }
        finally
        {
            lock (_incomingDecisionGate)
                foreach (var pending in _incomingDecisions.Where(kv => kv.Key.Fingerprint == fingerprint).Select(kv => kv.Value))
                    pending.TrySetCanceled();
            connection.Close();
            try { await FinishConnectionAsync(fingerprint, failed, connection); }
            catch (Exception ex) { AppLog.Write($"v3 finish failed: {ex.GetType().Name}"); }
            finally { cancellation.Dispose(); Post(() => OnTrustedPeerClosed(fingerprint)); }
        }
    }

    private async Task RunConnectionAsync(V4Connection connection, string fingerprint, CancellationTokenSource cancellation)
    {
        bool failed = false;
        try { await (GetV4Router(connection)?.RunAsync(connection, () => Settings.ReceiveFolder, cancellation.Token)
            ?? RunV4DirectLoopAsync(connection, cancellation.Token)); }
        catch (Exception ex)
        {
            AppLog.Write($"v4 read loop failed type={ex.GetType().Name}");
            if (!cancellation.IsCancellationRequested) { failed = true; Publish("连接已断开"); }
        }
        finally
        {
            lock (_incomingDecisionGate)
            {
                foreach (var pending in _incomingDecisions.Where(kv => kv.Key.Fingerprint == fingerprint).Select(kv => kv.Value))
                    pending.TrySetCanceled();
                foreach (var key in _v4IncomingWork.Keys.Where(key => key.Fingerprint == fingerprint).ToArray())
                    if (_v4IncomingWork.Remove(key, out var incomingWork)) incomingWork.Dispose();
            }
            connection.Close();
            lock (_v4Gate)
            {
                bool removed = _v4Connections?.Remove(fingerprint, connection) == true;
                if (removed && (_v4Connections?.TryGet(fingerprint, out _) != true))
                    _v4ReconnectReplacementPrimed.Remove(fingerprint);
                if (_v4Runtimes.TryGetValue(fingerprint, out var current) && ReferenceEquals(current.Connection, connection))
                    _v4Runtimes.Remove(fingerprint);
            }
            await FinishConnectionAsync(fingerprint, failed, connection);
            cancellation.Dispose();
            Post(() => OnTrustedPeerClosed(fingerprint));
        }
    }

    private V4GroupConnectionRouter? GetV4Router(V4Connection connection)
    {
        lock (_v4Gate)
            return _v4Runtimes.TryGetValue(ConversationHistory.NormalizeFingerprint(connection.PeerFingerprint), out var runtime) && ReferenceEquals(runtime.Connection, connection)
                ? runtime.GroupRouter : null;
    }

    private V4Connection? FirstDirectV4Connection()
    {
        var registry = _v4Connections;
        if (registry is null) return null;
        foreach (var pair in registry.Snapshot())
            if (registry.IsDirect(pair.Key)) return pair.Value;
        return null;
    }

    private string? FirstDirectV4Fingerprint()
    {
        var registry = _v4Connections;
        if (registry is null) return null;
        foreach (var pair in registry.Snapshot())
            if (registry.IsDirect(pair.Key)) return pair.Key;
        return null;
    }

    // Group router owns the same connection read loop and preserves direct callbacks installed above.
    private Task RunV4DirectLoopAsync(V4Connection connection, CancellationToken cancellationToken)
        => connection.RunAsync(() => Settings.ReceiveFolder, cancellationToken);

    private async Task FinishConnectionAsync(string fingerprint, bool failed, object connection)
    {
        bool peerStillConnected = (_connection is not null && !ReferenceEquals(_connection, connection) && SameFingerprint(_connection.PeerFingerprint, fingerprint)) ||
            (_v4Connections?.TryGet(fingerprint, out var activeV4) == true && !ReferenceEquals(activeV4, connection));
        if (!peerStillConnected)
        {
            try
            {
                var interrupted = await Task.Run(() => _messages!.FailActive(fingerprint, "连接已断开。"));
                await OnUiAsync(() => { foreach (var message in interrupted) ApplyMessage(message); });
            }
            catch (Exception ex) { AppLog.Write($"history: fail active {ex.GetType().Name}"); }
        }
        await OnUiAsync(() =>
        {
            var conversation = Conversations.FirstOrDefault(c => c.Fingerprint == fingerprint);
            if (conversation is not null && !peerStillConnected)
            {
                conversation.State = "未连接";
                foreach (var item in conversation.Messages.Where(m => m.Entry.IsActive)) item.Apply(item.Entry.Interrupted("连接已断开。"));
            }
            bool removedV3 = false;
            lock (_v3SlotGate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null; ActiveFingerprint = FirstDirectV4Fingerprint();
                    removedV3 = true;
                }
            }
            if (removedV3)
            {
                if (!failed) Publish("已断开连接");
                Changed();
            }
            else if (connection is V4Connection v4)
            {
                lock (_v4Gate)
                {
                    if (ReferenceEquals(_v4Connection, v4)) _v4Connection = FirstDirectV4Connection();
                    if (ActiveFingerprint == fingerprint) ActiveFingerprint = _connection?.PeerFingerprint ?? FirstDirectV4Fingerprint();
                }
                if (!failed) Publish("已断开连接");
                Changed();
            }
        });
    }

    public async Task DisconnectAsync(ConversationItem? conversation = null)
    {
        string? fingerprint = conversation is null ? null : ConversationHistory.NormalizeFingerprint(conversation.Fingerprint);
        // Only the conversation-level disconnect is user-initiated; it stops automatic redialing for this run.
        if (fingerprint is not null && IsTrustedPeer(fingerprint)) lock (_trustedGate) _trustedDial.Suppress(fingerprint);
        Task? pendingV3Accept = CancelV3PendingAccept(fingerprint);
        var (targetsActiveV3Connection, v3SlotCompletion, v3Cancellation, v3ActiveConnection) = GetV3SlotDrain(fingerprint);
        var runners = new List<Task>();
        if (targetsActiveV3Connection)
        {
            try { v3Cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            v3ActiveConnection?.Close();
        }
        if (fingerprint is null)
        {
            foreach (var pending in _v4Pending.Values)
                try { pending.Cancel(); } catch (ObjectDisposedException) { }
        }
        else if (_v4PendingByPeer.TryGetValue(fingerprint, out var pending))
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { }
        }

        V4PeerRuntime[] runtimes;
        lock (_v4Gate)
            runtimes = _v4Runtimes.Values.Where(runtime => fingerprint is null || SameFingerprint(runtime.Connection.PeerFingerprint, fingerprint)).ToArray();
        foreach (var runtime in runtimes)
        {
            try { runtime.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            runtime.Connection.Close();
            if (runtime.Runner is not null) runners.Add(runtime.Runner);
        }
        if (runners.Count > 0) await Task.WhenAll(runners);
        if (pendingV3Accept is not null) await pendingV3Accept;
        if (v3SlotCompletion is not null) await v3SlotCompletion;
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

    private (V3Connection? V3, V4Connection? V4)? TryConnection(ConversationItem conversation)
    {
        string fingerprint = ConversationHistory.NormalizeFingerprint(conversation.Fingerprint);
        if (_connection is not null && SameFingerprint(_connection.PeerFingerprint, fingerprint)) return (_connection, null);
        if (_v4Connections?.TryGet(fingerprint, out var v4) == true && v4 is not null) return (null, v4);
        return null;
    }

    private bool IsCurrent((V3Connection? V3, V4Connection? V4) connection)
        => connection.V3 is not null ? ReferenceEquals(_connection, connection.V3) :
            connection.V4 is not null && _v4Connections?.TryGet(connection.V4.PeerFingerprint, out var active) == true && ReferenceEquals(active, connection.V4);

    public async Task SendTextAsync(ConversationItem conversation, string text, MessageItem? retry = null)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后发送。");
        if (IsInstallingUpdate || conversation is null || _messages is null) throw new InvalidOperationException("请先连接当前设备；查看历史不会切换活动连接。");
        if (retry is not null && (retry.Entry.Fingerprint != conversation.Fingerprint || retry.Entry.Direction != TransferDirection.Sent || retry.Entry.Kind != MessageKind.Text))
            throw new InvalidOperationException("这条消息属于其他会话。");
        var id = retry?.Id ?? Guid.NewGuid(); new V3Text(id, text).Validate();
        // Earlier pending messages go first, so a new message joins the outbox behind them.
        if (TryConnection(conversation) is not { } connection || _messages.OutboxCount(conversation.Fingerprint) > 0)
        {
            await EnqueueOutboxAsync([new ConversationMessage(id, conversation.Fingerprint, OutboxPeerName(conversation), TransferDirection.Sent,
                MessageKind.Text, MessageState.Queued, retry?.Entry.At ?? DateTimeOffset.Now, Text: text, OutboxSince: DateTimeOffset.Now)]);
            return;
        }
        string peerName = connection.V3?.PeerName ?? connection.V4!.PeerName;
        try
        {
            await DeliverTextAsync(connection, new ConversationMessage(id, conversation.Fingerprint, peerName, TransferDirection.Sent,
                MessageKind.Text, MessageState.Sending, retry?.Entry.At ?? DateTimeOffset.Now, Text: text, OutboxSince: retry?.Entry.OutboxSince));
        }
        finally { KickOutbox(conversation.Fingerprint); }
    }

    private async Task DeliverTextAsync((V3Connection? V3, V4Connection? V4) connection, ConversationMessage message)
    {
        string sendFingerprint = ConversationHistory.NormalizeFingerprint(message.Fingerprint);
        if (!_sendingTextPeers.TryAdd(sendFingerprint, 0)) throw new InvalidOperationException("上一条文字仍在等待回执。");
        Interlocked.Increment(ref _sendingText);
        Changed();
        message = message with { State = MessageState.Sending, Error = "" };
        try
        {
            await SaveAsync(message);
            if (connection.V3 is not null) await connection.V3.SendTextAsync(message.Text, message.Id, _lifetime.Token);
            else await connection.V4!.SendTextAsync(message.Text, message.Id, _lifetime.Token);
            await SaveAsync(message with { State = MessageState.Delivered });
        }
        catch (Exception ex) { await MarkFailedAsync(message, ex, connection); throw; }
        finally { _sendingTextPeers.TryRemove(sendFingerprint, out _); Interlocked.Decrement(ref _sendingText); Changed(); }
    }

    public async Task SendFilesAsync(ConversationItem conversation, IReadOnlyList<string> paths, bool images = false)
    {
        using var work = _updateWork.TryEnter() ?? throw new InvalidOperationException("正在准备安装更新，请稍后发送。");
        if (IsInstallingUpdate || conversation is null || _messages is null) throw new InvalidOperationException("请先连接当前设备；查看历史不会切换活动连接。");
        var connection = TryConnection(conversation);
        // Earlier pending messages go first, so new attachments join the outbox behind them.
        bool offline = connection is null || _messages.OutboxCount(conversation.Fingerprint) > 0;
        string peerName = connection is { } active ? active.V3?.PeerName ?? active.V4!.PeerName : OutboxPeerName(conversation);
        bool[] folders = paths.Select(Directory.Exists).ToArray();
        if (!offline && folders.Contains(true) && connection!.Value.V4 is not { SupportsResume: true })
            throw new InvalidOperationException("对方版本不支持发送文件夹，请让对方升级后再试，或打包后发送。");
        DateTimeOffset? outboxSince = offline ? DateTimeOffset.Now : null;
        var messages = new List<ConversationMessage>();
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index];
            // A folder is one message; listing it first also rejects empty or oversized folders before anything is queued.
            messages.Add(folders[index]
                ? new ConversationMessage(Guid.NewGuid(), conversation.Fingerprint, peerName, TransferDirection.Sent,
                    MessageKind.File, MessageState.Queued, DateTimeOffset.Now,
                    FileName: Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), Path: path,
                    Size: await Task.Run(() => FolderEnumerator.Entries(path).Sum(entry => entry.Size)), OutboxSince: outboxSince)
                : new ConversationMessage(Guid.NewGuid(), conversation.Fingerprint, peerName, TransferDirection.Sent,
                    images || IsImage(path) ? MessageKind.Image : MessageKind.File, MessageState.Queued, DateTimeOffset.Now,
                    FileName: Path.GetFileName(path), Path: path, Size: new FileInfo(path).Length, OutboxSince: outboxSince));
        }
        if (offline)
        {
            await EnqueueOutboxAsync(messages);
            return;
        }
        try { await DeliverFilesAsync(conversation, connection!.Value, messages, saveFirst: true); }
        finally { KickOutbox(conversation.Fingerprint); }
    }

    /// <summary>Sends attachments one at a time; <paramref name="saveFirst"/> is false for outbox messages already stored.</summary>
    private async Task DeliverFilesAsync(ConversationItem conversation, (V3Connection? V3, V4Connection? V4) connection,
        IReadOnlyList<ConversationMessage> messages, bool saveFirst)
    {
        string peerName = connection.V3?.PeerName ?? connection.V4!.PeerName;
        string sendFingerprint = ConversationHistory.NormalizeFingerprint(conversation.Fingerprint);
        if (!_sendingFilePeers.TryAdd(sendFingerprint, 0)) throw new InvalidOperationException("请等当前这批附件发送完成。");
        Interlocked.Increment(ref _sendingFiles);
        Changed();
        var queued = new List<ConversationMessage>();
        try
        {
            foreach (var message in messages)
            {
                if (saveFirst) await SaveAsync(message);
                queued.Add(message);
            }
            foreach (var message in queued)
            {
                try
                {
                    if (!IsCurrent(connection)) throw new IOException("连接已断开，请重新连接后重试。");
                    await SaveAsync(message with { State = MessageState.Sending, Error = "等待对方确认接收" });
                    long lastUpdate = 0, sampleBytes = 0;
                    long sampleAt = Environment.TickCount64;
                    double sendSpeed = 0;
                    Action<long, long> reportProgress = (bytes, total) =>
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
                    };
                    long sentSize;
                    if (Directory.Exists(message.Path))
                        sentSize = (await connection.V4!.SendFolderAsync(message.Path, message.Id, reportProgress, _lifetime.Token)).TotalBytes;
                    else
                    {
                        Task<V3File> sendTask = connection.V3 is not null
                            ? connection.V3.SendFileAsync(message.Path, message.Kind == MessageKind.Image ? "image" : "file", message.Id, reportProgress, _lifetime.Token)
                            : connection.V4!.SendFileAsync(message.Path, message.Kind == MessageKind.Image ? "image" : "file", message.Id, reportProgress, _lifetime.Token);
                        sentSize = (await sendTask).Size;
                    }
                    var currentMessage = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent) ?? message;
                    await SaveAsync(currentMessage with { State = MessageState.Delivered, Error = "", Size = sentSize });
                    await RecordFileAsync(TransferDirection.Sent, new(message.FileName, message.Path, sentSize, peerName, DateTimeOffset.Now));
                }
                catch (Exception ex)
                {
                    var current = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent) ?? message;
                    // A declined file is final; it is never retried from the outbox.
                    if (ex is IOException { Message: "已拒绝接收文件。" })
                        await SaveAsync(current with { State = MessageState.Declined, Error = "" });
                    else if (current.State != MessageState.Declined) await MarkFailedAsync(current, ex, connection);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var message in queued)
            {
                var current = _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == TransferDirection.Sent);
                if (current?.IsActive == true) await MarkFailedAsync(current, ex, connection);
            }
            throw;
        }
        finally { _sendingFilePeers.TryRemove(sendFingerprint, out _); Interlocked.Decrement(ref _sendingFiles); Changed(); }
    }

    // Offline outbox: direct messages written while the peer is not connected, delivered in order once it connects.
    private string OutboxPeerName(ConversationItem conversation)
        => string.IsNullOrWhiteSpace(conversation.Name) ? TrustedPeerName(ConversationHistory.NormalizeFingerprint(conversation.Fingerprint)) : conversation.Name;

    private async Task EnqueueOutboxAsync(IReadOnlyList<ConversationMessage> messages)
    {
        if (AvatarIds.NormalizeFingerprint(messages[0].Fingerprint) is null) throw new InvalidOperationException("请先连接当前设备。");
        await Task.Run(() => _messages!.Enqueue(messages));
        await OnUiAsync(() => { foreach (var message in messages) ApplyMessage(message); });
        KickOutbox(messages[0].Fingerprint);
    }

    public async Task CancelOutboxAsync(ConversationItem conversation, MessageItem item)
    {
        if (_messages is null || item.Entry.Fingerprint != conversation.Fingerprint) return;
        if (!await Task.Run(() => _messages.CancelOutbox(conversation.Fingerprint, item.Id)))
            throw new InvalidOperationException("这条消息已开始发送，无法取消。");
        await OnUiAsync(() => { conversation.Messages.Remove(item); Changed(); });
    }

    private void KickOutbox(string fingerprint) => Post(() => _ = FlushOutboxAsync(ConversationHistory.NormalizeFingerprint(fingerprint)));

    private async Task ExpireOutboxAsync()
    {
        var expired = await Task.Run(() => _messages!.ExpireOutbox(DateTimeOffset.Now));
        if (expired.Count > 0) await OnUiAsync(() => { foreach (var message in expired) ApplyMessage(message); });
    }

    /// <summary>Checks at startup and then hourly, so a pending message expires even without connection events.</summary>
    private async Task RunOutboxExpiryAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do await ExpireOutboxAsync(); while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("outbox expiry: " + ex.GetType().Name); }
    }

    /// <summary>Delivers pending messages one at a time, oldest first. Only persistent contacts are dialed for it;
    /// other peers receive pending messages once either side connects.</summary>
    private async Task FlushOutboxAsync(string fingerprint)
    {
        if (_messages is null || !_flushingOutbox.TryAdd(fingerprint, 0)) return;
        try
        {
            await ExpireOutboxAsync();
            while (!_lifetime.IsCancellationRequested && _messages.NextOutbox(fingerprint) is { } next)
            {
                ConversationItem? conversation = null;
                await OnUiAsync(() => conversation = FindConversation(fingerprint, next.PeerName));
                if (TryConnection(conversation!) is not { } connection) { Post(ConnectTrustedPeers); return; }
                if (_sendingTextPeers.ContainsKey(fingerprint) || _sendingFilePeers.ContainsKey(fingerprint)) return;
                using var work = _updateWork.TryEnter();
                if (work is null || IsInstallingUpdate) return;
                try
                {
                    if (next.Kind == MessageKind.Text) await DeliverTextAsync(connection, next);
                    else if (!File.Exists(next.Path) && !Directory.Exists(next.Path))
                        await SaveAsync(next with { State = MessageState.Failed, Error = "源文件已不存在，无法发送。" });
                    else if (Directory.Exists(next.Path) && connection.V4 is not { SupportsResume: true })
                        await SaveAsync(next with { State = MessageState.Failed, Error = "对方版本不支持接收文件夹。" });
                    else await DeliverFilesAsync(conversation!, connection, [next], saveFirst: false);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"outbox delivery stopped peer={fingerprint[..8]} type={ex.GetType().Name}");
                    // A lost connection leaves the message pending for the next one; a final failure moves on.
                    if (!IsCurrent(connection) || _messages.NextOutbox(fingerprint)?.Id == next.Id) return;
                }
            }
        }
        finally { _flushingOutbox.TryRemove(fingerprint, out _); }
    }

    public static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private async Task MarkFailedAsync(ConversationMessage message, Exception error, (V3Connection? V3, V4Connection? V4)? connection = null)
    {
        // An outbox message interrupted by a lost connection waits for the next one; any other failure is final.
        var failed = connection is { } sent && !IsCurrent(sent)
            ? message.Interrupted(ErrorText.Describe(error))
            : message with { State = MessageState.Failed, Error = ErrorText.Describe(error) };
        try { await SaveAsync(failed); }
        catch { await OnUiAsync(() => ApplyMessage(failed)); }
    }
    private async Task SaveAsync(ConversationMessage message)
    {
        bool arrived = message.Direction == TransferDirection.Received && message.State == MessageState.Received &&
            _messages!.Messages.FirstOrDefault(m => m.Id == message.Id && m.Fingerprint == message.Fingerprint && m.Direction == message.Direction)?.State != MessageState.Received;
        await Task.Run(() => _messages!.Upsert(message));
        await OnUiAsync(() =>
        {
            ApplyMessage(message);
            if (arrived)
                NoteIncoming(message.Fingerprint, message.PeerName, message.Kind == MessageKind.Text ? message.Text : message.FileName,
                    notify: message.Kind == MessageKind.Text);
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
        conversation.SetUnread(UnreadCount(conversation.Fingerprint));
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

    private void OnGroupEventReceived(object? sender, GroupEvent groupEvent)
    {
        if (groupEvent.Kind is "text" or "file" && groupEvent.Message is { } groupMessage)
        {
            var group = Groups?.Store.GetGroup(groupEvent.GroupID);
            string senderName = group?.Members.FirstOrDefault(member => SameFingerprint(member.Fingerprint, groupEvent.PeerFingerprint))?.DisplayName
                ?? groupEvent.PeerFingerprint;
            NoteIncoming(UnreadCounter.GroupKey(groupEvent.GroupID), $"{group?.Name ?? "群聊"} · {senderName}",
                groupMessage.Text ?? groupMessage.FileMetadata?.FileName ?? "新消息", notify: groupEvent.Kind == "text");
        }
        if (groupEvent.Kind != "file" || groupEvent.FilePath is null ||
            groupEvent.Message?.FileMetadata is not { } file) return;

        string from = Groups?.Store.GetGroup(groupEvent.GroupID)?.Members
            .FirstOrDefault(member => SameFingerprint(member.Fingerprint, groupEvent.PeerFingerprint))?.DisplayName
            ?? groupEvent.PeerFingerprint;
        _ = RecordGroupFileAsync(new ReceivedItem(file.FileName, groupEvent.FilePath,
            checked((long)file.Size), from, DateTimeOffset.Now));
    }

    private async Task RecordGroupFileAsync(ReceivedItem item)
    {
        try { await RecordFileAsync(TransferDirection.Received, item); }
        catch (Exception ex) { AppLog.Write("group file history: " + ex.GetType().Name); }
    }

    public bool IsConversationHidden(ConversationItem conversation) => _messages?.IsHidden(conversation.Fingerprint) == true;
    public bool CanDeleteConversation(ConversationItem conversation) => _messages is not null && !IsConnected(conversation) && !conversation.Messages.Any(m => m.Entry.IsActive);

    public async Task SetConversationHiddenAsync(ConversationItem conversation, bool hidden)
    {
        if (_messages is null) throw new InvalidOperationException("本机记录尚未准备好。");
        await Task.Run(() => _messages.SetHidden(conversation.Fingerprint, hidden));
        RefreshUnread(conversation.Fingerprint);
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
                if (retained.Length == 0 && !IsConnected(conversation))
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

    public bool CanMergeConversation(ConversationItem conversation) => CanDeleteConversation(conversation);
    public IReadOnlyList<ConversationItem> MergeTargets(ConversationItem conversation)
        => Conversations.Where(c => c.Fingerprint != conversation.Fingerprint && !IsConversationHidden(c)).ToArray();

    /// <summary>Folds <paramref name="source"/> into <paramref name="target"/> once the user confirms they are one device.</summary>
    public async Task MergeConversationAsync(ConversationItem source, ConversationItem target)
    {
        if (!CanMergeConversation(source)) throw new InvalidOperationException("请先断开此会话的连接，并等待传输结束后再合并。");
        string from = source.Fingerprint, into = target.Fingerprint;
        await Task.Run(() => _messages!.MergeConversation(from, into));
        await OnUiAsync(() =>
        {
            var rows = _messages!.Messages.Where(m => m.Fingerprint == into).ToArray();
            target.Messages.Clear();
            foreach (var entry in rows) target.Messages.Add(new MessageItem(entry));
            target.Summary = rows.LastOrDefault() is not { } last ? "暂无消息" : last.Kind == MessageKind.Text ? last.Text.ReplaceLineEndings(" ") : last.FileName;
            if (string.IsNullOrWhiteSpace(target.Draft)) target.Draft = source.Draft;
            Conversations.Remove(source); source.Messages.Clear(); source.Draft = "";
            if (IsTrustedPeer(from)) SetTrustedPeer(from, false);
            Unread.Clear(from);
            RefreshUnread(into);
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
    public async Task UpdateAvatarIdAsync(string? avatarId)
    {
        Settings.SaveAvatarId(avatarId);
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
            Identity?.Dispose(); Identity = null; Listener = null; V4Listener = null; Discovery = null;
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
    public Task InvokeOnUiAsync(Action action) => OnUiAsync(action);
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
