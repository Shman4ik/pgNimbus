using System.Runtime.InteropServices;
using System.Text;

namespace PgNimbus.Core.Connections;

/// <summary>Generic passwords in the user's macOS Keychain through SecItem APIs.</summary>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300;
    private const int Duplicate = -25299;

    public void SavePassword(Guid connectionId, string password)
    {
        using var query = new KeychainQuery(connectionId);
        using var values = new CfDictionary();
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            values.Data("kSecValueData", bytes);
            var status = SecItemUpdate(query.Handle, values.Handle);
            if (status == NotFound)
            {
                query.Data("kSecValueData", bytes);
                status = SecItemAdd(query.Handle, 0);
                // Another app instance may have added the same key concurrently.
                if (status == Duplicate)
                {
                    using var retry = new KeychainQuery(connectionId);
                    status = SecItemUpdate(retry.Handle, values.Handle);
                }
            }
            Check(status);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public string? LoadPassword(Guid connectionId)
    {
        using var query = new KeychainQuery(connectionId);
        query.Constant("kSecReturnData", "kCFBooleanTrue", CoreFoundation);
        query.Constant("kSecMatchLimit", "kSecMatchLimitOne");
        var status = SecItemCopyMatching(query.Handle, out var data);
        try
        {
            if (status == NotFound) return null;
            Check(status);
            var bytes = new byte[checked((int)CFDataGetLength(data))];
            try
            {
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (data != 0) CFRelease(data); }
    }

    public void DeletePassword(Guid connectionId)
    {
        using var query = new KeychainQuery(connectionId);
        var status = SecItemDelete(query.Handle);
        if (status != NotFound) Check(status);
    }

    private static void Check(int status)
    {
        if (status != 0) throw new CredentialStoreException();
    }

    private sealed class KeychainQuery : CfDictionary
    {
        public KeychainQuery(Guid id)
        {
            try
            {
                Constant("kSecClass", "kSecClassGenericPassword");
                Text("kSecAttrService", "pgNimbus");
                Text("kSecAttrAccount", id.ToString("N"));
                // Never park a background operation behind an invisible OS prompt.
                // The dialog directs users to unlock the store and retry instead.
                Constant("kSecUseAuthenticationUI", "kSecUseAuthenticationUIFail");
            }
            catch { Dispose(); throw; }
        }
    }

    private class CfDictionary : IDisposable
    {
        // Keep frameworks loaded while dictionaries reference exported constants.
        private static readonly Lazy<nint> SecurityLibrary = new(() => NativeLibrary.Load(Security));
        private static readonly Lazy<nint> FoundationLibrary = new(() => NativeLibrary.Load(CoreFoundation));
        // Null callbacks: values are explicitly retained by this owner until disposal.
        public nint Handle { get; } = CFDictionaryCreateMutable(0, 0, 0, 0);
        private readonly List<nint> _owned = [];
        public void Constant(string key, string value, string library = Security) => Set(key, Symbol(library, value));
        public void Text(string key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Own(key, CFStringCreateWithBytes(0, bytes, bytes.Length, 0x08000100, 0));
        }
        public void Data(string key, byte[] value) => Own(key, CFDataCreate(0, value, value.Length));
        private void Own(string key, nint value)
        {
            if (value == 0) throw new CredentialStoreException();
            _owned.Add(value);
            Set(key, value);
        }
        private void Set(string key, nint value) => CFDictionarySetValue(Handle, Symbol(Security, key), value);
        private static nint Symbol(string library, string name)
        {
            var handle = library == Security ? SecurityLibrary.Value : FoundationLibrary.Value;
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, name));
        }
        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var item in _owned) CFRelease(item);
        }
    }

    [DllImport(Security)] private static extern int SecItemAdd(nint attributes, nint result);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint attributes);
    [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(Security)] private static extern int SecItemDelete(nint query);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint count, uint encoding, byte externalRepresentation);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint count);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
