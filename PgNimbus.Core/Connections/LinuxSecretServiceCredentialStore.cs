using System.Runtime.InteropServices;

namespace PgNimbus.Core.Connections;

/// <summary>libsecret's non-variadic API, compatible with NativeAOT and ARM64.</summary>
public sealed class LinuxSecretServiceCredentialStore : ICredentialStore
{
    private const string Secret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    public void SavePassword(Guid connectionId, string password)
    {
        if (password.Contains('\0')) throw new CredentialStoreException();
        using var call = new SecretCall(connectionId);
        var ok = secret_password_storev_sync(0, call.Attributes, "default", "pgNimbus connection password", password, call.Cancellable, out var error);
        Check(error);
        if (ok == 0) throw new CredentialStoreException();
    }

    public string? LoadPassword(Guid connectionId)
    {
        using var call = new SecretCall(connectionId);
        var data = secret_password_lookupv_sync(0, call.Attributes, call.Cancellable, out var error);
        try
        {
            Check(error);
            return data == 0 ? null : Marshal.PtrToStringUTF8(data);
        }
        finally { if (data != 0) secret_password_free(data); }
    }

    public void DeletePassword(Guid connectionId)
    {
        using var call = new SecretCall(connectionId);
        // FALSE without GError also means "nothing matched".
        secret_password_clearv_sync(0, call.Attributes, call.Cancellable, out var error);
        Check(error);
        // clear only removes unlocked items. Lookup attempts an unlock; if an
        // item remains, report failure rather than claiming deletion succeeded.
        if (LoadPassword(connectionId) is not null) throw new CredentialStoreException();
    }

    private static void Check(nint error)
    {
        if (error == 0) return;
        g_error_free(error);
        throw new CredentialStoreException();
    }

    private sealed class SecretCall : IDisposable
    {
        private readonly List<nint> _strings = [];
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private CancellationTokenRegistration _registration;
        public nint Attributes { get; private set; }
        public nint Cancellable { get; private set; }

        public SecretCall(Guid id)
        {
            try
            {
                // Function pointers avoid managed callbacks and variadic marshalling.
                var library = NativeLibrary.Load(Glib);
                try { Attributes = g_hash_table_new(NativeLibrary.GetExport(library, "g_str_hash"), NativeLibrary.GetExport(library, "g_str_equal")); }
                finally { NativeLibrary.Free(library); }
                Add("application", "pgNimbus");
                Add("connection", id.ToString("N"));
                Cancellable = g_cancellable_new();
                _registration = _timeout.Token.Register(() => g_cancellable_cancel(Cancellable));
            }
            catch { Dispose(); throw; }
        }

        private void Add(string key, string value)
        {
            var k = Marshal.StringToCoTaskMemUTF8(key);
            _strings.Add(k);
            var v = Marshal.StringToCoTaskMemUTF8(value);
            _strings.Add(v);
            g_hash_table_insert(Attributes, k, v);
        }

        public void Dispose()
        {
            _registration.Dispose(); // Wait for an in-flight cancellation before unref.
            _timeout.Dispose();
            if (Cancellable != 0) g_object_unref(Cancellable);
            if (Attributes != 0) g_hash_table_unref(Attributes);
            foreach (var value in _strings) Marshal.FreeCoTaskMem(value);
        }
    }

    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_storev_sync(nint schema, nint attributes, [MarshalAs(UnmanagedType.LPUTF8Str)] string collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, nint cancellable, out nint error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, out nint error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)] private static extern void secret_password_free(nint password);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] private static extern nint g_hash_table_new(nint hash, nint equal);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] private static extern int g_hash_table_insert(nint table, nint key, nint value);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] private static extern void g_hash_table_unref(nint table);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)] private static extern void g_error_free(nint error);
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)] private static extern nint g_cancellable_new();
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)] private static extern void g_cancellable_cancel(nint cancellable);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] private static extern void g_object_unref(nint obj);
}
