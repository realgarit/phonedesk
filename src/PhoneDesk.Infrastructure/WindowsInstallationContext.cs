using System.Runtime.InteropServices;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

/// <summary>
/// Detects MSIX package identity through GetCurrentPackageFullName. Non-Windows
/// platforms and classic (Inno Setup / zip) installs report <c>false</c>.
/// </summary>
public sealed class WindowsInstallationContext : IInstallationContext
{
    private const int AppModelErrorNoPackage = 15700;

    private readonly Lazy<bool> _isPackaged = new(DetectPackageIdentity);

    public bool IsPackaged => _isPackaged.Value;

    private static bool DetectPackageIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return false;
        }

        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            // ERROR_INSUFFICIENT_BUFFER (122) means identity exists and a buffer is needed.
            return result != AppModelErrorNoPackage && length > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
