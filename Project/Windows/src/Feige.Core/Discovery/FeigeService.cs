using System.Net;
using Feige.Core.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Feige.Core.Discovery;

/// <summary>A peer found on the LAN. Nothing here is trusted until the handshake proves it.</summary>
/// <param name="Instance">DNS-SD instance name, unique per responder on the link.</param>
/// <param name="DeviceName">Name from the TXT record; may be truncated or absent, always confirm it in the handshake.</param>
/// <param name="Fingerprint">Formatted fingerprint from TXT, when present. Advisory only — mDNS is unauthenticated.</param>
/// <param name="EndPoint">Address to dial: the first usable address of the advertised host.</param>
public sealed record DiscoveredPeer(DnsName Instance, string DeviceName, string? Fingerprint, IPEndPoint EndPoint)
{
    /// <summary>What the UI shows before a connection exists.</summary>
    public string DisplayName => string.IsNullOrEmpty(DeviceName) ? Instance.FirstLabel : DeviceName;

    /// <summary><c>address:port</c>, for a device list subtitle.</summary>
    public string EndpointText => EndPoint.ToString();

    /// <summary>All live LAN candidates for this instance; only one will reach the handshake.</summary>
    public IReadOnlyList<IPEndPoint> EndPoints { get; init; } = [EndPoint];

    /// <summary>First few fingerprint groups, for a subtitle; the full value belongs in the confirmation dialog.</summary>
    public string ShortFingerprint
        => string.IsNullOrEmpty(Fingerprint)
            ? "指纹待握手确认"
            : Fingerprint.Length <= 23 ? Fingerprint : Fingerprint[..23] + "…";
}

/// <summary>
/// The DNS-SD advertisement 通信协议 §1 leaves open: service type <c>_feige._tcp</c> plus the TXT keys this
/// implementation defines.
/// </summary>
/// <remarks>
/// <para>
/// TXT records are limited to short values, so they carry only what a device list needs before connecting:
/// <c>v</c> (protocol version), <c>name</c> (display name), <c>fp</c> (fingerprint without spaces). Everything a
/// security decision depends on — role, keys, SAS — comes from the handshake, never from mDNS.
/// </para>
/// <para>
/// The instance label is limited to 63 bytes, so a long device name is truncated there and kept in full in <c>name</c>.
/// </para>
/// </remarks>
public static class FeigeService
{
    /// <summary>Service type label.</summary>
    public const string ServiceLabel = "_feige";

    /// <summary>Transport label.</summary>
    public const string TransportLabel = "_tcp";

    /// <summary>Domain label.</summary>
    public const string DomainLabel = "local";

    /// <summary>Protocol version advertised in TXT; must match <c>HandshakeOffer.CurrentVersion</c>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>TXT key for the protocol version.</summary>
    public const string TxtVersion = "v";

    /// <summary>TXT key for the display name.</summary>
    public const string TxtName = "name";

    /// <summary>TXT key for the fingerprint with spaces removed.</summary>
    public const string TxtFingerprint = "fp";

    /// <summary>The service type every peer browses for.</summary>
    public static DnsName Type { get; } = new(ServiceLabel, TransportLabel, DomainLabel);

    /// <summary>Builds the instance name for a device, truncating the label to the 63-byte DNS limit.</summary>
    public static DnsName InstanceName(string deviceName, string? fingerprint = null, int version = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);

        if (version is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(version));
        string label;
        if (version == 3)
        {
            string compact = fingerprint?.Replace(" ", string.Empty).ToUpperInvariant() ?? string.Empty;
            if (compact.Length != 64 || !compact.All(char.IsAsciiHexDigit))
                throw new ArgumentException("V3 discovery requires a full SHA-256 fingerprint.", nameof(fingerprint));
            label = "Feige-" + compact[..20];
        }
        else label = TruncateUtf8(deviceName.Trim(), 63);
        return Type.WithPrefix(label);
    }

    /// <summary>Builds the TXT entries for a device.</summary>
    public static IReadOnlyList<string> BuildTxt(string deviceName, string fingerprint, int version = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (version is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(version));

        if (version == 3) return ["version=3", $"name={deviceName.Trim()}"];
        return
        [
            $"{TxtVersion}={ProtocolVersion}",
            $"{TxtName}={deviceName.Trim()}",
            $"{TxtFingerprint}={fingerprint.Replace(" ", string.Empty, StringComparison.Ordinal)}",
        ];
    }

    /// <summary>Parses the TXT entries into a display name and fingerprint, tolerating missing keys.</summary>
    public static (string DeviceName, string? Fingerprint) ParseTxt(IEnumerable<string> entries)
    {
        string? name = null;
        string? fingerprint = null;
        int version = 0;

        foreach (string entry in entries)
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = entry[..separator];
            string value = entry[(separator + 1)..];

            if (string.Equals(key, TxtName, StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, TxtFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                fingerprint = value;
            }
            else if (string.Equals(key, TxtVersion, StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, out version);
            }
        }

        // A different protocol version is still shown: the handshake rejects it with a clear error, which is a far
        // better message than "device silently disappeared".
        _ = version;
        return (string.IsNullOrEmpty(name) ? string.Empty : name, string.IsNullOrEmpty(fingerprint) ? null : fingerprint);
    }

    /// <summary>
    /// Addresses to advertise: up, non-loopback unicast addresses of every multicast-capable adapter.
    /// </summary>
    /// <remarks>Only addresses allowed by the LAN connector are published. Receivers attach their own IPv6 scope.</remarks>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();

        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return addresses; }
        foreach (NetworkInterface nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            UnicastIPAddressInformationCollection unicastAddresses;

            try
            {
                unicastAddresses = nic.GetIPProperties().UnicastAddresses;
            }
            catch (NetworkInformationException)
            {
                // Adapter reported itself up but has no configured address family.
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in unicastAddresses)
            {
                IPAddress address = unicast.Address;

                if (!LanAddressPolicy.IsLocal(address) || IPAddress.IsLoopback(address))
                {
                    continue;
                }

                if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }

                if (!addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    /// <summary>Trims a string to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    internal static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        string trimmed = value;
        while (trimmed.Length > 1 && Encoding.UTF8.GetByteCount(trimmed) > maxBytes)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    /// <summary>Short fingerprint of an identity, for the TXT record.</summary>
    internal static string ShortFingerprint(ReadOnlySpan<byte> subjectPublicKeyInfo)
        => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
}

/// <summary>Builds the query messages the browser sends.</summary>
internal static class MdnsQueries
{
    /// <summary>A PTR browse for the service type.</summary>
    internal static MdnsMessage BrowseForService(DnsName serviceType)
    {
        var message = new MdnsMessage();
        message.Questions.Add(new MdnsQuestion(serviceType, DnsRecordType.Ptr));
        return message;
    }

    /// <summary>Asks for the SRV and TXT records of one instance in a single datagram.</summary>
    internal static MdnsMessage ResolveInstance(DnsName instance)
    {
        var message = new MdnsMessage();
        message.Questions.Add(new MdnsQuestion(instance, DnsRecordType.Srv));
        message.Questions.Add(new MdnsQuestion(instance, DnsRecordType.Txt));
        return message;
    }

    /// <summary>Asks for the addresses of a host name.</summary>
    internal static MdnsMessage ResolveHost(DnsName host)
    {
        var message = new MdnsMessage();
        message.Questions.Add(new MdnsQuestion(host, DnsRecordType.Any));
        return message;
    }
}
