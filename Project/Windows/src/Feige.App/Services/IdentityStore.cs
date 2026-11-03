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

    private const string BackupKeyPath = @"Software\Songli\FeigeTransfer";
    private const string BackupValueName = "IdentityBackup";

    private readonly string _path;
    private readonly string? _backupKeyPath;

    /// <summary>Creates a store under the packaged app's local folder.</summary>
    /// <remarks>
    /// The protected blob is also kept under HKCU, so deleting the data folder or reinstalling does not show this PC to
    /// peers as a new device. Isolated test builds never touch the real backup.
    /// </remarks>
    public IdentityStore()
#if FEIGE_TEST_ISOLATION
        : this(Path.Combine(AppSettings.AppDataFolder, "identity.pkcs8.dpapi"), null)
#else
        : this(Path.Combine(AppSettings.AppDataFolder, "identity.pkcs8.dpapi"), BackupKeyPath)
#endif
    {
    }

    /// <summary>Creates a store at <paramref name="path"/>, backed up under HKCU\<paramref name="backupKeyPath"/> when given.</summary>
    public IdentityStore(string path, string? backupKeyPath)
    {
        _path = path;
        _backupKeyPath = backupKeyPath;
    }

    /// <summary>Path of the protected key file, for diagnostics.</summary>
    public string ProtectedKeyPath => _path;

    /// <summary>Returns the stored identity, creating and persisting one on first run.</summary>
    public IdentityKey LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            byte[] protectedBlob = File.ReadAllBytes(_path);
            IdentityKey identity = Unprotect(protectedBlob);
            // The file is authoritative: installs that predate the backup get one, and a stale backup is replaced.
            if (ReadBackup() is not { } existing || !existing.AsSpan().SequenceEqual(protectedBlob))
            {
                try { WriteBackup(protectedBlob); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            return identity;
        }

        if (ReadBackup() is { } backup)
        {
            IdentityKey restored = Unprotect(backup);
            WriteKeyFile(backup);
            return restored;
        }

        using IdentityKey created = IdentityKey.Create();
        byte[] exported = created.ExportPkcs8();

        try
        {
            byte[] protectedBlob = ProtectedData.Protect(exported, Entropy, DataProtectionScope.CurrentUser);
            WriteKeyFile(protectedBlob);
            WriteBackup(protectedBlob);

            // Re-import from the export so the returned key is independent of the disposed instance.
            return IdentityKey.FromPkcs8(exported);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    private static IdentityKey Unprotect(byte[] protectedBlob)
    {
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

    private void WriteKeyFile(byte[] protectedBlob)
    {
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
    }

    private byte[]? ReadBackup()
    {
        if (_backupKeyPath is null) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_backupKeyPath);
        return key?.GetValue(BackupValueName) as byte[] is { Length: > 0 } value ? value : null;
    }

    private void WriteBackup(byte[] protectedBlob)
    {
        if (_backupKeyPath is null) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_backupKeyPath);
        key.SetValue(BackupValueName, protectedBlob, Microsoft.Win32.RegistryValueKind.Binary);
    }
}
