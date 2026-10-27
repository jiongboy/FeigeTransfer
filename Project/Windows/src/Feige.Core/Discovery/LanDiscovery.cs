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
    public LanDiscovery(LocalDevice device, int port, IMdnsTransport? transport = null, IReadOnlyList<IPAddress>? addresses = null, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _transport = transport ?? new UdpMdnsTransport();
        Advertiser = new MdnsAdvertiser(_transport, device, port, addresses, version);
        Browser = new PeerBrowser(_transport)
        {
            LocalInstanceLabel = Advertiser.InstanceName.FirstLabel,
            RequiredVersion = version == 3 ? 3 : null,
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

    /// <summary>The registration half.</summary>
    public MdnsAdvertiser Advertiser { get; }

    /// <summary>The browse half.</summary>
    public PeerBrowser Browser { get; }

    /// <summary>Currently reachable peers, excluding this device.</summary>
    public IReadOnlyList<DiscoveredPeer> Peers => Browser.Peers;

    /// <summary>Announces this device and starts browsing.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await Advertiser.StartAsync(cancellationToken).ConfigureAwait(false);
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

        // Advertiser first: its goodbye must go out before the transport is closed.
        await Advertiser.DisposeAsync().ConfigureAwait(false);
        await Browser.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
