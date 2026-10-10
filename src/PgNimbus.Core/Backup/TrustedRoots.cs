using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PgNimbus.Core.Connections;
using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Backup;

/// <summary>
/// The certificate authorities this machine trusts, as one PEM file libpq can
/// read. A profile in Verify CA or Verify full without a root certificate of its
/// own is checked by the app against the OS trust store (Windows' and macOS's
/// stores, the system bundle on Linux). libpq has no such store: given no
/// <c>sslrootcert</c> it reads <c>~/.postgresql/root.crt</c> and refuses to
/// connect when that is missing, so pg_dump would fail exactly where the app
/// connects fine. Exporting the same roots keeps the two checking the server
/// the same way.
/// </summary>
public static class TrustedRoots
{
    /// <summary>The file under the app data directory the roots are written to.</summary>
    public const string FileName = "trusted-roots.pem";

    /// <summary>
    /// The OS's trusted roots in PEM, from the machine store and the user's,
    /// each certificate once.
    /// </summary>
    public static string ExportPem()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var certificate in store.Certificates)
                {
                    using (certificate)
                    {
                        if (seen.Add(certificate.Thumbprint))
                        {
                            builder.AppendLine(certificate.ExportCertificatePem());
                        }
                    }
                }
            }
            catch (CryptographicException)
            {
                // A store that does not exist on this platform (the user's
                // root store on a fresh Linux account) adds nothing.
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes <see cref="ExportPem"/> to the app data directory (owner-only on
    /// Linux and macOS, like every app data file) and returns its path, or null
    /// when there is no app data directory or nothing to write. Written fresh
    /// for each run rather than cached: it is a few hundred kilobytes, and a CA
    /// removed from the OS store must stop being trusted here too. Never in the
    /// shared temp directory, where another local user could replace it between
    /// the write and the read and have pg_dump trust their CA.
    /// </summary>
    public static string? WriteForTools()
    {
        var path = AppDataPaths.Resolve(FileName);
        if (path is null)
        {
            return null;
        }

        var pem = ExportPem();
        if (pem.Length == 0)
        {
            return null;
        }

        AppDataFile.WriteAllText(path, pem);
        return path;
    }
}
