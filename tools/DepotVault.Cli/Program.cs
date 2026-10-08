using System.Text;
using DepotVault.Cli;
using DepotVault.Core.Download;
using DepotVault.Core.Library;
using DepotVault.Core.Persistence;
using DepotVault.Core.Steam;
using QRCoder;
using SteamKit2;
using CdnClient = SteamKit2.CDN.Client;

var paths = AppPaths.CreateDefault();
var secrets = new SecretStore(paths.Auth);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await using var session = new SteamSession(secrets);
using var apps = new AppRepository(paths);
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
        if (!await LogOnAsync())
            return 1;
        await Task.Delay(2000, ct);
        Console.WriteLine($"Logged on as {session.AccountName} ({session.SteamId}), cell {session.CellId}, {session.Licenses.Count} licenses");
        break;
    case "logout":
        secrets.Clear();
        Console.WriteLine("Saved token removed.");
        break;
    case "app" when args.Length >= 2:
    {
        if (!await LogOnAsync())
            return 1;
        var meta = new DepotMetadataService(session, apps);
        var rec = await meta.RefreshAsync(uint.Parse(args[1]), ct);
        var sel = DepotMetadataParser.SelectDefault(rec.Depots, DepotFilter.ForCurrentPlatform());
        Console.WriteLine($"{rec.AppId} {rec.Name} installdir='{rec.InstallDir}' build={rec.PublicBuildId}");
        foreach (var d in rec.Depots)
            Console.WriteLine($" {(sel.Contains(d.DepotId) ? '*' : ' ')} {d.DepotId,-8} {d.Name,-40} os={d.OsList} arch={d.OsArch} lang={d.Language} manifest={d.CurrentManifestId}");
        apps.Flush();
        break;
    }
    case "manifest" when args.Length >= 3:
    {
        if (!await LogOnAsync())
            return 1;
        var appId = uint.Parse(args[1]);
        var depotId = uint.Parse(args[2]);
        ulong manifestId;
        if (args.Length >= 4)
            manifestId = ulong.Parse(args[3]);
        else
            manifestId = (await new DepotMetadataService(session, apps).RefreshAsync(appId, ct)).Depots.Single(d => d.DepotId == depotId).CurrentManifestId;
        using var cdnClient = new CdnClient(session.Client);
        var svc = new ManifestService(session, new DepotKeyCache(session), new CdnPool(session), cdnClient);
        var path = Path.Combine(paths.Root, "cli-cache", $"{depotId}_{manifestId}.manifest.bin");
        try
        {
            var m = await svc.GetAsync(appId, depotId, manifestId, path, ct);
            var reloaded = DepotManifest.LoadFromFile(path);
            Console.WriteLine($"Manifest {manifestId}: {m.Files.Count} entries, {m.TotalUncompressedSize:N0} bytes, created {m.CreationTime:u}");
            Console.WriteLine($"Reloaded from {path}: {reloaded.Files.Count} entries, encrypted names={reloaded.FilenamesEncrypted}");
            foreach (var f in m.Files.Take(10))
                Console.WriteLine($"  {f.FileName} ({f.TotalSize:N0} bytes, {f.Chunks.Count} chunks, {f.Flags})");
        }
        catch (ManifestUnavailableException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
        break;
    }
    case "download" when args.Length >= 5:
    {
        if (!await LogOnAsync())
            return 1;
        var appId = uint.Parse(args[1]);
        var depotId = uint.Parse(args[2]);
        var manifestId = ulong.Parse(args[3]);
        var dir = Path.GetFullPath(args[4]);
        using var cdnClient = new CdnClient(session.Client);
        var keys = new DepotKeyCache(session);
        var pool = new CdnPool(session);
        var runner = new DepotJobRunner(new ManifestService(session, keys, pool, cdnClient), id => new CdnChunkSource(id, pool, cdnClient, keys), new DirectoryTargetResolver(dir), () => 16);
        var job = new DownloadJob { AppId = appId, DepotId = depotId, ManifestId = manifestId, TargetVersionId = "cli" };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = runner.RunAsync(job, ct);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(1000, ct));
            var c = job.Counters;
            Console.Write($"\r{c.CompletedBytes / 1048576.0,10:N1} / {c.TotalBytes / 1048576.0:N1} MiB  net {c.DownloadedBytes / 1048576.0:N1} MiB  reused {c.ReusedBytes / 1048576.0:N1} MiB   ");
        }
        await run;
        Console.WriteLine($"\nDone in {sw.Elapsed}. All file hashes verified against the manifest.");
        break;
    }
    default:
        Console.WriteLine("dvcli login | login-qr | whoami | logout | app <appid> | manifest <appid> <depotid> [manifestid] | download <appid> <depotid> <manifestid> <dir>");
        return 1;
}
return 0;

async Task<bool> LogOnAsync()
{
    if (await session.TryLogOnWithSavedTokenAsync(ct))
        return true;
    Console.WriteLine("No valid saved token. Run `dvcli login` or `dvcli login-qr`.");
    return false;
}

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
