using System.Runtime.InteropServices;

namespace Feige.App.Services;

/// <summary>Package identity is optional in the EXE installation, not inferred from an exception while loading user data.</summary>
public static class AppEnvironment
{
    public static bool IsPackaged { get; } = HasPackageIdentity();

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return result != 15700; // APPMODEL_ERROR_NO_PACKAGE; packaged calls report ERROR_INSUFFICIENT_BUFFER.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
