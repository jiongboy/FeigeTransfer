using Feige.Core.Profiles;

namespace Feige.Core.Conversations;

/// <summary>
/// Decides when a persistent contact may be dialed automatically. Keys are normalized fingerprints.
/// The peer with the larger fingerprint waits 10 seconds after discovery so both sides rarely dial at once;
/// a declined dial or a manual disconnect stops automatic dialing until the user connects manually.
/// </summary>
public sealed class TrustedDialPlanner
{
    public static readonly TimeSpan PeerDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private sealed class State
    {
        public int Failures;
        public DateTimeOffset NextAttempt = DateTimeOffset.MinValue;
        public bool Suppressed;
        public DateTimeOffset? FirstSeen;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string value in values ?? [])
            if (AvatarIds.NormalizeFingerprint(value) is { } fingerprint && seen.Add(fingerprint)) result.Add(fingerprint);
        return result.Count > 256 ? result[^256..] : result;
    }

    public bool IsSuppressed(string fingerprint) => _states.TryGetValue(fingerprint, out var state) && state.Suppressed;

    /// <summary>A manual connect or a successful connection clears failures; a successful one keeps a suppression.</summary>
    public void Reset(string fingerprint, bool clearSuppression)
    {
        if (clearSuppression || !IsSuppressed(fingerprint)) _states.Remove(fingerprint);
    }

    public void Suppress(string fingerprint) => Get(fingerprint).Suppressed = true;

    public void RecordFailure(string fingerprint, bool rejected, DateTimeOffset now)
    {
        State state = Get(fingerprint);
        if (rejected) { state.Suppressed = true; return; }
        state.Failures++;
        state.NextAttempt = now + Backoff[Math.Min(state.Failures, Backoff.Length) - 1];
    }

    /// <summary>Waiting after a disconnect keeps a peer that just closed the connection from being redialed at once.</summary>
    public void RecordDisconnect(string fingerprint, DateTimeOffset now)
    {
        State state = Get(fingerprint);
        if (!state.Suppressed && state.NextAttempt < now + Backoff[0]) state.NextAttempt = now + Backoff[0];
    }

    public void MarkUnseen(string fingerprint)
    {
        if (_states.TryGetValue(fingerprint, out var state)) state.FirstSeen = null;
    }

    /// <summary>Returns when the discovered peer may be dialed, or null while suppressed.</summary>
    public DateTimeOffset? DueAt(string fingerprint, string localFingerprint, DateTimeOffset now)
    {
        State state = Get(fingerprint);
        state.FirstSeen ??= now;
        if (state.Suppressed) return null;
        DateTimeOffset due = state.NextAttempt;
        if (string.CompareOrdinal(localFingerprint, fingerprint) > 0 && state.FirstSeen.Value + PeerDelay > due)
            due = state.FirstSeen.Value + PeerDelay;
        return due;
    }

    private State Get(string fingerprint)
    {
        if (!_states.TryGetValue(fingerprint, out var state)) _states[fingerprint] = state = new State();
        return state;
    }
}
