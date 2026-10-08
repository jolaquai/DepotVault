using DepotVault.Core.Persistence;

namespace DepotVault.Tests.Persistence;

public class SecretStoreTests
{
    [Fact]
    public void RoundTrips()
    {
        using var dir = new TempDir();
        var store = new SecretStore(dir.Combine("auth.bin"));
        Assert.Null(store.Load());
        store.Save(new SteamCredentials("user", "eyJ.token.äö"));
        Assert.Equal(new SteamCredentials("user", "eyJ.token.äö"), store.Load());
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void FileDoesNotContainPlaintextOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows-only");
        using var dir = new TempDir();
        var store = new SecretStore(dir.Combine("auth.bin"));
        store.Save(new SteamCredentials("someaccount", "SECRETTOKENVALUE"));
        var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Path));
        Assert.DoesNotContain("SECRETTOKENVALUE", text);
        Assert.DoesNotContain("someaccount", text);
    }

    [Fact]
    public void FileIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix file modes only");
        using var dir = new TempDir();
        var store = new SecretStore(dir.Combine("auth.bin"), interactive: false, useKeychain: false);
        store.Save(new SteamCredentials("a", "b"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
    }

    [Fact]
    public void KeychainHoldsTokenOnMac()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Keychain is macOS-only");
        using var dir = new TempDir();
        var store = new SecretStore(dir.Combine("auth.bin"));
        try
        {
            store.Save(new SteamCredentials("someaccount", "SECRETTOKENVALUE"));
            if (Environment.GetEnvironmentVariable("DV_EXPECT_KEYCHAIN") == "1")
                Assert.False(File.Exists(store.Path), "Token should be in the Keychain, not in the file");
            Assert.Equal(new SteamCredentials("someaccount", "SECRETTOKENVALUE"), store.Load());
            store.Save(new SteamCredentials("someaccount", "ROTATED"));
            Assert.Equal("ROTATED", store.Load().RefreshToken);
        }
        finally
        {
            store.Clear();
        }
        Assert.Null(store.Load());
    }

    [Fact]
    public void CorruptFileYieldsNull()
    {
        using var dir = new TempDir();
        var store = new SecretStore(dir.Combine("auth.bin"));
        File.WriteAllBytes(store.Path, [0x44, 0x56, 0x41, 0x31, 1, 9, 9, 9]);
        Assert.Null(store.Load());
    }
}
