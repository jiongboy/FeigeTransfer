using System.Net;
using System.Net.Sockets;
using Feige.Core.Net;

namespace Feige.Core.Discovery;

/// <summary>
/// Publishes this device as <c>&lt;name&gt;._feige._tcp.local</c> and answers mDNS queries for it (RFC 6762 responder,
/// RFC 6763 service registration).
/// </summary>
/// <remarks>
/// <para>
/// Only the local transfer port and descriptive text are advertised; no key material is. A peer must still run the
/// authenticated handshake; discovery data is an address hint, not a verified identity.
/// </para>
/// <para>
/// Two browser-side details are handled here: a query that arrives from a source port other than 5353, or with the QU
/// bit set, is answered by unicast (RFC 6762 §6.7), and startup repeats the announcement so a peer that just joined the
/// link does not have to wait for the next refresh.
/// </para>
/// </remarks>
public sealed class MdnsAdvertiser : IAsyncDisposable
{
    private readonly IMdnsTransport _transport;
    private readonly IReadOnlyList<IPAddress> _addresses;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Creates an advertiser; nothing is sent until <see cref="StartAsync"/>.</summary>
    /// <param name="transport">Shared mDNS transport.</param>
    /// <param name="device">Identity and display name to advertise.</param>
    /// <param name="port">TCP port of the local <see cref="PeerListener"/>.</param>
    /// <param name="addresses">Addresses to publish; defaults to <see cref="FeigeService.LocalAddresses"/>.</param>
    public MdnsAdvertiser(IMdnsTransport transport, LocalDevice device, int port, IReadOnlyList<IPAddress>? addresses = null, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(device);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1..65535.");
        }

        _transport = transport;
        _addresses = addresses ?? FeigeService.LocalAddresses();

        InstanceName = FeigeService.InstanceName(device.DeviceName, device.Fingerprint, version);
        HostName = new DnsName(
            FeigeService.TruncateUtf8($"{Environment.MachineName.ToLowerInvariant()}-feige", 63),
            FeigeService.DomainLabel);
        Txt = FeigeService.BuildTxt(device.DeviceName, device.Fingerprint, version);
        Port = port;

        _transport.DatagramReceived += OnDatagramReceived;
    }

    /// <summary>Instance name this device publishes.</summary>
    public DnsName InstanceName { get; }

    /// <summary>Host name that carries the address records.</summary>
    public DnsName HostName { get; }

    /// <summary>TXT entries being advertised.</summary>
    public IReadOnlyList<string> Txt { get; }

    /// <summary>Advertised TCP port.</summary>
    public int Port { get; }

    /// <summary>Addresses being advertised.</summary>
    public IReadOnlyList<IPAddress> Addresses => _addresses;

    /// <summary>Sends the first announcement, then two more in the background (RFC 6762 §8.3).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transport.SendAsync(BuildAnnouncement(MdnsDefaults.ServiceTtl), cancellationToken).ConfigureAwait(false);
        _ = RepeatAnnouncementsAsync(cancellationToken);
    }

    /// <summary>Sends an unsolicited response, e.g. after the listener's port changed.</summary>
    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
        => _transport.SendAsync(BuildAnnouncement(MdnsDefaults.ServiceTtl), cancellationToken);

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

        try
        {
            // RFC 6762 §10.1 goodbye: TTL 0 so peers drop us immediately instead of waiting for the TTL.
            await _transport.SendAsync(BuildAnnouncement(0), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The transport may already be closed during shutdown.
        }

        _cts.Dispose();
    }

    private async Task RepeatAnnouncementsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
                await _transport.SendAsync(BuildAnnouncement(MdnsDefaults.ServiceTtl), linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Announcements are best effort and must never surface as an unobserved exception.
        }
    }

    private void OnDatagramReceived(object? sender, MdnsDatagram datagram)
    {
        if (_disposed || datagram.Message.IsResponse)
        {
            return;
        }

        List<MdnsQuestion> interesting = [];
        bool wantsUnicast = datagram.Source.Port != UdpMdnsTransport.MdnsPort;

        foreach (MdnsQuestion question in datagram.Message.Questions)
        {
            if (!Matches(question.Name, question.Type))
            {
                continue;
            }

            interesting.Add(question);
            wantsUnicast |= question.UnicastResponse;
        }

        if (interesting.Count == 0)
        {
            return;
        }

        MdnsMessage response = BuildResponse(interesting);
        _ = SendAnswerAsync(response, wantsUnicast ? datagram.Source : null);
    }

    private async Task SendAnswerAsync(MdnsMessage response, IPEndPoint? unicast)
    {
        try
        {
            if (unicast is not null)
            {
                await _transport.SendAsync(response, unicast, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _transport.SendAsync(response, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A failed answer simply means the peer retries.
        }
    }

    private bool Matches(DnsName name, DnsRecordType type)
    {
        if (name == FeigeService.Type)
        {
            return type is DnsRecordType.Ptr or DnsRecordType.Any;
        }

        if (name == InstanceName)
        {
            return type is DnsRecordType.Srv or DnsRecordType.Txt or DnsRecordType.Any;
        }

        if (name == HostName)
        {
            return type is DnsRecordType.A or DnsRecordType.Aaaa or DnsRecordType.Any;
        }

        return false;
    }

    private MdnsMessage BuildResponse(IEnumerable<MdnsQuestion> questions)
    {
        var response = new MdnsMessage { IsResponse = true, IsAuthoritative = true };
        var seen = new HashSet<DnsRecordType>();

        foreach (MdnsQuestion question in questions)
        {
            if (question.Name == FeigeService.Type && seen.Add(DnsRecordType.Ptr))
            {
                response.Answers.Add(new PtrRecord(FeigeService.Type, InstanceName));
            }
            else if (question.Name == InstanceName && question.Type is DnsRecordType.Srv or DnsRecordType.Any && seen.Add(DnsRecordType.Srv))
            {
                response.Answers.Add(CreateSrv());
                response.Answers.Add(CreateTxt());
            }
            else if (question.Name == InstanceName && question.Type == DnsRecordType.Txt && seen.Add(DnsRecordType.Txt))
            {
                response.Answers.Add(CreateTxt());
            }
            else if (question.Name == HostName && question.Type is DnsRecordType.A or DnsRecordType.Aaaa or DnsRecordType.Any
                && seen.Add(DnsRecordType.A))
            {
                AddAddresses(response.Answers);
            }
        }

        // Additionals save the browser a round trip and are what most responders rely on.
        if (response.Answers.Exists(r => r.Type == DnsRecordType.Ptr))
        {
            AddIfMissing(response.Additionals, () => CreateSrv());
            AddIfMissing(response.Additionals, () => CreateTxt());
            AddAddresses(response.Additionals);
        }
        else if (response.Answers.Exists(r => r.Type == DnsRecordType.Srv))
        {
            AddAddresses(response.Additionals);
        }

        return response;
    }

    private MdnsMessage BuildAnnouncement(uint ttl)
    {
        var message = new MdnsMessage { IsResponse = true, IsAuthoritative = true };

        message.Answers.Add(new PtrRecord(FeigeService.Type, InstanceName, ttl));
        message.Answers.Add(CreateSrv(ttl));
        message.Answers.Add(CreateTxt(ttl));

        // RFC 6763 §12.2: the host's addresses belong in additionals, not duplicated in answers.
        AddAddresses(message.Additionals, ttl);
        return message;
    }

    private SrvRecord CreateSrv(uint ttl = MdnsDefaults.HostTtl)
        => new(InstanceName, (ushort)Port, HostName, ttl: ttl);

    private TxtRecord CreateTxt(uint ttl = MdnsDefaults.HostTtl)
        => new(InstanceName, Txt, ttl);

    private void AddAddresses(List<MdnsRecord> target, uint ttl = MdnsDefaults.HostTtl)
    {
        foreach (IPAddress address in _addresses)
        {
            target.Add(new AddressRecord(HostName, address, ttl));
        }
    }

    private static void AddIfMissing(List<MdnsRecord> target, Func<MdnsRecord> factory)
    {
        MdnsRecord record = factory();
        if (!target.Exists(existing => existing.Type == record.Type && existing.Name == record.Name))
        {
            target.Add(record);
        }
    }
}
