namespace Feige.Core.Crypto;

/// <summary>
/// Directional session material derived in 通信协议 §3.9.
/// Both keys are 32 bytes; both nonce prefixes are 8 bytes (<c>digest[0..4]</c> + <c>"INIT"</c>/<c>"RESP"</c>).
/// </summary>
/// <remarks>
/// Instances are per-connection. Keys must never be reused across connections, logged, or persisted.
/// </remarks>
public sealed class SessionKeys
{
    internal SessionKeys(byte[] sendKey, byte[] receiveKey, byte[] sendNoncePrefix, byte[] receiveNoncePrefix)
    {
        SendKey = sendKey;
        ReceiveKey = receiveKey;
        SendNoncePrefix = sendNoncePrefix;
        ReceiveNoncePrefix = receiveNoncePrefix;
    }

    /// <summary>AES-256-GCM key for records this side sends.</summary>
    public byte[] SendKey { get; }

    /// <summary>AES-256-GCM key for records this side receives.</summary>
    public byte[] ReceiveKey { get; }

    /// <summary>8-byte nonce prefix used when sending.</summary>
    public byte[] SendNoncePrefix { get; }

    /// <summary>8-byte nonce prefix used when receiving.</summary>
    public byte[] ReceiveNoncePrefix { get; }
}
