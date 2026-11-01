#if FEIGE_TEST_ISOLATION
using Feige.App.Services;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Storage;

namespace Feige.App.Tests;

public sealed class AppSettingsIsolationTests
{
    [Fact]
    public void TestBuildRequiresAndUsesExplicitDataDirectory()
    {
        string? original = Environment.GetEnvironmentVariable("FEIGE_TEST_DATA_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("FEIGE_TEST_DATA_DIRECTORY", null);
            Assert.Throws<InvalidOperationException>(() => _ = AppSettings.AppDataFolder);

            string expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "isolated-appdata-test"));
            Environment.SetEnvironmentVariable("FEIGE_TEST_DATA_DIRECTORY", expected);
            Assert.Equal(expected, AppSettings.AppDataFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEIGE_TEST_DATA_DIRECTORY", original);
        }
    }

    [Fact]
    public void ProvisionIsolatedWindowsUiProfileWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("FEIGE_TEST_PROVISION_UI_PROFILE") != "1") return;

        string root = AppSettings.AppDataFolder;
        Directory.CreateDirectory(root);
        string receiveFolder = Path.Combine(root, "Received");
        Directory.CreateDirectory(receiveFolder);
        using IdentityKey identity = new IdentityStore().LoadOrCreate();
        new LocalPreferencesFile(root).Save(new LocalPreferences("Feige Windows Test VM", receiveFolder,
            new SetupProgress(Step: 2, Ended: true), AutoCheckUpdates: false));
        File.WriteAllText(Path.Combine(root, "ui-test-fingerprint.txt"), identity.Fingerprint);

        string? macFingerprint = Environment.GetEnvironmentVariable("FEIGE_TEST_MAC_FINGERPRINT");
        if (!string.IsNullOrWhiteSpace(macFingerprint))
        {
            Guid groupID = Guid.TryParse(Environment.GetEnvironmentVariable("FEIGE_TEST_GROUP_ID"), out Guid configured)
                ? configured : Guid.Parse("11111111-1111-4111-8111-111111111111");
            var group = new GroupConversation(groupID, "LAN provisioning acceptance", identity.Fingerprint, 1,
                [new(identity.Fingerprint, "Windows runtime"), new(macFingerprint, "macOS runtime")]);
            new V4GroupStore(Path.Combine(root, "v4-groups.json")).AddGroup(group);
        }
    }
}
#endif
