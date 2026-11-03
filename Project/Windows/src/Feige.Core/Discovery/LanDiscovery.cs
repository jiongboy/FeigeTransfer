using System.Net;
using Feige.Core.Net;

namespace Feige.Core.Discovery;

/// <summary>
/// One-call discovery facade: advertises this device's listener on the LAN and browses for the others over the same
/// mDNS transport.
/// </summary>
/// <remarks>
/// Both halves must share one transport, because mDNS uses a single port (5353) and two sockets bound to it would fight
/// over the multicast group. The facade owns the transport: disposing it closes discovery for both halves.
/// </remarks>
public sealed class LanDiscovery : IAsyncDisposable
{
    private readonly IMdnsTransport _transport;
    private bool _disposed;

    /// <summary>Creates discovery for a device listening on <paramref name="port"/>.</summary>
    /// <param name="device">Identity and display name to advertise.</param>
    /// <param name="port">TCP port of the local <see cref="PeerListener"/>.</param>
    /// <param name="transport">
    /// Transport to use; defaults to a real <see cref="UdpMdnsTransport"/>. Tests pass an in-memory one.
    /// </param>
    /// <param name="addresses">Addresses to advertise; defaults to the machine's unicast addresses.</param>
    public LanDiscovery(LocalDevice device, int port, IMdnsTransport? transport = null, IReadOnlyList<IPAddress>? addresses = null, int version = 1, string? avatarId = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        _transport = transport ?? new UdpMdnsTransport();
        Advertiser = new MdnsAdvertiser(_transport, device, port, addresses, version, avatarId);
        V3Advertiser = version == 3 ? Advertiser : null;
        V4Advertiser = version == 4 ? Advertiser : null;
        Advertisers = [Advertiser];
        Browser = new PeerBrowser(_transport)
        {
            LocalInstanceLabel = Advertiser.InstanceName.FirstLabel,
            LocalInstanceLabels = [Advertiser.InstanceName.FirstLabel],
            RequiredVersion = version is 3 or 4 ? version : null,
        };
    }

    /// <summary>Creates simultaneous v3 and v4 advertisements over one shared mDNS transport.</summary>
    public LanDiscovery(LocalDevice device, int v3Port, int v4Port, IMdnsTransport? transport = null,
                        IReadOnlyList<IPAddress>? addresses = null, string? avatarId = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _transport = transport ?? new UdpMdnsTransport();
        V3Advertiser = new MdnsAdvertiser(_transport, device, v3Port, addresses, version: 3, avatarId);
        V4Advertiser = new MdnsAdvertiser(_transport, device, v4Port, addresses, version: 4, avatarId);
        Advertiser = V3Advertiser; // Compatibility alias for callers that previously read the single advertiser.
        Advertisers = [V3Advertiser, V4Advertiser];
        Browser = new PeerBrowser(_transport)
        {
            LocalInstanceLabel = V3Advertiser.InstanceName.FirstLabel,
            LocalInstanceLabels = [V3Advertiser.InstanceName.FirstLabel, V4Advertiser.InstanceName.FirstLabel],
        };
    }

    /// <summary>Raised when a peer becomes dialable.</summary>
    public event EventHandler<DiscoveredPeer>? PeerFound
    {
        add => Browser.PeerFound += value;
        remove => Browser.PeerFound -= value;
    }

    /// <summary>Raised when a peer goes away.</summary>
    public event EventHandler<DiscoveredPeer>? PeerLost
    {
        add => Browser.PeerLost += value;
        remove => Browser.PeerLost -= value;
    }

    /// <summary>The primary registration half; in dual mode this aliases the v3 advertisement.</summary>
    public MdnsAdvertiser Advertiser { get; }

    /// <summary>The v3 registration, when enabled.</summary>
    public MdnsAdvertiser? V3Advertiser { get; }

    /// <summary>The v4 registration, when enabled.</summary>
    public MdnsAdvertiser? V4Advertiser { get; }

    /// <summary>All local registrations sharing this discovery transport.</summary>
    public IReadOnlyList<MdnsAdvertiser> Advertisers { get; }

    /// <summary>The browse half.</summary>
    public PeerBrowser Browser { get; }

    /// <summary>Currently reachable peers, excluding this device.</summary>
    public IReadOnlyList<DiscoveredPeer> Peers => Browser.Peers;

    /// <summary>Announces this device and starts browsing.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (MdnsAdvertiser advertiser in Advertisers)
        {
            await advertiser.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        await Browser.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-sends the browse query; call when the user opens the device list.</summary>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => Browser.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Advertisers first: all their goodbyes must go out before the transport is closed.
        foreach (MdnsAdvertiser advertiser in Advertisers)
        {
            await advertiser.DisposeAsync().ConfigureAwait(false);
        }
        await Browser.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
