using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Feige.Core.Discovery;

/// <summary>DNS record types this implementation reads or writes.</summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,

    /// <summary>Domain name alias, the type DNS-SD uses to publish a service.</summary>
    Ptr = 12,

    /// <summary>Key/value strings.</summary>
    Txt = 16,

    /// <summary>IPv6 address.</summary>
    Aaaa = 28,

    /// <summary>Service location: priority, weight, port, target host.</summary>
    Srv = 33,

    /// <summary>Wildcard, only legal in questions.</summary>
    Any = 255,
}

/// <summary>DNS class and flag bits that matter for mDNS (RFC 6762).</summary>
public static class DnsClass
{
    /// <summary>The internet class, the only one mDNS uses.</summary>
    public const ushort In = 1;

    /// <summary>Set by a responder on records that supersede cached ones (RFC 6762 §10.2).</summary>
    public const ushort CacheFlush = 0x8000;

    /// <summary>Set by a querier asking for a unicast reply (QU bit, RFC 6762 §5.4).</summary>
    public const ushort UnicastResponse = 0x8000;
}

/// <summary>Header flag bits used by mDNS.</summary>
public static class MdnsFlags
{
    /// <summary>This datagram is a response.</summary>
    public const ushort Response = 0x8000;

    /// <summary>Authoritative answer, always set by an mDNS responder.</summary>
    public const ushort Authoritative = 0x0400;
}

/// <summary>A question in an mDNS query.</summary>
/// <param name="Name">Name being asked about.</param>
/// <param name="Type">Record type requested.</param>
/// <param name="UnicastResponse">QU bit; we always answer multicast, so this only affects whether we reply immediately.</param>
public sealed record MdnsQuestion(DnsName Name, DnsRecordType Type, bool UnicastResponse = false);

/// <summary>Base of the resource records this implementation understands.</summary>
public abstract class MdnsRecord
{
    /// <summary>Creates a record.</summary>
    protected MdnsRecord(DnsName name, DnsRecordType type, uint ttl, bool cacheFlush)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type;
        Ttl = ttl;
        CacheFlush = cacheFlush;
    }

    /// <summary>Owner name.</summary>
    public DnsName Name { get; }

    /// <summary>Record type.</summary>
    public DnsRecordType Type { get; }

    /// <summary>Lifetime in seconds; 0 means "goodbye, drop this record" (RFC 6762 §10.1).</summary>
    public uint Ttl { get; }

    /// <summary>Cache-flush bit.</summary>
    public bool CacheFlush { get; }

    /// <summary>Writes the type-specific portion of the record.</summary>
    internal abstract void WriteRData(DnsWriter writer);
}

/// <summary>A PTR record: service type → instance name.</summary>
public sealed class PtrRecord : MdnsRecord
{
    /// <summary>Creates a PTR record.</summary>
    public PtrRecord(DnsName name, DnsName target, uint ttl = MdnsDefaults.ServiceTtl, bool cacheFlush = false)
        : base(name, DnsRecordType.Ptr, ttl, cacheFlush)
    {
        Target = target;
    }

    /// <summary>The name this record points at.</summary>
    public DnsName Target { get; }

    /// <inheritdoc />
    internal override void WriteRData(DnsWriter writer) => writer.WriteName(Target);
}

/// <summary>An SRV record: instance → host and port.</summary>
public sealed class SrvRecord : MdnsRecord
{
    /// <summary>Creates an SRV record.</summary>
    public SrvRecord(DnsName name, ushort port, DnsName target, ushort priority = 0, ushort weight = 0, uint ttl = MdnsDefaults.HostTtl, bool cacheFlush = true)
        : base(name, DnsRecordType.Srv, ttl, cacheFlush)
    {
        Port = port;
        Target = target;
        Priority = priority;
        Weight = weight;
    }

    /// <summary>Selection priority; lower wins. Single-service advertisements use 0.</summary>
    public ushort Priority { get; }

    /// <summary>Load-balancing weight; single-service advertisements use 0.</summary>
    public ushort Weight { get; }

    /// <summary>TCP port the service listens on.</summary>
    public ushort Port { get; }

    /// <summary>Host name that carries the addresses.</summary>
    public DnsName Target { get; }

    /// <inheritdoc />
    internal override void WriteRData(DnsWriter writer)
    {
        writer.WriteUInt16(Priority);
        writer.WriteUInt16(Weight);
        writer.WriteUInt16(Port);
        writer.WriteName(Target);
    }
}

/// <summary>A TXT record: an ordered list of <c>key=value</c> strings.</summary>
public sealed class TxtRecord : MdnsRecord
{
    /// <summary>Creates a TXT record.</summary>
    public TxtRecord(DnsName name, IReadOnlyList<string> entries, uint ttl = MdnsDefaults.HostTtl, bool cacheFlush = true)
        : base(name, DnsRecordType.Txt, ttl, cacheFlush)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));

        foreach (string entry in entries)
        {
            if (Encoding.UTF8.GetByteCount(entry) > 255)
            {
                throw new ArgumentException("A TXT string must be at most 255 bytes.", nameof(entries));
            }
        }
    }

    /// <summary>The raw strings, in order.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>Looks up <c>key=value</c>; returns null when absent or when the entry has no value.</summary>
    public string? GetValue(string key)
    {
        string prefix = key + "=";
        foreach (string entry in Entries)
        {
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry[prefix.Length..];
            }
        }

        return null;
    }

    /// <inheritdoc />
    internal override void WriteRData(DnsWriter writer)
    {
        if (Entries.Count == 0)
        {
            // RFC 6763 §6.1: a TXT record must contain at least one (possibly empty) string.
            writer.WriteByte(0);
            return;
        }

        foreach (string entry in Entries)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(entry);
            writer.WriteByte((byte)bytes.Length);
            writer.WriteBytes(bytes);
        }
    }
}

/// <summary>An A or AAAA record.</summary>
public sealed class AddressRecord : MdnsRecord
{
    /// <summary>Creates an address record; the type follows the address family.</summary>
    public AddressRecord(DnsName name, IPAddress address, uint ttl = MdnsDefaults.HostTtl, bool cacheFlush = true)
        : base(
            name,
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? DnsRecordType.Aaaa : DnsRecordType.A,
            ttl,
            cacheFlush)
    {
        Address = address;
    }

    /// <summary>The address carried by this record.</summary>
    public IPAddress Address { get; }

    /// <inheritdoc />
    internal override void WriteRData(DnsWriter writer) => writer.WriteBytes(Address.GetAddressBytes());
}

/// <summary>TTL values. mDNS recommends 120 s for host records and 4500 s for PTR (RFC 6762 §10).</summary>
public static class MdnsDefaults
{
    /// <summary>TTL for SRV, TXT and address records.</summary>
    public const uint HostTtl = 120;

    /// <summary>TTL for the service PTR record.</summary>
    public const uint ServiceTtl = 4500;
}

/// <summary>
/// A complete mDNS datagram. Parsing supports name compression (every real responder uses it); writing does not
/// compress, which is legal and keeps the encoder trivial.
/// </summary>
public sealed class MdnsMessage
{
    private const int HeaderSize = 12;

    /// <summary>Questions.</summary>
    public List<MdnsQuestion> Questions { get; } = [];

    /// <summary>Answer records.</summary>
    public List<MdnsRecord> Answers { get; } = [];

    /// <summary>Authority records; unused by this protocol.</summary>
    public List<MdnsRecord> Authorities { get; } = [];

    /// <summary>Additional records, where mDNS puts the SRV/TXT/A that save a round trip.</summary>
    public List<MdnsRecord> Additionals { get; } = [];

    /// <summary>True for a response.</summary>
    public bool IsResponse { get; set; }

    /// <summary>True when the sender claims authority over the records (always true for mDNS responses).</summary>
    public bool IsAuthoritative { get; set; }

    /// <summary>Transaction id; 0 for mDNS queries.</summary>
    public ushort Id { get; set; }

    /// <summary>Serializes the message.</summary>
    public byte[] ToArray()
    {
        var writer = new DnsWriter();

        writer.WriteUInt16(Id);
        ushort flags = 0;
        if (IsResponse)
        {
            flags |= MdnsFlags.Response;
        }

        if (IsAuthoritative)
        {
            flags |= MdnsFlags.Authoritative;
        }

        writer.WriteUInt16(flags);
        writer.WriteUInt16(checked((ushort)Questions.Count));
        writer.WriteUInt16(checked((ushort)Answers.Count));
        writer.WriteUInt16(checked((ushort)Authorities.Count));
        writer.WriteUInt16(checked((ushort)Additionals.Count));

        foreach (MdnsQuestion question in Questions)
        {
            writer.WriteName(question.Name);
            writer.WriteUInt16((ushort)question.Type);
            writer.WriteUInt16((ushort)(DnsClass.In | (question.UnicastResponse ? DnsClass.UnicastResponse : 0)));
        }

        foreach (MdnsRecord record in Answers)
        {
            WriteRecord(writer, record);
        }

        foreach (MdnsRecord record in Authorities)
        {
            WriteRecord(writer, record);
        }

        foreach (MdnsRecord record in Additionals)
        {
            WriteRecord(writer, record);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Parses a datagram. Returns null for anything malformed or truncated instead of throwing: an mDNS socket receives
    /// arbitrary traffic on 5353, and a bad packet from one peer must not disturb the others.
    /// </summary>
    public static MdnsMessage? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        try
        {
            var reader = new DnsReader(data);
            var message = new MdnsMessage
            {
                Id = reader.ReadUInt16(),
            };

            ushort flags = reader.ReadUInt16();
            message.IsResponse = (flags & MdnsFlags.Response) != 0;
            message.IsAuthoritative = (flags & MdnsFlags.Authoritative) != 0;

            int questionCount = reader.ReadUInt16();
            int answerCount = reader.ReadUInt16();
            int authorityCount = reader.ReadUInt16();
            int additionalCount = reader.ReadUInt16();

            for (int i = 0; i < questionCount; i++)
            {
                DnsName name = reader.ReadName();
                var type = (DnsRecordType)reader.ReadUInt16();
                ushort cls = reader.ReadUInt16();
                message.Questions.Add(new MdnsQuestion(name, type, (cls & DnsClass.UnicastResponse) != 0));
            }

            // By ref: DnsReader is a ref struct whose position must survive the call.
            ReadRecords(ref reader, answerCount, message.Answers);
            ReadRecords(ref reader, authorityCount, message.Authorities);
            ReadRecords(ref reader, additionalCount, message.Additionals);

            return message;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static void ReadRecords(ref DnsReader reader, int count, List<MdnsRecord> target)
    {
        for (int i = 0; i < count; i++)
        {
            MdnsRecord? record = reader.ReadRecord();
            if (record is not null)
            {
                target.Add(record);
            }
        }
    }

    private static void WriteRecord(DnsWriter writer, MdnsRecord record)
    {
        writer.WriteName(record.Name);
        writer.WriteUInt16((ushort)record.Type);
        writer.WriteUInt16((ushort)(DnsClass.In | (record.CacheFlush ? DnsClass.CacheFlush : 0)));
        writer.WriteUInt32(record.Ttl);

        var rdata = new DnsWriter();
        record.WriteRData(rdata);
        byte[] payload = rdata.ToArray();

        writer.WriteUInt16(checked((ushort)payload.Length));
        writer.WriteBytes(payload);
    }
}

/// <summary>Big-endian writer for DNS wire format.</summary>
internal sealed class DnsWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(512);

    public void WriteByte(byte value)
    {
        Span<byte> span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteUInt16(ushort value)
    {
        Span<byte> span = _buffer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        _buffer.Advance(2);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer.GetSpan(value.Length));
        _buffer.Advance(value.Length);
    }

    /// <summary>Writes labels with length prefixes; no compression, which is legal for a responder.</summary>
    public void WriteName(DnsName name)
    {
        foreach (string label in name.Labels)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            WriteByte((byte)bytes.Length);
            WriteBytes(bytes);
        }

        WriteByte(0);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}

/// <summary>Reader for DNS wire format, including compression pointers.</summary>
internal ref struct DnsReader
{
    private const int MaxPointerJumps = 64;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public DnsReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _data[_position++];
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureAvailable(count);
        ReadOnlySpan<byte> span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    /// <summary>Reads a possibly compressed name.</summary>
    public DnsName ReadName()
    {
        var labels = new List<string>();

        int position = _position;
        bool jumped = false;
        int jumps = 0;

        while (true)
        {
            if (position >= _data.Length)
            {
                throw new FormatException("DNS name runs past the end of the message.");
            }

            byte length = _data[position];

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= _data.Length)
                {
                    throw new FormatException("Truncated DNS compression pointer.");
                }

                int target = ((length & 0x3F) << 8) | _data[position + 1];
                if (!jumped)
                {
                    _position = position + 2;
                }

                jumped = true;
                if (++jumps > MaxPointerJumps || target >= _data.Length)
                {
                    throw new FormatException("DNS compression pointer loop.");
                }

                position = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new FormatException("Unsupported DNS label type.");
            }

            position++;
            if (length == 0)
            {
                if (!jumped)
                {
                    _position = position;
                }

                break;
            }

            if (position + length > _data.Length)
            {
                throw new FormatException("Truncated DNS label.");
            }

            labels.Add(Encoding.UTF8.GetString(_data.Slice(position, length)));
            position += length;
        }

        return new DnsName(labels);
    }

    /// <summary>Reads one resource record, skipping types this implementation does not understand.</summary>
    public MdnsRecord? ReadRecord()
    {
        DnsName name = ReadName();
        var type = (DnsRecordType)ReadUInt16();
        ushort cls = ReadUInt16();
        uint ttl = ReadUInt32();
        int length = ReadUInt16();
        bool cacheFlush = (cls & DnsClass.CacheFlush) != 0;

        EnsureAvailable(length);
        int end = _position + length;

        MdnsRecord? record = type switch
        {
            DnsRecordType.Ptr => new PtrRecord(name, ReadName(), ttl),
            DnsRecordType.Srv => ReadSrv(name, ttl, cacheFlush, end),
            DnsRecordType.Txt => new TxtRecord(name, ReadTxtEntries(length), ttl, cacheFlush),
            DnsRecordType.A => new AddressRecord(name, ReadAddress(4), ttl, cacheFlush),
            DnsRecordType.Aaaa => new AddressRecord(name, ReadAddress(16), ttl, cacheFlush),
            _ => null,
        };

        // Always honour rdata length, even when a parser above consumed a different amount.
        _position = end;
        return record;
    }

    private SrvRecord ReadSrv(DnsName name, uint ttl, bool cacheFlush, int end)
    {
        ushort priority = ReadUInt16();
        ushort weight = ReadUInt16();
        ushort port = ReadUInt16();
        DnsName target = ReadName();

        if (_position > end)
        {
            throw new FormatException("SRV record rdata is too short.");
        }

        return new SrvRecord(name, port, target, priority, weight, ttl, cacheFlush);
    }

    private List<string> ReadTxtEntries(int length)
    {
        var entries = new List<string>();
        int end = _position + length;

        while (_position < end)
        {
            int entryLength = ReadByte();
            if (_position + entryLength > end)
            {
                throw new FormatException("TXT string runs past its record.");
            }

            entries.Add(Encoding.UTF8.GetString(ReadBytes(entryLength)));
        }

        return entries;
    }

    private IPAddress ReadAddress(int size)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(size);
        return new IPAddress(bytes);
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new FormatException("DNS message is truncated.");
        }
    }
}
