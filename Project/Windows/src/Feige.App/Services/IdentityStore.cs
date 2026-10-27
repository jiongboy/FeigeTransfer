using System.Security.Cryptography;
using Feige.Core.Crypto;

namespace Feige.App.Services;

/// <summary>
/// Loads the RSA-3072 identity once per installation and keeps the private key encrypted with DPAPI.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint is what the peer sees, so the key must survive restarts — regenerating it would silently break every
/// "same device as last time" check. The PKCS#8 blob is protected with <see cref="DataProtectionScope.CurrentUser"/>,
/// which ties it to the Windows account: copying the file to another machine or user yields nothing.
/// </para>
/// <para>
/// Generating a 3072-bit key takes a second, so callers should not do this on the UI thread.
/// </para>
/// </remarks>
public sealed class IdentityStore
{
    private static readonly byte[] Entropy = "Feige.Identity.v1"u8.ToArray();

    private readonly string _path;

    /// <summary>Creates a store under the packaged app's local folder.</summary>
    public IdentityStore()
    {
        _path = Path.Combine(AppSettings.AppDataFolder, "identity.pkcs8.dpapi");
    }

    /// <summary>Path of the protected key file, for diagnostics.</summary>
    public string ProtectedKeyPath => _path;

    /// <summary>Returns the stored identity, creating and persisting one on first run.</summary>
    public IdentityKey LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            byte[] protectedBlob = File.ReadAllBytes(_path);
            byte[] pkcs8 = ProtectedData.Unprotect(protectedBlob, Entropy, DataProtectionScope.CurrentUser);

            try
            {
                return IdentityKey.FromPkcs8(pkcs8);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }

        using IdentityKey created = IdentityKey.Create();
        byte[] exported = created.ExportPkcs8();

        try
        {
            byte[] protectedBlob = ProtectedData.Protect(exported, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(protectedBlob);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }

            // Re-import from the export so the returned key is independent of the disposed instance.
            return IdentityKey.FromPkcs8(exported);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }
}
