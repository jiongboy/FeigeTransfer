using Feige.App.Services;
using Microsoft.Win32;

namespace Feige.App.Tests;

public sealed class IdentityStoreBackupTests
{
    [Fact]
    public void DeletedDataFolderRestoresSameIdentityFromRegistryBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "feige-identity-backup-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "identity.pkcs8.dpapi");
        string backupKey = @"Software\Songli\FeigeTransferTests\" + Guid.NewGuid().ToString("N");
        try
        {
            string original;
            using (var identity = new IdentityStore(path, backupKey).LoadOrCreate()) original = identity.Fingerprint;

            Directory.Delete(root, recursive: true);
            using (var restored = new IdentityStore(path, backupKey).LoadOrCreate())
                Assert.Equal(original, restored.Fingerprint);
            Assert.True(File.Exists(path));

            // An install from before the backup existed gains one on its next launch.
            Registry.CurrentUser.DeleteSubKeyTree(backupKey);
            using (var reloaded = new IdentityStore(path, backupKey).LoadOrCreate())
                Assert.Equal(original, reloaded.Fingerprint);
            using var key = Registry.CurrentUser.OpenSubKey(backupKey);
            Assert.NotNull(key?.GetValue("IdentityBackup"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(backupKey, throwOnMissingSubKey: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
