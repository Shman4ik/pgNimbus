namespace PgNimbus.Core.Connections;

/// <summary>
/// What a profile's client certificate and key files must be before a connect
/// tries them: there, a key only beside a certificate, and a key with no
/// passphrase.
/// <para>
/// A passphrase is refused rather than stored. Supporting it would mean a third
/// secret in the credential store, passed to Npgsql as <c>SSL Password</c> and
/// to pg_dump and pg_restore as libpq's <c>sslpassword</c>, which has no
/// environment variable: it would go on the command line, where any local user
/// can read it. An unprotected key file that only its owner can
/// read is what libpq itself requires of a key (it refuses one that the group or
/// others can read), so the refusal costs one <c>openssl</c> command, which the
/// message names.
/// </para>
/// </summary>
public static class ClientCertificates
{
    /// <summary>
    /// Null when the files can be tried, else a sentence for the connect form.
    /// Only what can be told from the files is checked: a key is "protected" when
    /// it is PEM and says so (<c>ENCRYPTED PRIVATE KEY</c>, or OpenSSL's legacy
    /// <c>Proc-Type: 4,ENCRYPTED</c>). A DER key or a PKCS#12 file with a password
    /// reaches Npgsql, whose error then says it could not read the file.
    /// </summary>
    public static string? Check(string? certificatePath, string? keyPath)
    {
        var certificate = Blank(certificatePath) ? null : certificatePath!.Trim();
        var key = Blank(keyPath) ? null : keyPath!.Trim();
        if (certificate is null)
        {
            return key is null ? null : "A client key needs its client certificate. Choose the certificate file too.";
        }

        if (!File.Exists(certificate))
        {
            return $"The client certificate file was not found: {certificate}";
        }

        if (key is null)
        {
            return null;
        }

        if (!File.Exists(key))
        {
            return $"The client key file was not found: {key}";
        }

        return IsPassphraseProtected(key)
            ? "The client key is protected by a passphrase, which pgNimbus does not support. Save a copy without it (openssl pkey -in client.key -out client-plain.key) and make that file readable only by you."
            : null;
    }

    /// <summary>True when <paramref name="keyPath"/> is a PEM key that says it is encrypted.</summary>
    public static bool IsPassphraseProtected(string keyPath)
    {
        try
        {
            // A key is a few kilobytes; a file far past that is not a PEM key.
            var info = new FileInfo(keyPath);
            if (!info.Exists || info.Length > 1024 * 1024)
            {
                return false;
            }

            var text = File.ReadAllText(keyPath);
            return text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)
                || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
