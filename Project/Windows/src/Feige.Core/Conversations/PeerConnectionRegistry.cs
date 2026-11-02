using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Feige.Core.Conversations;

public enum ConnectionRegistrationDisposition { Added, ExistingKept, Replaced }

public sealed record ConnectionRegistrationResult<T>(ConnectionRegistrationDisposition Disposition, T Active, T? Superseded)
    where T : class;

/// <summary>
/// Thread-safe registry with one canonical authenticated channel per peer identity and the v4 topology limits.
/// Simultaneous dials converge on the same channel using initiator identity and shared transcript digest ordering.
/// </summary>
public sealed class PeerConnectionRegistry<T> where T : class
{
    public const int MaximumDirectPeers = 20;
    public const int MaximumGroupOnlyPeers = 19;
    public const int MaximumConnections = MaximumDirectPeers + MaximumGroupOnlyPeers;

    private sealed record Entry(T Connection, string InitiatorFingerprint, byte[] TranscriptDigest, bool IsDirect);
    private readonly object _gate = new();
    private readonly string _localFingerprint;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public PeerConnectionRegistry(string localFingerprint)
        => _localFingerprint = NormalizeFingerprint(localFingerprint);

    public int ConnectionCount { get { lock (_gate) return _entries.Count; } }
    public int DirectPeerCount { get { lock (_gate) return _entries.Values.Count(entry => entry.IsDirect); } }

    /// <summary>Registers an authenticated channel; replacement bypasses rank only for an approved reconnect.</summary>
    public ConnectionRegistrationResult<T> Register(T connection, string peerFingerprint, string initiatorFingerprint,
        ReadOnlySpan<byte> transcriptDigest, bool direct, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string peer = NormalizeFingerprint(peerFingerprint);
        string initiator = NormalizeFingerprint(initiatorFingerprint);
        if (peer == _localFingerprint || (initiator != peer && initiator != _localFingerprint) || transcriptDigest.Length != 32)
            throw new InvalidDataException("连接身份或握手摘要无效。");
        byte[] digest = transcriptDigest.ToArray();

        lock (_gate)
        {
            if (_entries.TryGetValue(peer, out Entry? current))
            {
                bool becameDirect = direct && !current.IsDirect;
                if (becameDirect && _entries.Values.Count(entry => entry.IsDirect) >= MaximumDirectPeers)
                    throw new InvalidOperationException("直连设备已达到 20 台上限。");
                if (!replaceExisting && CompareRank(peer, initiator, digest, current.InitiatorFingerprint, current.TranscriptDigest) >= 0)
                {
                    if (becameDirect) _entries[peer] = current with { IsDirect = true };
                    CryptographicOperations.ZeroMemory(digest);
                    return new(ConnectionRegistrationDisposition.ExistingKept, current.Connection, connection);
                }
                _entries[peer] = new(connection, initiator, digest, direct || current.IsDirect);
                CryptographicOperations.ZeroMemory(current.TranscriptDigest);
                return new(ConnectionRegistrationDisposition.Replaced, connection, current.Connection);
            }

            if (_entries.Count >= MaximumConnections)
            {
                CryptographicOperations.ZeroMemory(digest);
                throw new InvalidOperationException("连接数已达到 39 台上限。");
            }
            if (direct && _entries.Values.Count(entry => entry.IsDirect) >= MaximumDirectPeers)
            {
                CryptographicOperations.ZeroMemory(digest);
                throw new InvalidOperationException("直连设备已达到 20 台上限。");
            }
            if (!direct && _entries.Values.Count(entry => !entry.IsDirect) >= MaximumGroupOnlyPeers)
            {
                CryptographicOperations.ZeroMemory(digest);
                throw new InvalidOperationException("仅用于群组的设备连接已达到 19 台上限。");
            }
            _entries.Add(peer, new(connection, initiator, digest, direct));
            return new(ConnectionRegistrationDisposition.Added, connection, null);
        }
    }

    public bool TryGet(string peerFingerprint, out T? connection)
    {
        string peer = NormalizeFingerprint(peerFingerprint);
        lock (_gate)
        {
            if (_entries.TryGetValue(peer, out var entry)) { connection = entry.Connection; return true; }
            connection = null; return false;
        }
    }

    public bool IsDirect(string peerFingerprint)
    {
        string peer = NormalizeFingerprint(peerFingerprint);
        lock (_gate) return _entries.TryGetValue(peer, out var entry) && entry.IsDirect;
    }

    public bool Remove(string peerFingerprint, T expected)
    {
        string peer = NormalizeFingerprint(peerFingerprint);
        lock (_gate)
        {
            if (!_entries.TryGetValue(peer, out var entry) || !ReferenceEquals(entry.Connection, expected)) return false;
            _entries.Remove(peer);
            CryptographicOperations.ZeroMemory(entry.TranscriptDigest);
            return true;
        }
    }

    public IReadOnlyDictionary<string, T> Snapshot()
    {
        lock (_gate) return new ReadOnlyDictionary<string, T>(_entries.ToDictionary(pair => pair.Key, pair => pair.Value.Connection, StringComparer.Ordinal));
    }

    public static string NormalizeFingerprint(string value)
    {
        if (value is null) throw new InvalidDataException("连接身份无效。");
        string normalized = value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 64 || !normalized.All(char.IsAsciiHexDigit)) throw new InvalidDataException("连接身份无效。");
        return normalized;
    }

    private int CompareRank(string peer, string firstInitiator, ReadOnlySpan<byte> firstDigest, string secondInitiator, ReadOnlySpan<byte> secondDigest)
    {
        string preferredInitiator = string.CompareOrdinal(_localFingerprint, peer) < 0 ? _localFingerprint : peer;
        int firstDirection = firstInitiator == preferredInitiator ? 0 : 1;
        int secondDirection = secondInitiator == preferredInitiator ? 0 : 1;
        int directionOrder = firstDirection.CompareTo(secondDirection);
        return directionOrder != 0 ? directionOrder : firstDigest.SequenceCompareTo(secondDigest);
    }
}
