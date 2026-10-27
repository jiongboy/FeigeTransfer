using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace Feige.Core.Crypto;

/// <summary>
/// X25519 key agreement (RFC 7748), required by 通信协议 §3.9.
/// <para>
/// .NET 10 has no built-in X25519 and Windows CNG does not implement Curve25519, so BouncyCastle is the
/// only external crypto dependency of <c>Feige.Core</c>. This type is the single place that touches it:
/// everything else in the protocol (RSA-PSS, HKDF, AES-GCM, SHA-256) uses .NET built-in types.
/// </para>
/// </summary>
public static class X25519KeyAgreement
{
    /// <summary>Length of an X25519 private key, public key and shared secret, all in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>
    /// Generates a fresh ephemeral private key from the platform CSPRNG.
    /// Per §3.9 every connection must use a new key pair; never reuse the result.
    /// </summary>
    public static byte[] CreatePrivateKey()
    {
        byte[] key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>Derives the 32-byte public key that goes into <c>ephemeralPublicKey</c>.</summary>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        ValidateSize(privateKey, nameof(privateKey));
        byte[] key = privateKey.ToArray();
        return new X25519PrivateKeyParameters(key, 0).GeneratePublicKey().GetEncoded();
    }

    /// <summary>
    /// Computes the 32-byte shared secret between a local private key and the peer's <c>ephemeralPublicKey</c>.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The peer key is small order, so the result is all zeros. §3.9 requires treating that as a failure.
    /// </exception>
    public static byte[] Agree(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        ValidateSize(privateKey, nameof(privateKey));
        ValidateSize(peerPublicKey, nameof(peerPublicKey));

        byte[] privateKeyBytes = privateKey.ToArray();
        byte[] peerPublicKeyBytes = peerPublicKey.ToArray();

        var agreement = new X25519Agreement();
        agreement.Init(new X25519PrivateKeyParameters(privateKeyBytes, 0));

        byte[] shared = new byte[agreement.AgreementSize];
        try
        {
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKeyBytes, 0), shared, 0);
        }
        catch (InvalidOperationException ex)
        {
            // BouncyCastle reports the RFC 7748 all-zero check as InvalidOperationException.
            throw new CryptographicException("X25519 agreement produced an all-zero shared secret (small-order peer key).", ex);
        }

        if (IsAllZero(shared))
        {
            throw new CryptographicException("X25519 agreement produced an all-zero shared secret (small-order peer key).");
        }

        return shared;
    }

    /// <summary>Returns true when every byte is zero; used for the RFC 7748 Contributory Behaviour check.</summary>
    internal static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        int accumulator = 0;
        foreach (byte b in value)
        {
            accumulator |= b;
        }

        return accumulator == 0;
    }

    private static void ValidateSize(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Length != KeySize)
        {
            throw new ArgumentException($"X25519 keys must be exactly {KeySize} bytes, got {value.Length}.", parameterName);
        }
    }
}
