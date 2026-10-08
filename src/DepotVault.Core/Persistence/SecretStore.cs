using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DepotVault.Core.Persistence;

public sealed record SteamCredentials(string AccountName, string RefreshToken);

public sealed class SecretStore
{
    private const uint Magic = 0x31415644;
    private const byte FlagDpapi = 1;
    private const string KeychainService = "DepotVault";
    private static readonly byte[] Entropy = "DepotVault.auth"u8.ToArray();
    private readonly bool _keychain;
    private readonly bool _interactive;

    public SecretStore(string path, bool interactive = false) : this(path, interactive, OperatingSystem.IsMacOS()) { }

    internal SecretStore(string path, bool interactive, bool useKeychain)
    {
        Path = path;
        _interactive = interactive;
        _keychain = useKeychain && OperatingSystem.IsMacOS();
    }

    public string Path { get; }

    private string KeychainAccount => System.IO.Path.GetFullPath(Path);

    public SteamCredentials Load()
    {
        if (_keychain && OperatingSystem.IsMacOS() && MacKeychain.Read(KeychainService, KeychainAccount, _interactive, out var stored) == MacKeychain.Status.Ok)
        {
            try
            {
                return Decode(stored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }
        if (!File.Exists(Path))
            return null;
        try
        {
            var raw = File.ReadAllBytes(Path);
            if (raw.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic)
                return null;
            var body = raw.AsSpan(5);
            byte[] plain;
            if ((raw[4] & FlagDpapi) != 0)
            {
                if (!OperatingSystem.IsWindows())
                    return null;
                plain = ProtectedData.Unprotect(body.ToArray(), Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                plain = body.ToArray();
            }
            return Decode(plain);
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public void Save(SteamCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var plain = Encode(credentials);
        if (_keychain && OperatingSystem.IsMacOS() && MacKeychain.Write(KeychainService, KeychainAccount, plain, _interactive))
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(Path))
                File.Delete(Path);
            return;
        }
        byte flags = 0;
        byte[] body = plain;
        if (OperatingSystem.IsWindows())
        {
            body = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            flags = FlagDpapi;
            CryptographicOperations.ZeroMemory(plain);
        }

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, options))
        {
            Span<byte> header = stackalloc byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            header[4] = flags;
            fs.Write(header);
            fs.Write(body);
            fs.Flush(true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, Path, true);
        if (flags == 0)
            CryptographicOperations.ZeroMemory(plain);
    }

    public void Clear()
    {
        if (_keychain && OperatingSystem.IsMacOS())
            MacKeychain.Delete(KeychainService, KeychainAccount, _interactive);
        if (File.Exists(Path))
            File.Delete(Path);
    }

    private static byte[] Encode(SteamCredentials c)
    {
        var accLen = Encoding.UTF8.GetByteCount(c.AccountName);
        var tokLen = Encoding.UTF8.GetByteCount(c.RefreshToken);
        var buf = new byte[8 + accLen + tokLen];
        BinaryPrimitives.WriteInt32LittleEndian(buf, accLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), tokLen);
        Encoding.UTF8.GetBytes(c.AccountName, buf.AsSpan(8));
        Encoding.UTF8.GetBytes(c.RefreshToken, buf.AsSpan(8 + accLen));
        return buf;
    }

    private static SteamCredentials Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 8)
            return null;
        var accLen = BinaryPrimitives.ReadInt32LittleEndian(buf);
        var tokLen = BinaryPrimitives.ReadInt32LittleEndian(buf[4..]);
        if (accLen < 0 || tokLen < 0 || 8L + accLen + tokLen != buf.Length)
            return null;
        return new SteamCredentials(Encoding.UTF8.GetString(buf.Slice(8, accLen)), Encoding.UTF8.GetString(buf.Slice(8 + accLen, tokLen)));
    }
}
