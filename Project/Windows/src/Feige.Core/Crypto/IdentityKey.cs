using System.Security.Cryptography;
using Feige.Core.Protocol;

namespace Feige.Core.Crypto;

/// <summary>
/// Long-term RSA-3072 device identity used to sign the handshake transcript (通信协议 §3.6).
/// </summary>
/// <remarks>
/// <para>
/// Signatures are RSASSA-PSS with SHA-256/MGF1-SHA256 and a <b>32-byte salt</b>, which is what .NET's
/// <see cref="RSASignaturePadding.Pss"/> uses by default (salt length equals digest length) and what macOS
/// <c>rsaSignatureMessagePSSSHA256</c> produces. The signed message is the raw transcript from §3.5, not its digest.
/// </para>
/// <para>
/// The public key travels as SubjectPublicKeyInfo DER. PKCS#1 <c>RSAPublicKey</c> must never be used: fingerprints
/// are computed over these exact bytes, so an encoding mismatch makes two devices disagree about identity.
/// </para>
/// </remarks>
public sealed class IdentityKey : IDisposable
{
    /// <summary>Required modulus size of an identity key.</summary>
    public const int KeySizeInBits = 3072;

    /// <summary>Expected signature length in bytes for a 3072-bit key.</summary>
    public const int SignatureSize = KeySizeInBits / 8;

    private readonly RSA _rsa;
    private readonly byte[] _subjectPublicKeyInfo;
    private string? _fingerprint;

    private IdentityKey(RSA rsa)
    {
        _rsa = rsa;
        _subjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>SPKI DER, the exact bytes that go into <c>identityPublicKeyDER</c> and into the fingerprint.</summary>
    public ReadOnlySpan<byte> SubjectPublicKeyInfo => _subjectPublicKeyInfo;

    /// <summary>Formatted fingerprint (<c>XXXX XXXX …</c>, §3.6); computed once.</summary>
    public string Fingerprint => _fingerprint ??= DeviceFingerprint.Compute(_subjectPublicKeyInfo);

    /// <summary>Generates a fresh RSA-3072 identity. Do this once per installation and persist the result.</summary>
    public static IdentityKey Create()
    {
        RSA rsa = RSA.Create(KeySizeInBits);
        try
        {
            return new IdentityKey(rsa);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Restores an identity from PKCS#8 DER. The caller is responsible for protecting those bytes at rest —
    /// on Windows that means DPAPI (<c>ProtectedData</c>) or the MSIX package vault; this class never writes to disk.
    /// </summary>
    public static IdentityKey FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(pkcs8, out int read);
            if (read != pkcs8.Length)
            {
                throw new ProtocolException(ProtocolError.InvalidOffer);
            }

            EnsureKeySize(rsa);
            return new IdentityKey(rsa);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>Exports the private key as PKCS#8 DER for persistence. Never log or transmit the result.</summary>
    public byte[] ExportPkcs8() => _rsa.ExportPkcs8PrivateKey();

    /// <summary>Signs the raw transcript bytes with RSASSA-PSS(SHA-256, salt 32).</summary>
    public byte[] Sign(ReadOnlySpan<byte> transcript)
        => _rsa.SignData(transcript, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="transcript"/> using the SPKI DER carried in an offer.
    /// </summary>
    /// <remarks>
    /// Returns false for both a bad signature and an unusable key; callers turn that into
    /// <see cref="ProtocolError.SignatureFailed"/>. The key size is enforced so a peer cannot downgrade the
    /// identity strength silently.
    /// </remarks>
    public static bool Verify(ReadOnlySpan<byte> subjectPublicKeyInfo, ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> signature)
    {
        if (subjectPublicKeyInfo.IsEmpty || signature.IsEmpty)
        {
            return false;
        }

        RSA? rsa = null;
        try
        {
            rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int read);
            if (read != subjectPublicKeyInfo.Length || rsa.KeySize != KeySizeInBits)
            {
                return false;
            }

            return rsa.VerifyData(transcript, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            rsa?.Dispose();
        }
    }

    /// <summary>
    /// Verifies a peer signature and throws <see cref="ProtocolError.SignatureFailed"/> on failure.
    /// </summary>
    public static void VerifyOrThrow(ReadOnlySpan<byte> subjectPublicKeyInfo, ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> signature)
    {
        if (!Verify(subjectPublicKeyInfo, transcript, signature))
        {
            throw new ProtocolException(ProtocolError.SignatureFailed);
        }
    }

    private static void EnsureKeySize(RSA rsa)
    {
        if (rsa.KeySize != KeySizeInBits)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();
}
