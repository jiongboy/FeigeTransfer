using System.Security.Cryptography;
using Feige.Core.Protocol;

namespace Feige.Core.Crypto;

/// <summary>
/// Device identity fingerprint as defined in 通信协议 §3.6:
/// <c>SHA-256(SPKI DER)</c> rendered as uppercase hex in groups of four characters separated by one space
/// (16 groups), e.g. <c>6893 A08C 9C4C …</c>.
/// </summary>
/// <remarks>
/// Comparison must be whitespace- and case-insensitive, because the value is read aloud or typed by a human;
/// <see cref="Matches"/> does that normalization. A fingerprint identifies a key, but only a user comparing it
/// out-of-band or the SAS check stops a man-in-the-middle.
/// </remarks>
public static class DeviceFingerprint
{
    /// <summary>Length of the SHA-256 digest behind a fingerprint.</summary>
    public const int DigestSize = 32;

    /// <summary>Number of hex characters in a formatted fingerprint.</summary>
    public const int HexLength = DigestSize * 2;

    /// <summary>Formats <paramref name="spkiDer"/> as <c>XXXX XXXX …</c> (16 groups, uppercase).</summary>
    public static string Compute(ReadOnlySpan<byte> spkiDer)
    {
        if (spkiDer.IsEmpty)
        {
            throw new ArgumentException("SPKI DER must not be empty.", nameof(spkiDer));
        }

        Span<byte> digest = stackalloc byte[DigestSize];
        SHA256.HashData(spkiDer, digest);
        return Format(digest);
    }

    /// <summary>Formats an already-computed 32-byte digest into the display form.</summary>
    public static string Format(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != DigestSize)
        {
            throw new ArgumentException($"Fingerprint digest must be {DigestSize} bytes, got {digest.Length}.", nameof(digest));
        }

        string hex = Convert.ToHexString(digest); // already uppercase
        var builder = new System.Text.StringBuilder(HexLength + 15);
        for (int i = 0; i < hex.Length; i += 4)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(hex, i, 4);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes user input for comparison: strips whitespace, uppercases hex digits.
    /// Returns an empty string when the input contains anything other than hex digits and spaces,
    /// which can never equal a real fingerprint.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(HexLength);
        foreach (char c in value)
        {
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            if (Uri.IsHexDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
                continue;
            }

            return string.Empty;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compares two fingerprints ignoring spaces and case; empty input never matches.
    /// </summary>
    public static bool Matches(string? left, string? right)
    {
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Length == HexLength && b.Length == HexLength && string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Throws <see cref="ProtocolError.FingerprintMismatch"/> unless <paramref name="spkiDer"/> corresponds to
    /// <paramref name="expected"/>.
    /// </summary>
    public static void Verify(ReadOnlySpan<byte> spkiDer, string? expected)
    {
        if (!Matches(Compute(spkiDer), expected))
        {
            throw new ProtocolException(ProtocolError.FingerprintMismatch);
        }
    }
}
