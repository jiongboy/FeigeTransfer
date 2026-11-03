using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Net;

namespace Feige.Core.Discovery;

/// <summary>User-configured probe targets: one private IPv4 address, a /24–/32 prefix, or a three-octet /24 shorthand.</summary>
public static class ProbeTargets
{
    public const int MaxEntries = 16;
    public const int MaxHosts = 4096;

    /// <summary>
    /// Router default LANs offered for one-click entry. None is probed until the user saves it: each /24 costs 254
    /// probes a minute and the whole list would nearly exhaust <see cref="MaxHosts"/>. Keep in sync with the macOS list.
    /// </summary>
    public static IReadOnlyList<(string Target, string Note)> Common { get; } =
    [
        ("192.168.0.0/24", "TP-Link、D-Link、Netgear 等"),
        ("192.168.1.0/24", "多数路由器、运营商光猫"),
        ("192.168.3.0/24", "华为、荣耀路由器"),
        ("192.168.8.0/24", "华为随身 Wi-Fi、4G/5G 路由"),
        ("192.168.31.0/24", "小米路由器"),
        ("192.168.50.0/24", "华硕路由器"),
        ("192.168.68.0/24", "TP-Link Deco"),
        ("192.168.86.0/24", "Google Nest Wifi"),
        ("192.168.199.0/24", "极路由"),
        ("10.0.0.0/24", "部分运营商路由器"),
        ("10.0.1.0/24", "Apple AirPort"),
    ];

    /// <summary>
    /// Company subnets probed without being saved, but only while this machine itself has a 10.80.0.0/16 address, so
    /// devices off the company network never sweep them. Keep in sync with the macOS list.
    /// </summary>
    public static IReadOnlyList<string> Company { get; } = ["10.80.1.0/24", "10.80.10.0/24"];

    /// <summary>The unsaved targets that apply to a machine with <paramref name="localAddresses"/>.</summary>
    public static IReadOnlyList<string> Automatic(IEnumerable<IPAddress> localAddresses)
        => localAddresses.Any(address => address.AddressFamily == AddressFamily.InterNetwork
                                         && address.GetAddressBytes() is [10, 80, _, _]) ? Company : [];

    /// <summary>Hosts for one probe round: the saved targets plus the automatic ones. Saved targets win when both do not fit.</summary>
    public static IReadOnlyList<uint> RoundHosts(IReadOnlyCollection<string> saved, IEnumerable<IPAddress> localAddresses)
    {
        IReadOnlyList<uint> baseHosts = Hosts(saved) ?? [];
        IReadOnlyList<string> extra = Automatic(localAddresses);
        if (extra.Count == 0) return baseHosts;
        return Hosts(saved.Select(Normalize).Union(extra).ToArray()) ?? baseHosts;
    }

    /// <summary>Canonical form of <paramref name="entry"/>: trimmed, with a three-octet shorthand such as <c>10.80.1</c> expanded to <c>10.80.1.0/24</c>.</summary>
    public static string Normalize(string entry)
    {
        string trimmed = entry.Trim();
        return !trimmed.Contains('/') && trimmed.Split('.').Length == 3 ? trimmed + ".0/24" : trimmed;
    }

    /// <summary>Returns the host addresses of <paramref name="entry"/>, or null when it is not an allowed private target.</summary>
    public static IReadOnlyList<uint>? Hosts(string entry)
    {
        string[] parts = Normalize(entry).Split('/');
        if (parts.Length > 2 || ParseIPv4(parts[0]) is not uint address || !IsPrivate(address)) return null;
        if (parts.Length == 1) return [address];
        if (parts[1].Length is 0 or > 2 || !parts[1].All(char.IsAsciiDigit)) return null;
        int prefix = int.Parse(parts[1]);
        if (prefix is < 24 or > 32) return null;
        if (prefix == 32) return [address];
        if (prefix == 31) return [address & ~1u, address | 1u];
        uint mask = uint.MaxValue << 32 - prefix;
        uint network = address & mask;
        uint broadcast = network | ~mask;
        var hosts = new List<uint>();
        for (uint host = network + 1; host < broadcast; host++) hosts.Add(host);
        return hosts;
    }

    /// <summary>Returns the deduplicated hosts of <paramref name="entries"/>, or null when any limit or entry is invalid.</summary>
    public static IReadOnlyList<uint>? Hosts(IReadOnlyCollection<string> entries)
    {
        if (entries.Count > MaxEntries) return null;
        var seen = new HashSet<uint>();
        var result = new List<uint>();
        foreach (string entry in entries)
        {
            if (Hosts(entry) is not { } hosts) return null;
            foreach (uint host in hosts)
                if (seen.Add(host)) result.Add(host);
        }
        return result.Count <= MaxHosts ? result : null;
    }

    public static bool IsPrivate(uint address)
    {
        uint a = address >> 24, b = address >> 16 & 0xff;
        return a == 10 || a == 172 && b is >= 16 and <= 31 || a == 192 && b == 168 || a == 169 && b == 254;
    }

    private static uint? ParseIPv4(string text)
    {
        string[] octets = text.Split('.');
        if (octets.Length != 4) return null;
        uint value = 0;
        foreach (string octet in octets)
        {
            if (octet.Length is 0 or > 3 || !octet.All(char.IsAsciiDigit) || octet.Length > 1 && octet[0] == '0') return null;
            uint number = uint.Parse(octet);
            if (number > 255) return null;
            value = value << 8 | number;
        }
        return value;
    }
}

public sealed record ProbeAdvertisement(string Name, string Fingerprint, string Capabilities, int Port, string? AvatarId);

public sealed record ProbePeer(IPAddress Address, string Name, string Fingerprint, IReadOnlySet<int> ProtocolVersions, int Port, string? AvatarId);

/// <summary>Probe datagrams. Their content is untrusted, exactly like mDNS TXT data.</summary>
public static class ProbeMessage
{
    public const int DatagramSize = 512;

    private sealed record Request(
        [property: JsonPropertyName("feige")] string Feige,
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("nonce")] string Nonce);

    private sealed record Reply(
        [property: JsonPropertyName("feige")] string Feige,
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("fp")] string Fingerprint,
        [property: JsonPropertyName("caps")] string Capabilities,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("avatar"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarId);

    public static byte[] CreateRequest(string nonce)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Request("probe", 1, nonce));
        byte[] datagram = new byte[DatagramSize];
        json.CopyTo(datagram, 0);
        return datagram;
    }

    public static byte[]? CreateReply(ReadOnlySpan<byte> request, ProbeAdvertisement advertisement)
    {
        if (request.Length != DatagramSize || Decode<Request>(request) is not { Feige: "probe", Version: 1 } decoded || !IsNonce(decoded.Nonce)) return null;
        byte[] reply = JsonSerializer.SerializeToUtf8Bytes(new Reply("reply", 1, decoded.Nonce, advertisement.Name,
            advertisement.Fingerprint, advertisement.Capabilities, advertisement.Port, advertisement.AvatarId));
        return reply.Length <= DatagramSize ? reply : null;
    }

    public static (ProbePeer Peer, string Nonce)? ParseReply(ReadOnlySpan<byte> datagram, IPAddress address)
    {
        if (datagram.Length > DatagramSize || Decode<Reply>(datagram) is not { Feige: "reply", Version: 1 } reply) return null;
        if (reply.Port is <= 0 or > 65535 || string.IsNullOrEmpty(reply.Name) || Encoding.UTF8.GetByteCount(reply.Name) > 128 ||
            reply.Name.Any(char.IsControl) || reply.Fingerprint is not { Length: 64 } || !reply.Fingerprint.All(char.IsAsciiHexDigit) ||
            reply.Capabilities is null) return null;
        var versions = new HashSet<int>();
        foreach (string part in reply.Capabilities.Split(','))
            if (int.TryParse(part.Trim(), out int version) && version is >= 4 and <= 6) versions.Add(version);
        if (versions.Count == 0) return null;
        return (new ProbePeer(address, reply.Name, reply.Fingerprint.ToUpperInvariant(), versions, reply.Port, reply.AvatarId), reply.Nonce);
    }

    private static bool IsNonce(string? value)
    {
        Span<byte> bytes = stackalloc byte[24];
        return value is not null && Convert.TryFromBase64String(value, bytes, out int count) && count == 16;
    }

    private static T? Decode<T>(ReadOnlySpan<byte> datagram) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(datagram.TrimEnd((byte)0)); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Finds Feige devices at user-configured addresses over one UDP socket.</summary>
public sealed class UnicastProbe : IAsyncDisposable
{
    public const int DefaultPort = 52526;
    public const int HostsPerSecond = 50;
    public const int RepliesPerSecond = 100;
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProbeAdvertisement _advertisement;
    private readonly int _port;
    private readonly object _gate = new();
    private readonly List<string> _nonces = [];
    private Task? _receiveLoop;
    private long _replyWindow;
    private int _repliesInWindow;

    public UnicastProbe(ProbeAdvertisement advertisement, bool respondsToProbes, int port = DefaultPort)
    {
        _advertisement = advertisement;
        _port = port;
        RespondsToProbes = respondsToProbes;
    }

    /// <summary>Raised on the receive loop for each validated reply from another device.</summary>
    public event Action<ProbePeer>? PeerFound;

    private bool RespondsToProbes { get; }

    /// <summary>False when the fixed port was taken: this device can still probe others but cannot be found.</summary>
    public bool CanRespond { get; private set; }

    /// <summary>Opens the socket. Falls back to an ephemeral port so outgoing probes keep working.</summary>
    public void Start()
    {
        if (OperatingSystem.IsWindows()) _socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            CanRespond = RespondsToProbes;
        }
        catch (SocketException) { _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Probes <paramref name="hosts"/> at the paced rate.</summary>
    public async Task ScanAsync(IReadOnlyList<uint> hosts, CancellationToken ct)
    {
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            // Late replies from the previous round still count.
            _nonces.Add(nonce);
            if (_nonces.Count > 2) _nonces.RemoveAt(0);
        }
        byte[] request = ProbeMessage.CreateRequest(nonce);
        int batch = Math.Max(1, HostsPerSecond / 10);
        for (int index = 0; index < hosts.Count; index += batch)
        {
            if (index > 0) await Task.Delay(100, ct).ConfigureAwait(false);
            foreach (uint host in hosts.Skip(index).Take(batch))
            {
                var target = new IPEndPoint(new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)host))), _port);
                try { await _socket.SendToAsync(request, SocketFlags.None, target, ct).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[ProbeMessage.DatagramSize + 1];
        while (!_lifetime.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (!_lifetime.IsCancellationRequested) { continue; }
            if (received.ReceivedBytes <= ProbeMessage.DatagramSize && received.RemoteEndPoint is IPEndPoint remote && LanAddressPolicy.IsLocal(remote.Address))
                await HandleAsync(buffer.AsMemory(0, received.ReceivedBytes), remote).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote)
    {
        if (ProbeMessage.ParseReply(datagram.Span, remote.Address) is { } reply)
        {
            bool expected;
            lock (_gate) expected = _nonces.Contains(reply.Nonce);
            if (expected && !string.Equals(reply.Peer.Fingerprint, _advertisement.Fingerprint, StringComparison.OrdinalIgnoreCase))
                PeerFound?.Invoke(reply.Peer);
        }
        else if (CanRespond && AllowReply() && ProbeMessage.CreateReply(datagram.Span, _advertisement) is { } answer)
        {
            try { await _socket.SendToAsync(answer, SocketFlags.None, remote, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private bool AllowReply()
    {
        long window = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (window != _replyWindow) { _replyWindow = window; _repliesInWindow = 0; }
        return ++_repliesInWindow <= RepliesPerSecond;
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _socket.Dispose();
        if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
