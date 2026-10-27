using System.Net;
using System.Net.Sockets;
using Feige.Core.Net;

namespace Feige.Core.Discovery;

/// <summary>
/// Browses <c>_feige._tcp.local</c> and maintains the list of peers that are currently reachable.
/// </summary>
/// <remarks>
/// <para>
/// The DNS-SD resolution is staged: a PTR answer yields instance names, an SRV answer yields the host and port, and an
/// A/AAAA answer yields a dialable address. A peer is only reported once all three are known, because a device list
/// entry without an address cannot be connected to.
/// </para>
/// <para>
/// Records expire on the TTL from the response; a goodbye (TTL 0) removes the peer immediately. Everything here is
/// unauthenticated — the fingerprint in TXT is a hint for the device list, and the value the user confirms must come
/// from the handshake.
/// </para>
/// </remarks>
public sealed class PeerBrowser : IAsyncDisposable
{
    private readonly IMdnsTransport _transport;
    private readonly Dictionary<DnsName, Entry> _entries = [];
    private readonly List<DiscoveredPeer> _peers = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _refreshLoop;
    private Task? _sweepLoop;
    private bool _disposed;

    /// <summary>Creates a browser for <paramref name="serviceType"/>; nothing is sent until <see cref="StartAsync"/>.</summary>
    public PeerBrowser(IMdnsTransport transport, DnsName? serviceType = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ServiceType = serviceType ?? FeigeService.Type;
        _transport.DatagramReceived += OnDatagramReceived;
    }

    /// <summary>Raised when a peer becomes dialable or its addresses/metadata change. Peers is the current snapshot.</summary>
    public event EventHandler<DiscoveredPeer>? PeerFound;

    /// <summary>Raised when a peer said goodbye or its records expired.</summary>
    public event EventHandler<DiscoveredPeer>? PeerLost;

    /// <summary>Service type being browsed.</summary>
    public DnsName ServiceType { get; }

    /// <summary>How often the browse query is re-sent. mDNS is lossy, so browse must be periodic.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Instance label of this device, so it never lists itself.</summary>
    public string? LocalInstanceLabel { get; set; }

    /// <summary>Snapshot of the currently reachable peers.</summary>
    public IReadOnlyList<DiscoveredPeer> Peers
    {
        get
        {
            lock (_gate)
            {
                return [.. _peers];
            }
        }
    }

    /// <summary>Sends the first browse query and starts the refresh and expiry loops.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transport
            .SendAsync(MdnsQueries.BrowseForService(ServiceType), cancellationToken)
            .ConfigureAwait(false);

        _refreshLoop ??= RefreshLoopAsync();
        _sweepLoop ??= SweepLoopAsync();
    }

    /// <summary>Sends an immediate browse query, e.g. when the user opens the device list.</summary>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        => _transport.SendAsync(MdnsQueries.BrowseForService(ServiceType), cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.DatagramReceived -= OnDatagramReceived;
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (Task? loop in new[] { _refreshLoop, _sweepLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _cts.Dispose();
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(RefreshInterval, _cts.Token).ConfigureAwait(false);
                await _transport.SendAsync(MdnsQueries.BrowseForService(ServiceType), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Shutdown or a closed socket; either way the loop is done.
        }
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);

                List<DiscoveredPeer> lost;
                lock (_gate)
                {
                    lost = SweepExpired(DateTimeOffset.UtcNow);
                }

                RaiseLost(lost);
                List<DiscoveredPeer> changed = [], withdrawn = [];
                lock (_gate) CollectCompleted(DateTimeOffset.UtcNow, changed, withdrawn);
                RaiseLost(withdrawn); RaiseFound(changed);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown.
        }
    }

    private void OnDatagramReceived(object? sender, MdnsDatagram datagram)
    {
        if (_disposed || !datagram.Message.IsResponse)
        {
            return;
        }

        List<MdnsMessage> queries = [];
        List<DiscoveredPeer> found;
        List<DiscoveredPeer> lost;

        lock (_gate)
        {
            found = [];
            lost = [];
            DateTimeOffset now = DateTimeOffset.UtcNow;

            var records = datagram.Message.Answers.Concat(datagram.Message.Additionals).ToArray();
            foreach (MdnsRecord record in records.Where(r => r is not AddressRecord))
            {
                switch (record)
                {
                    case PtrRecord ptr when ptr.Name == ServiceType:
                        HandlePointer(ptr, now, queries, lost);
                        break;

                    case SrvRecord srv:
                        HandleSrv(srv, now, queries, lost);
                        break;

                    case TxtRecord txt:
                        HandleTxt(txt, lost);
                        break;

                }
            }

            foreach (var address in records.OfType<AddressRecord>()) HandleAddress(address, now, datagram);
            CollectCompleted(now, found, lost);
        }

        RaiseLost(lost);
        RaiseFound(found);

        foreach (MdnsMessage query in queries)
        {
            _ = SendQueryAsync(query);
        }
    }

    private void HandlePointer(PtrRecord ptr, DateTimeOffset now, List<MdnsMessage> queries, List<DiscoveredPeer> lost)
    {
        if (IsSelf(ptr.Target))
        {
            return;
        }

        if (ptr.Ttl == 0)
        {
            RemoveEntry(ptr.Target, lost);
            return;
        }

        Entry entry = GetOrCreate(ptr.Target, now);
        entry.ExpiresAt = Max(entry.ExpiresAt, now.AddSeconds(ptr.Ttl));

        if (entry.Port is null && !entry.SrvRequested)
        {
            entry.SrvRequested = true;
            queries.Add(MdnsQueries.ResolveInstance(ptr.Target));
        }
    }

    private void HandleSrv(SrvRecord srv, DateTimeOffset now, List<MdnsMessage> queries, List<DiscoveredPeer> lost)
    {
        if (IsSelf(srv.Name))
        {
            return;
        }

        if (srv.Ttl == 0)
        {
            RemoveEntry(srv.Name, lost);
            return;
        }

        Entry entry = GetOrCreate(srv.Name, now);
        entry.Port = srv.Port;
        if (entry.Host != srv.Target) { entry.Addresses.Clear(); entry.SourceAddresses.Clear(); entry.HostRequested = false; }
        entry.Host = srv.Target;
        entry.ExpiresAt = Max(entry.ExpiresAt, now.AddSeconds(srv.Ttl));

        if (!entry.HostRequested)
        {
            entry.HostRequested = true;
            queries.Add(MdnsQueries.ResolveHost(srv.Target));
        }
    }

    public int? RequiredVersion { get; init; }

    private void HandleTxt(TxtRecord txt, List<DiscoveredPeer> lost)
    {
        if (IsSelf(txt.Name))
        {
            return;
        }

        Entry entry = GetOrCreate(txt.Name, DateTimeOffset.UtcNow);
        (string name, string? fingerprint) = FeigeService.ParseTxt(txt.Entries);
        entry.VersionMatches = RequiredVersion is null || txt.Ttl != 0 && txt.Entries.Contains($"version={RequiredVersion}");
        if (!entry.VersionMatches && entry.Reported is { } reported)
        {
            _peers.Remove(reported);
            lost.Add(reported);
            entry.Reported = null;
        }
        entry.DeviceName = name;
        entry.Fingerprint = fingerprint;
    }

    private void HandleAddress(AddressRecord record, DateTimeOffset now, MdnsDatagram datagram)
    {
        IPAddress address = record.Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!LanAddressPolicy.IsLocal(address) || IPAddress.IsLoopback(address)) return;
        if (address.IsIPv6LinkLocal)
        {
            // Scope numbers are local to the receiver. Never use the sender's interface number.
            long scope = datagram.IPv6ScopeId;
            if (scope == 0 && datagram.Source.Address.IsIPv6LinkLocal) scope = datagram.Source.Address.ScopeId;
            if (scope <= 0) return;
            address = new IPAddress(address.GetAddressBytes(), scope);
        }
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Host != record.Name) continue;
            if (record.Ttl == 0) { entry.Addresses.Remove(address); entry.SourceAddresses.Remove(address); continue; }
            entry.Addresses[address] = now.AddSeconds(record.Ttl);
            if (address.Equals(datagram.Source.Address)) entry.SourceAddresses.Add(address);
            entry.ExpiresAt = Max(entry.ExpiresAt, now.AddSeconds(record.Ttl));
        }
    }

    public Func<IReadOnlyList<(IPAddress Address, int PrefixLength)>> LocalSubnetsProvider { get; init; } = LanAddressPolicy.LocalSubnets;

    private void CollectCompleted(DateTimeOffset now, List<DiscoveredPeer> found, List<DiscoveredPeer> lost)
    {
        var subnets = LocalSubnetsProvider();
        foreach (Entry entry in _entries.Values)
        {
            foreach (var expired in entry.Addresses.Where(row => row.Value <= now).Select(row => row.Key).ToArray())
            { entry.Addresses.Remove(expired); entry.SourceAddresses.Remove(expired); }
            var addresses = entry.Addresses.Keys
                .OrderBy(address => entry.SourceAddresses.Contains(address) ? 0 : subnets.Any(local => LanAddressPolicy.IsSameSubnet(address, local.Address, local.PrefixLength)) ? 1 : 2)
                .ThenBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : address.IsIPv6LinkLocal ? 2 : 1)
                .ThenBy(address => address.ToString(), StringComparer.Ordinal)
                .Take(V3PeerConnector.MaxCandidates).ToArray();
            bool compatible = RequiredVersion is null || entry.VersionMatches;
            if (!compatible || entry.Port is null or 0 || addresses.Length == 0)
            {
                if (entry.Reported is { } old) { _peers.Remove(old); lost.Add(old); entry.Reported = null; }
                continue;
            }
            var targets = addresses.Select(address => new IPEndPoint(address, entry.Port.Value)).ToArray();
            var peer = new DiscoveredPeer(entry.Instance, entry.DeviceName, entry.Fingerprint, targets[0]) { EndPoints = targets };
            if (entry.Reported is { } previous)
            {
                if (previous.DeviceName == peer.DeviceName && previous.Fingerprint == peer.Fingerprint && previous.EndPoints.SequenceEqual(targets)) continue;
                _peers.Remove(previous);
            }
            entry.Reported = peer; _peers.Add(peer); found.Add(peer);
        }
    }

    private List<DiscoveredPeer> SweepExpired(DateTimeOffset now)
    {
        List<DiscoveredPeer> lost = [];

        foreach (DnsName instance in _entries.Keys.ToArray())
        {
            Entry entry = _entries[instance];
            if (entry.ExpiresAt > now)
            {
                continue;
            }

            RemoveEntry(instance, lost);
        }

        return lost;
    }

    private void RemoveEntry(DnsName instance, List<DiscoveredPeer> lost)
    {
        if (!_entries.Remove(instance, out Entry? entry))
        {
            return;
        }

        if (entry.Reported is { } peer)
        {
            _peers.RemoveAll(candidate => candidate.Instance == instance);
            lost.Add(peer);
        }
    }

    private Entry GetOrCreate(DnsName instance, DateTimeOffset now)
    {
        if (_entries.TryGetValue(instance, out Entry? existing))
        {
            return existing;
        }

        var entry = new Entry(instance, now);
        _entries.Add(instance, entry);
        return entry;
    }

    private bool IsSelf(DnsName instance)
        => LocalInstanceLabel is not null
            && string.Equals(instance.FirstLabel, LocalInstanceLabel, StringComparison.OrdinalIgnoreCase);

    private async Task SendQueryAsync(MdnsMessage query)
    {
        try
        {
            await _transport.SendAsync(query, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A dropped query is retried by the refresh loop.
        }
    }

    private void RaiseFound(List<DiscoveredPeer> peers)
    {
        foreach (DiscoveredPeer peer in peers)
        {
            try
            {
                PeerFound?.Invoke(this, peer);
            }
            catch (Exception)
            {
                // A subscriber bug must not break discovery.
            }
        }
    }

    private void RaiseLost(List<DiscoveredPeer> peers)
    {
        foreach (DiscoveredPeer peer in peers)
        {
            try
            {
                PeerLost?.Invoke(this, peer);
            }
            catch (Exception)
            {
                // Same as above.
            }
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private sealed class Entry(DnsName instance, DateTimeOffset now)
    {
        public bool VersionMatches { get; set; }

        public DnsName Instance { get; } = instance;

        public string DeviceName { get; set; } = string.Empty;

        public string? Fingerprint { get; set; }

        public ushort? Port { get; set; }

        public DnsName? Host { get; set; }

        public Dictionary<IPAddress, DateTimeOffset> Addresses { get; } = [];
        public HashSet<IPAddress> SourceAddresses { get; } = [];

        public DateTimeOffset ExpiresAt { get; set; } = now;

        public DiscoveredPeer? Reported { get; set; }

        public bool SrvRequested { get; set; }

        public bool HostRequested { get; set; }
    }
}
