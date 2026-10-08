using System.Net.Sockets;
using DepotVault.Core.Steam;

namespace DepotVault.Core.Download;

public enum JobErrorKind
{
    None,
    Other,
    ManifestUnavailable,
    AccessDenied,
    DiskFull,
    Network,
    SteamTimeout,
}

public static class JobErrors
{
    private const int ENOSPC = 28;
    private const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);
    private const int ERROR_DISK_FULL = unchecked((int)0x80070070);

    public static bool IsTransient(JobErrorKind kind) => kind is JobErrorKind.Network or JobErrorKind.SteamTimeout;

    public static bool IsDiskFull(Exception ex) => ex is IOException { HResult: ENOSPC or ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL };

    public static (JobErrorKind Kind, string Message) Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ManifestUnavailableException mu:
                    return (JobErrorKind.ManifestUnavailable, $"Steam no longer serves manifest {mu.ManifestId} of depot {mu.DepotId} (purged or never public).");
                case DepotAccessException da:
                    return (JobErrorKind.AccessDenied, $"This account cannot access depot {da.DepotId} ({da.Result}).");
                case IOException when IsDiskFull(e):
                    return (JobErrorKind.DiskFull, "Not enough disk space in the library folder. Free some space, then resume.");
                case HttpRequestException or SocketException:
                    return (JobErrorKind.Network, $"Network problem: {e.Message} Resume to retry.");
                case TimeoutException or OperationCanceledException:
                    return (JobErrorKind.SteamTimeout, "Steam did not answer in time. Check that you are signed in, then resume.");
            }
        }
        return (JobErrorKind.Other, ex.Message);
    }
}
