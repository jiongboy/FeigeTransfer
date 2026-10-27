namespace Feige.Core.Net;

/// <summary>
/// Local policies for one TCP connection: how long to wait for a connect, for each handshake read/write, and for each
/// record read/write after the session is up.
/// </summary>
/// <remarks>
/// <para>
/// 通信协议 §2 lists these as suggestions and states explicitly that they are local policy, not wire format — a peer
/// using different values still interoperates. Values are per call site and never negotiated.
/// </para>
/// <para>
/// All three are <b>idle</b> timeouts, never total deadlines: a long transfer is fine as long as records keep arriving.
/// The one wait that is deliberately unbounded here is the local user confirmation prompt — it happens between the last
/// handshake read and key derivation, so no network timeout can expire while it is open. Bounding that window
/// (the suggested 5 minutes) belongs to the UI that owns the dialog.
/// </para>
/// </remarks>
public sealed record ConnectionOptions
{
    /// <summary>Default policies: 5 s connect, 15 s per handshake read/write, 15 s per record read/write.</summary>
    public static ConnectionOptions Default { get; } = new();

    /// <summary>TCP connect timeout. Keep short: a LAN peer that does not answer in seconds is not going to.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-read and per-write timeout while the four handshake messages are exchanged. A handshake frame is at most
    /// 16 KiB, so a peer that sends slower than this is stalled rather than large.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-read and per-write timeout once the session is established (idle timeout, not total transfer time).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Throws when any policy is not a positive, sane duration.</summary>
    public void Validate()
    {
        RequirePositive(ConnectTimeout, nameof(ConnectTimeout));
        RequirePositive(HandshakeTimeout, nameof(HandshakeTimeout));
        RequirePositive(IdleTimeout, nameof(IdleTimeout));
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        // Upper bound keeps CancelAfter from throwing on absurd values and flags a unit mix-up early.
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(name, value, "Timeout must be positive and at most one hour.");
        }
    }
}
