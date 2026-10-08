using SteamKit2.Authentication;

namespace DepotVault.Core.Steam;

public sealed class QrLogin
{
    private readonly QrAuthSession _session;

    internal QrLogin(QrAuthSession session)
    {
        _session = session;
        _session.ChallengeURLChanged = () => ChallengeUrlChanged?.Invoke(_session.ChallengeURL);
    }

    public string ChallengeUrl => _session.ChallengeURL;

    public event Action<string> ChallengeUrlChanged;

    internal Task<AuthPollResult> WaitAsync(CancellationToken ct) => _session.PollingWaitForResultAsync(ct);
}
