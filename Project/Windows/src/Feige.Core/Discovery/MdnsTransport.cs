using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Feige.Core.Discovery;

/// <summary>One received datagram plus where it came from.</summary>
/// <param name="Message">Parsed message.</param>
/// <param name="Source">Sender address and port.</param>
public sealed record MdnsDatagram(MdnsMessage Message, IPEndPoint Source, long IPv6ScopeId = 0);

/// <summary>
/// Sends and receives mDNS datagrams. Abstracted so the responder and browser can be exercised deterministically
/// without binding port 5353.
/// </summary>
public interface IMdnsTransport : IAsyncDisposable
{
    /// <summary>Raised for every parsed inbound datagram, on a background thread.</summary>
    event EventHandler<MdnsDatagram>? DatagramReceived;

    /// <summary>Sends to the mDNS multicast group(s) on every joined interface.</summary>
    ValueTask SendAsync(MdnsMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends to a single peer, used to answer a question that asked for a unicast reply.</summary>
    ValueTask SendAsync(MdnsMessage message, IPEndPoint target, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real mDNS transport: IPv4 (224.0.0.251) and IPv6 (ff02::fb) sockets on port 5353, one receive loop each.
/// </summary>
/// <remarks>
/// <para>
/// The application owns its per-interface mDNS transport and shares it between the browser and advertiser.
/// Both address families are attempted independently because discovery may return an A or AAAA address.
/// </para>
/// <para>
/// Binding 5353 requires <c>SO_REUSEADDR</c>: other mDNS software (Bonjour, iTunes, printers) may already hold it, and
/// coexistence is exactly what the reuse flag is for.
/// </para>
/// </remarks>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    /// <summary>IANA-assigned mDNS port.</summary>
    public const int MdnsPort = 5353;

    /// <summary>IPv4 mDNS group.</summary>
    public static readonly IPAddress Ipv4Group = IPAddress.Parse("224.0.0.251");

    /// <summary>IPv6 mDNS group.</summary>
    public static readonly IPAddress Ipv6Group = IPAddress.Parse("ff02::fb");

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _startGate = new();
    private readonly List<IPAddress> _ipv4Interfaces = [];
    private readonly List<int> _ipv6Interfaces = [];
    private readonly Dictionary<int, int> _ipv4ToIpv6 = [];
    private Socket? _ipv4;
    private Socket? _ipv6;
    private Task? _ipv4Loop;
    private Task? _ipv6Loop;
    private bool _disposed, _started;

    /// <summary>Creates a transport; call <see cref="Start"/> before sending.</summary>
    public UdpMdnsTransport(int port = MdnsPort)
    {
        Port = port;
    }

    /// <inheritdoc />
    public event EventHandler<MdnsDatagram>? DatagramReceived;

    /// <summary>Port in use, 5353 unless overridden (tests use an ephemeral port to avoid colliding with real mDNS).</summary>
    public int Port { get; }

    /// <summary>Number of interfaces joined, for diagnostics.</summary>
    public int JoinedInterfaceCount => _ipv4Interfaces.Count + _ipv6Interfaces.Count;

    /// <summary>Binds both families and starts receiving.</summary>
    /// <exception cref="SocketException">Port 5353 is held exclusively by another process.</exception>
    public void Start()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            Exception? failure = null;
            try
            {
                _ipv4 = CreateSocket(AddressFamily.InterNetwork); JoinIpv4Groups(_ipv4);
            }
            catch (Exception ex) when (ex is SocketException or NetworkInformationException)
            { failure = ex; _ipv4?.Dispose(); _ipv4 = null; }
            try
            {
                _ipv6 = CreateSocket(AddressFamily.InterNetworkV6); JoinIpv6Groups(_ipv6);
            }
            catch (Exception ex) when (ex is SocketException or NetworkInformationException)
            { failure = ex; _ipv6?.Dispose(); _ipv6 = null; }
            if (_ipv4 is null && _ipv6 is null) throw failure ?? new SocketException((int)SocketError.NetworkDown);
            _started = true;
            if (_ipv4 is not null) _ipv4Loop = ReceiveLoopAsync(_ipv4);
            if (_ipv6 is not null) _ipv6Loop = ReceiveLoopAsync(_ipv6);
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(MdnsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Start();

        byte[] payload = message.ToArray();

        // Serialized because the multicast interface option is per socket and shared by both callers.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ipv4 is { } ipv4)
            {
                var target = new IPEndPoint(Ipv4Group, Port);
                IReadOnlyList<IPAddress> interfaces = _ipv4Interfaces.Count > 0 ? _ipv4Interfaces : [IPAddress.Any];

                foreach (IPAddress address in interfaces)
                {
                    try
                    {
                        ipv4.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        await ipv4.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // A vanished adapter must not stop the other interfaces.
                    }
                }
            }

            if (_ipv6 is { } ipv6)
            {
                IReadOnlyList<int> indexes = _ipv6Interfaces.Count > 0 ? _ipv6Interfaces : [0];

                foreach (int index in indexes)
                {
                    try
                    {
                        if (index == 0)
                        {
                            ipv6.SetSocketOption(
                                SocketOptionLevel.IPv6,
                                SocketOptionName.MulticastInterface,
                                IPAddress.IPv6Any.GetAddressBytes());
                        }
                        else
                        {
                            ipv6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                        }

                        var target = new IPEndPoint(new IPAddress(Ipv6Group.GetAddressBytes(), index), Port);
                        await ipv6.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Same tolerance as above.
                    }
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(MdnsMessage message, IPEndPoint target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(target);
        Start();

        byte[] payload = message.ToArray();
        Socket socket = (target.AddressFamily == AddressFamily.InterNetworkV6 ? _ipv6 : _ipv4)
            ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_startGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await _cts.CancelAsync().ConfigureAwait(false);

        _ipv4?.Dispose();
        _ipv6?.Dispose();

        foreach (Task? loop in new[] { _ipv4Loop, _ipv6Loop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected once the sockets are gone.
            }
        }

        _sendLock.Dispose();
        _cts.Dispose();
    }

    private Socket CreateSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (family == AddressFamily.InterNetworkV6) socket.DualMode = false;
            socket.SetSocketOption(family == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, Port));

            if (family == AddressFamily.InterNetwork)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void JoinIpv4Groups(Socket socket)
    {
        foreach (NetworkInterface nic in ActiveInterfaces())
        {
            IPInterfaceProperties properties;
            UnicastIPAddressInformationCollection addresses;
            try { properties = nic.GetIPProperties(); addresses = properties.UnicastAddresses; }
            catch (NetworkInformationException) { continue; }
            try
            {
                var v4 = properties.GetIPv4Properties(); var v6 = properties.GetIPv6Properties();
                if (v4 is not null && v6 is not null) _ipv4ToIpv6[v4.Index] = v6.Index;
            }
            catch (NetworkInformationException) { }
            foreach (UnicastIPAddressInformation unicast in addresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(Ipv4Group, unicast.Address));
                    _ipv4Interfaces.Add(unicast.Address);
                }
                catch (SocketException)
                {
                    // Interface without multicast support; keep going.
                }
            }
        }
    }

    private void JoinIpv6Groups(Socket socket)
    {
        foreach (NetworkInterface nic in ActiveInterfaces())
        {
            IPv6InterfaceProperties? properties;

            try
            {
                // Throws NetworkInformationException on adapters that have no IPv6 stack at all.
                properties = nic.GetIPProperties().GetIPv6Properties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (properties is null)
            {
                continue;
            }

            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IPv6,
                    SocketOptionName.AddMembership,
                    new IPv6MulticastOption(Ipv6Group, properties.Index));
                _ipv6Interfaces.Add(properties.Index);
            }
            catch (SocketException)
            {
                // No IPv6 multicast on this adapter.
            }
        }
    }

    private static IEnumerable<NetworkInterface> ActiveInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel && nic.SupportsMulticast).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private async Task ReceiveLoopAsync(Socket socket)
    {
        byte[] buffer = new byte[9000];
        EndPoint remote = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket
                    .ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP port unreachable and friends: keep the loop alive.
                continue;
            }

            if (result.ReceivedBytes <= 0)
            {
                continue;
            }

            MdnsMessage? message = MdnsMessage.TryParse(buffer.AsSpan(0, result.ReceivedBytes));
            if (message is null || result.RemoteEndPoint is not IPEndPoint source)
            {
                continue;
            }

            try
            {
                long scope = source.AddressFamily == AddressFamily.InterNetworkV6
                    ? result.PacketInformation.Interface
                    : _ipv4ToIpv6.GetValueOrDefault(result.PacketInformation.Interface);
                if (scope == 0 && source.Address.IsIPv6LinkLocal) scope = source.Address.ScopeId;
                DatagramReceived?.Invoke(this, new MdnsDatagram(message, source, scope));
            }
            catch (Exception)
            {
                // A throwing subscriber must not kill discovery.
            }
        }
    }
}
