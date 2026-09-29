using Avalonia.Input;
using Avalonia.Input.Platform;

namespace PgNimbus.App.Platform;

/// <summary>
/// Puts a secret (today: a connection string with its password) on the
/// clipboard the way password managers do, rather than as plain text that
/// every clipboard monitor keeps (security audit 2026-09, finding 18).
///
/// <para>Two things. The text travels with the platform's "do not keep this"
/// markers: on Windows the formats clipboard history and cloud clipboard look
/// for (<c>ExcludeClipboardContentFromMonitorProcessing</c>, and
/// <c>CanIncludeInClipboardHistory</c>/<c>CanUploadToCloudClipboard</c> set
/// to a DWORD 0), on macOS the nspasteboard.org concealed/transient types that
/// clipboard managers honour, on Linux KDE's password-manager hint. They go
/// through Avalonia's own data transfer as platform formats, whose names reach
/// the OS unchanged, so no P/Invoke is needed. And the clipboard is cleared
/// after <see cref="ClearAfter"/> if it still holds that same text, so a
/// secret copied and forgotten does not wait there for the next paste.</para>
/// </summary>
public static class SecretClipboard
{
    /// <summary>How long a copied secret stays; settable so a test need not wait 30 s.</summary>
    public static TimeSpan ClearAfter { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly byte[] Dword0 = [0, 0, 0, 0];

    /// <summary>The platform formats set beside the text. Named here so a test can look for them.</summary>
    public static IReadOnlyList<(string Format, byte[] Value)> Markers { get; } =
        OperatingSystem.IsWindows()
            ?
            [
                ("ExcludeClipboardContentFromMonitorProcessing", Dword0),
                ("CanIncludeInClipboardHistory", Dword0),
                ("CanUploadToCloudClipboard", Dword0),
            ]
            : OperatingSystem.IsMacOS()
                ?
                [
                    // The convention reads the type's presence, not its data.
                    ("org.nspasteboard.ConcealedType", Dword0),
                    ("org.nspasteboard.TransientType", Dword0),
                ]
                : [("x-kde-passwordManagerHint", "secret"u8.ToArray())];

    /// <summary>
    /// Copies <paramref name="text"/> marked as a secret, then clears the
    /// clipboard after <see cref="ClearAfter"/> unless something else has been
    /// copied since. Returns once the text is on the clipboard; the clearing
    /// runs on its own.
    /// </summary>
    public static async Task SetAsync(IClipboard clipboard, string text)
    {
        var item = new DataTransferItem();
        item.SetText(text);
        foreach (var (format, value) in Markers)
        {
            item.Set(DataFormat.CreateBytesPlatformFormat(format), value);
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);

        _ = ClearLaterAsync(clipboard, text);
    }

    private static async Task ClearLaterAsync(IClipboard clipboard, string text)
    {
        try
        {
            await Task.Delay(ClearAfter);
            if (await clipboard.TryGetTextAsync() == text)
            {
                await clipboard.ClearAsync();
            }
        }
        catch
        {
            // The clipboard can be locked by another process at that moment;
            // nothing here is worth an error the user never asked about.
        }
    }
}
