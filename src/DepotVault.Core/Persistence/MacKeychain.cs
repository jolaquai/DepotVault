using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DepotVault.Core.Persistence;

[SupportedOSPlatform("macos")]
internal static unsafe partial class MacKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    internal enum Status
    {
        Ok,
        NotFound,
        Failed,
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainSetUserInteractionAllowed(byte state);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint passwordLength, byte* password, nint* item);

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(nint keychainOrArray, uint serviceLength, byte* service, uint accountLength, byte* account, uint* passwordLength, void** password, nint* item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint length, byte* data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, void* data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    public static Status Read(string service, string account, bool interactive, out byte[] data)
    {
        data = null;
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        try
        {
            SecKeychainSetUserInteractionAllowed(interactive ? (byte)1 : (byte)0);
            uint len = 0;
            void* pw = null;
            fixed (byte* ps = s, pa = a)
            {
                var rc = SecKeychainFindGenericPassword(0, (uint)s.Length, ps, (uint)a.Length, pa, &len, &pw, null);
                if (rc == ErrSecItemNotFound)
                    return Status.NotFound;
                if (rc != 0)
                    return Status.Failed;
            }
            try
            {
                data = new ReadOnlySpan<byte>(pw, (int)len).ToArray();
            }
            finally
            {
                SecKeychainItemFreeContent(0, pw);
            }
            return Status.Ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Status.Failed;
        }
    }

    public static bool Write(string service, string account, ReadOnlySpan<byte> data, bool interactive)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        try
        {
            SecKeychainSetUserInteractionAllowed(interactive ? (byte)1 : (byte)0);
            fixed (byte* ps = s, pa = a, pd = data)
            {
                nint item = 0;
                var rc = SecKeychainFindGenericPassword(0, (uint)s.Length, ps, (uint)a.Length, pa, null, null, &item);
                if (rc == 0)
                {
                    try
                    {
                        return SecKeychainItemModifyAttributesAndData(item, 0, (uint)data.Length, pd) == 0;
                    }
                    finally
                    {
                        CFRelease(item);
                    }
                }
                if (rc != ErrSecItemNotFound)
                    return false;
                return SecKeychainAddGenericPassword(0, (uint)s.Length, ps, (uint)a.Length, pa, (uint)data.Length, pd, null) == 0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static void Delete(string service, string account, bool interactive)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        try
        {
            SecKeychainSetUserInteractionAllowed(interactive ? (byte)1 : (byte)0);
            fixed (byte* ps = s, pa = a)
            {
                nint item = 0;
                if (SecKeychainFindGenericPassword(0, (uint)s.Length, ps, (uint)a.Length, pa, null, null, &item) != 0)
                    return;
                SecKeychainItemDelete(item);
                CFRelease(item);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
