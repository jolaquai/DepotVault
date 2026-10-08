using DepotVault.Core.Persistence;
using SteamKit2;
using SteamKit2.Authentication;

namespace DepotVault.Core.Steam;

public enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
    LoggingOn,
    LoggedOn,
}

public sealed class SteamLoginException(EResult result, string message) : Exception(message)
{
    public EResult Result { get; } = result;
}

public sealed class SteamSession : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly SecretStore _secrets;
    private readonly CallbackManager _callbacks;
    private readonly Thread _pump;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource _connectTcs;
    private TaskCompletionSource<SteamUser.LoggedOnCallback> _logOnTcs;
    private SteamCredentials _active;
    private bool _wantOnline;
    private int _reconnecting;
    private volatile bool _running = true;
    private SessionState _state;

    public SteamSession(SecretStore secrets)
    {
        _secrets = secrets;
        Client = new SteamClient(SteamConfiguration.Create(c => c.WithConnectionTimeout(TimeSpan.FromSeconds(30))));
        User = Client.GetHandler<SteamUser>();
        Apps = Client.GetHandler<SteamApps>();
        Content = Client.GetHandler<SteamContent>();
        _callbacks = new CallbackManager(Client);
        _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        _pump = new Thread(Pump) { IsBackground = true, Name = "SteamKit2 callbacks" };
        _pump.Start();
    }

    public SteamClient Client { get; }
    public SteamUser User { get; }
    public SteamApps Apps { get; }
    public SteamContent Content { get; }
    public SessionState State => _state;
    public string AccountName => _active?.AccountName;
    public SteamID SteamId => Client.SteamID;
    public uint CellId { get; private set; }
    public IReadOnlyList<SteamApps.LicenseListCallback.License> Licenses { get; private set; } = [];
    public bool HasSavedToken => _secrets.Load() is not null;

    public event Action<SessionState> StateChanged;
    public event Action<IReadOnlyList<SteamApps.LicenseListCallback.License>> LicensesChanged;
    public event Action<EResult> SessionLost;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Task wait;
        lock (_gate)
        {
            if (Client.IsConnected)
                return;
            _connectTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _connectTcs.Task;
            if (_state != SessionState.Connecting)
            {
                SetState(SessionState.Connecting);
                Client.Connect();
            }
        }
        await wait.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryLogOnWithSavedTokenAsync(CancellationToken ct = default)
    {
        var saved = _secrets.Load();
        if (saved is null)
            return false;
        try
        {
            await LogOnWithTokenAsync(saved, ct).ConfigureAwait(false);
            return true;
        }
        catch (SteamLoginException ex) when (IsTokenRejection(ex.Result))
        {
            _secrets.Clear();
            return false;
        }
    }

    public async Task LogOnWithCredentialsAsync(string username, string password, bool remember, IGuardPrompt prompt, CancellationToken ct = default)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        var auth = await Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = remember,
            DeviceFriendlyName = DeviceName,
            Authenticator = new GuardPromptAuthenticator(prompt),
        }).ConfigureAwait(false);
        var result = await auth.PollingWaitForResultAsync(ct).ConfigureAwait(false);
        await CompleteAuthAsync(result, remember, ct).ConfigureAwait(false);
    }

    public async Task<QrLogin> BeginQrLogOnAsync(CancellationToken ct = default)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        var auth = await Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            IsPersistentSession = true,
            DeviceFriendlyName = DeviceName,
        }).ConfigureAwait(false);
        return new QrLogin(auth);
    }

    public async Task CompleteQrLogOnAsync(QrLogin qr, bool remember, CancellationToken ct = default)
    {
        var result = await qr.WaitAsync(ct).ConfigureAwait(false);
        await CompleteAuthAsync(result, remember, ct).ConfigureAwait(false);
    }

    public void LogOff(bool forget)
    {
        _wantOnline = false;
        _active = null;
        if (forget)
            _secrets.Clear();
        if (Client.IsConnected)
        {
            User.LogOff();
            Client.Disconnect();
        }
        SetState(SessionState.Disconnected);
    }

    private async Task CompleteAuthAsync(AuthPollResult result, bool remember, CancellationToken ct)
    {
        var creds = new SteamCredentials(result.AccountName, result.RefreshToken);
        await LogOnWithTokenAsync(creds, ct).ConfigureAwait(false);
        if (remember)
            _secrets.Save(creds);
    }

    private async Task LogOnWithTokenAsync(SteamCredentials creds, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        Task<SteamUser.LoggedOnCallback> wait;
        lock (_gate)
        {
            _logOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _logOnTcs.Task;
            SetState(SessionState.LoggingOn);
            User.LogOn(new SteamUser.LogOnDetails
            {
                Username = creds.AccountName,
                AccessToken = creds.RefreshToken,
                ShouldRememberPassword = true,
                LoginID = 0x44564C54,
            });
        }
        var cb = await wait.WaitAsync(ct).ConfigureAwait(false);
        if (cb.Result != EResult.OK)
        {
            SetState(SessionState.Connected);
            throw new SteamLoginException(cb.Result, $"Steam logon failed: {cb.Result} ({cb.ExtendedResult})");
        }
        _active = creds;
        _wantOnline = true;
        CellId = cb.CellID;
        SetState(SessionState.LoggedOn);
    }

    private static bool IsTokenRejection(EResult r) => r is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked or EResult.InvalidSignature or EResult.AccountLogonDenied;

    private static string DeviceName => $"DepotVault ({Environment.MachineName})";

    private void Pump()
    {
        while (_running)
            _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            tcs = _connectTcs;
            _connectTcs = null;
            SetState(SessionState.Connected);
        }
        tcs?.TrySetResult();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        TaskCompletionSource tcs;
        TaskCompletionSource<SteamUser.LoggedOnCallback> logOn;
        lock (_gate)
        {
            tcs = _connectTcs;
            _connectTcs = null;
            logOn = _logOnTcs;
            _logOnTcs = null;
            SetState(SessionState.Disconnected);
        }
        tcs?.TrySetException(new IOException("Could not connect to Steam."));
        logOn?.TrySetException(new IOException("Disconnected from Steam during logon."));
        if (!cb.UserInitiated && _wantOnline && _running)
            _ = ReconnectAsync();
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        TaskCompletionSource<SteamUser.LoggedOnCallback> tcs;
        lock (_gate)
        {
            tcs = _logOnTcs;
            _logOnTcs = null;
        }
        tcs?.TrySetResult(cb);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        SetState(Client.IsConnected ? SessionState.Connected : SessionState.Disconnected);
        if (IsTokenRejection(cb.Result))
        {
            _wantOnline = false;
            _secrets.Clear();
            SessionLost?.Invoke(cb.Result);
        }
    }

    private void OnLicenseList(SteamApps.LicenseListCallback cb)
    {
        if (cb.Result != EResult.OK)
            return;
        Licenses = cb.LicenseList;
        LicensesChanged?.Invoke(Licenses);
    }

    private async Task ReconnectAsync()
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
            return;
        try
        {
            for (var attempt = 0; _wantOnline && !_lifetime.IsCancellationRequested; attempt++)
            {
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], _lifetime.Token).ConfigureAwait(false);
                var creds = _active;
                if (creds is null)
                    return;
                try
                {
                    await LogOnWithTokenAsync(creds, _lifetime.Token).ConfigureAwait(false);
                    return;
                }
                catch (SteamLoginException ex) when (IsTokenRejection(ex.Result))
                {
                    _wantOnline = false;
                    _secrets.Clear();
                    SessionLost?.Invoke(ex.Result);
                    return;
                }
                catch (SteamLoginException) { }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
        }
    }

    private void SetState(SessionState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _wantOnline = false;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (Client.IsConnected)
            Client.Disconnect();
        _running = false;
        _pump.Join(TimeSpan.FromSeconds(2));
        _lifetime.Dispose();
    }
}
