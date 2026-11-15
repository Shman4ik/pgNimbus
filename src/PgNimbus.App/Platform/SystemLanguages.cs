using System.Runtime.InteropServices;

namespace PgNimbus.App.Platform;

/// <summary>
/// The languages the user set the operating system to, best first, as BCP 47 tags.
/// <para>
/// Not <c>CultureInfo.CurrentUICulture</c>: the App runs with
/// <c>InvariantGlobalization</c>, so that is the invariant culture on every machine.
/// Each platform is asked directly instead: Windows' preferred UI languages, the
/// macOS language list (System Settings ▸ Language &amp; Region, the per-app choice
/// included), and the POSIX locale variables on Linux. Every call here is blittable
/// (NativeAOT-safe) and any failure is an empty list, which means English.
/// </para>
/// </summary>
internal static class SystemLanguages
{
    public static IReadOnlyList<string> Preferred()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Windows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return Mac();
            }

            return Posix(Environment.GetEnvironmentVariable);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    /// <summary>
    /// The POSIX order: <c>LANGUAGE</c> (a colon list, honoured only when a locale is
    /// set at all, as gettext does), then the first of <c>LC_ALL</c>,
    /// <c>LC_MESSAGES</c>, <c>LANG</c>. <c>C</c> and <c>POSIX</c> mean no preference.
    /// </summary>
    internal static IReadOnlyList<string> Posix(Func<string, string?> env)
    {
        var locale = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }
            .Select(env)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (locale is null || locale is "C" or "POSIX" || locale.StartsWith("C.", StringComparison.Ordinal))
        {
            return [];
        }

        var list = new List<string>();
        if (env("LANGUAGE") is { Length: > 0 } language)
        {
            list.AddRange(language.Split(':', StringSplitOptions.RemoveEmptyEntries));
        }

        list.Add(locale);
        return list;
    }

    private static List<string> Windows()
    {
        const uint MuiLanguageName = 0x8;
        uint size = 0;
        if (!GetUserPreferredUILanguages(MuiLanguageName, out _, IntPtr.Zero, ref size) || size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            if (!GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref size))
            {
                return [];
            }

            // A double-NUL-terminated list of NUL-terminated names.
            var text = Marshal.PtrToStringUni(buffer, (int)size) ?? "";
            return [.. text.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> Mac()
    {
        var list = new List<string>();
        var array = CFLocaleCopyPreferredLanguages();
        if (array == IntPtr.Zero)
        {
            return list;
        }

        try
        {
            var count = CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                var str = CFArrayGetValueAtIndex(array, i);
                var length = CFStringGetLength(str);
                if (length <= 0 || length > 256)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
                try
                {
                    CFStringGetCharacters(str, new CFRange(0, length), buffer);
                    if (Marshal.PtrToStringUni(buffer, (int)length) is { } tag)
                    {
                        list.Add(tag);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            CFRelease(array);
        }

        return list;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, IntPtr buffer, ref uint size);

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRange(nint Location, nint Length);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFLocaleCopyPreferredLanguages();

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr str);

    [DllImport(CoreFoundation)]
    private static extern void CFStringGetCharacters(IntPtr str, CFRange range, IntPtr buffer);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr obj);
}
