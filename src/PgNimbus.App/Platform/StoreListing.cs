using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PgNimbus.App.Platform;

/// <summary>
/// The Microsoft Store listing, and whether this process came from it.
///
/// <para>The rating entry (palette row, ☰ menu item, About link) is shown only
/// when the process has a package identity, which for pgNimbus means the Store
/// MSIX: the self-signed MSIX CI builds is never published, and the zip, the
/// installers and the other platforms have no identity at all. A link to rate
/// an app the user did not get from the Store would be a dead end.</para>
///
/// <para>It is a passive entry on purpose: no prompt, no counter, no
/// telemetry (UI design rule 1). Detection is one
/// <c>GetCurrentPackageFullName</c> call, which needs no WinRT projection and
/// is a blittable P/Invoke, so it is safe under NativeAOT.</para>
/// </summary>
public static class StoreListing
{
    /// <summary>pgNimbus's Store product id (Partner Center).</summary>
    public const string ProductId = "9N6SZT42XJ24";

    /// <summary>Opens the Store app on the listing's rate-and-review page.</summary>
    public const string ReviewUri = "ms-windows-store://review/?ProductId=" + ProductId;

    private static readonly Lazy<bool> Packaged = new(DetectPackaged);

    /// <summary>Whether this process runs with a package identity (the Store MSIX). Read once.</summary>
    public static bool IsPackaged => Packaged.Value;

    /// <summary>Hands <see cref="ReviewUri"/> to the shell; the Store app answers it.</summary>
    public static void OpenReviewPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReviewUri) { UseShellExecute = true });
        }
        catch
        {
            // No Store app to hand off to is not worth an error for a menu item.
        }
    }

    private static bool DetectPackaged()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return false;
        }

        try
        {
            // With a zero-length buffer an unpackaged process gets
            // APPMODEL_ERROR_NO_PACKAGE and a packaged one ERROR_INSUFFICIENT_BUFFER
            // (with the length it would need).
            uint length = 0;
            return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
