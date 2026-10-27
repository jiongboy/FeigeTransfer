namespace Feige.Core.Discovery;

/// <summary>
/// A DNS name as a sequence of labels (no trailing dot), which is how mDNS carries service types and instance names.
/// </summary>
/// <remarks>
/// Labels are kept separate instead of storing one dotted string: DNS-SD instance names are arbitrary UTF-8 that may
/// itself contain dots and spaces, so splitting a string would be lossy. Comparison is case-insensitive per RFC 1035
/// and per 通信协议 §2 (UUID-style values compare case-insensitively).
/// </remarks>
public sealed class DnsName : IEquatable<DnsName>
{
    private readonly string[] _labels;
    private readonly string _display;

    /// <summary>Creates a name from labels, outermost last: <c>["Mac-A", "_feige", "_tcp", "local"]</c>.</summary>
    /// <exception cref="ArgumentException">A label is empty, too long, or the whole name exceeds 255 bytes.</exception>
    public DnsName(params string[] labels)
        : this((IEnumerable<string>)labels)
    {
    }

    /// <summary>Creates a name from labels, outermost last.</summary>
    public DnsName(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _labels = labels.ToArray();

        int total = 1;
        foreach (string label in _labels)
        {
            if (string.IsNullOrEmpty(label))
            {
                throw new ArgumentException("DNS labels must not be empty.", nameof(labels));
            }

            int length = System.Text.Encoding.UTF8.GetByteCount(label);
            if (length > 63)
            {
                throw new ArgumentException("A DNS label must be at most 63 bytes.", nameof(labels));
            }

            total += length + 1;
        }

        if (total > 255)
        {
            throw new ArgumentException("A DNS name must be at most 255 bytes on the wire.", nameof(labels));
        }

        _display = string.Join('.', _labels);
    }

    /// <summary>The labels, outermost last.</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>The leftmost label — the service instance name for <c>&lt;instance&gt;._feige._tcp.local</c>.</summary>
    public string FirstLabel => _labels.Length > 0 ? _labels[0] : string.Empty;

    /// <summary>True when this name is not the DNS root.</summary>
    public bool IsEmpty => _labels.Length == 0;

    /// <summary>Prepends a label, used to build an instance name from a service type.</summary>
    public DnsName WithPrefix(string label) => new([label, .. _labels]);

    /// <summary>The dotted display form; not round-trippable when a label contains a dot.</summary>
    public override string ToString() => _display;

    /// <inheritdoc />
    public bool Equals(DnsName? other)
    {
        if (other is null || other._labels.Length != _labels.Length)
        {
            return false;
        }

        for (int i = 0; i < _labels.Length; i++)
        {
            if (!string.Equals(_labels[i], other._labels[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DnsName);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string label in _labels)
        {
            hash.Add(label, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    /// <summary>Case-insensitive equality.</summary>
    public static bool operator ==(DnsName? left, DnsName? right) => Equals(left, right);

    /// <summary>Case-insensitive inequality.</summary>
    public static bool operator !=(DnsName? left, DnsName? right) => !Equals(left, right);
}
