using System.Text;
using DepotVault.Cli;
using DepotVault.Core.Persistence;
using DepotVault.Core.Steam;
using QRCoder;

var paths = AppPaths.CreateDefault();
var secrets = new SecretStore(paths.Auth);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await using var session = new SteamSession(secrets);
var ct = cts.Token;

switch (args.FirstOrDefault())
{
    case "login":
        Console.Write("Username: ");
        var user = Console.ReadLine()?.Trim();
        Console.Write("Password: ");
        var pass = ReadSecret();
        await session.LogOnWithCredentialsAsync(user, pass, true, new ConsoleGuardPrompt(), ct);
        Console.WriteLine($"Logged on as {session.AccountName} ({session.SteamId}). Token saved to {paths.Auth}");
        break;
    case "login-qr":
        var qr = await session.BeginQrLogOnAsync(ct);
        PrintQr(qr.ChallengeUrl);
        qr.ChallengeUrlChanged += PrintQr;
        await session.CompleteQrLogOnAsync(qr, true, ct);
        Console.WriteLine($"Logged on as {session.AccountName} ({session.SteamId}). Token saved to {paths.Auth}");
        break;
    case "whoami":
        if (!await session.TryLogOnWithSavedTokenAsync(ct))
        {
            Console.WriteLine("No valid saved token. Run `dvcli login` or `dvcli login-qr`.");
            return 1;
        }
        await Task.Delay(2000, ct);
        Console.WriteLine($"Logged on as {session.AccountName} ({session.SteamId}), cell {session.CellId}, {session.Licenses.Count} licenses");
        break;
    case "logout":
        secrets.Clear();
        Console.WriteLine("Saved token removed.");
        break;
    default:
        Console.WriteLine("dvcli login | login-qr | whoami | logout");
        return 1;
}
return 0;

static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter)
            break;
        if (k.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
                sb.Length--;
            continue;
        }
        sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static void PrintQr(string url)
{
    using var gen = new QRCodeGenerator();
    using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
    Console.WriteLine(new AsciiQRCode(data).GetGraphicSmall());
    Console.WriteLine("Scan with the Steam mobile app.");
}
