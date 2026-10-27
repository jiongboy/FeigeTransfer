using System.Text;
using Feige.Core.Crypto;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// What this device presents in an offer: its long-term identity and the name the peer will display.
/// </summary>
/// <remarks>
/// Both values must satisfy 通信协议 §3.3, so construction validates them once instead of failing deep inside a
/// handshake. The identity is not owned by this object; dispose it separately.
/// </remarks>
public sealed class LocalDevice
{
    /// <summary>Wraps an identity and a display name.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidOffer"/> when the name is empty or longer than 128 UTF-8 bytes.
    /// </exception>
    public LocalDevice(IdentityKey identity, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(identity);

        string name = deviceName?.Trim() ?? string.Empty;
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > HandshakeOffer.MaxDeviceNameBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        Identity = identity;
        DeviceName = name;
    }

    /// <summary>RSA-3072 identity used to sign the transcript and to derive this device's fingerprint.</summary>
    public IdentityKey Identity { get; }

    /// <summary>Name shown to the peer and confirmed alongside the SAS.</summary>
    public string DeviceName { get; }

    /// <summary>Fingerprint of <see cref="Identity"/>, for display and for trusting known peers.</summary>
    public string Fingerprint => Identity.Fingerprint;

    /// <summary>
    /// The machine name, trimmed to the 128-byte protocol limit — a reasonable default the settings UI can override.
    /// </summary>
    public static string SuggestDeviceName()
    {
        string name = Environment.MachineName.Trim();
        if (name.Length == 0)
        {
            return "Windows";
        }

        // Only trim when the name really is oversized, and never split a UTF-8 sequence.
        while (Encoding.UTF8.GetByteCount(name) > HandshakeOffer.MaxDeviceNameBytes && name.Length > 1)
        {
            name = name[..^1];
        }

        return name;
    }
}
