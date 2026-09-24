using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CmxDialer.Config;
using CmxDialer.Infrastructure;
using CmxDialer.Services;
using Microsoft.Win32;

namespace CmxDialer.ViewModels;

/// <summary>
/// App shell. Owns the API client, the SIP phone and the WebSocket, and moves the window
/// through Login → Campaign select → Connecting (register SIP, connect, set Not Ready) → Dialer.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private object? _currentView;
    private AgentInfo? _agent;
    private bool _sessionActive;
    private bool _sipRegistered;
    private bool _topmost;
    private DialerViewModel? _dialer;
    private SipCredentials? _sipCredentials;
    private string? _sipHost;
    private SipTunnel? _tunnel;

    public AppSettings Settings { get; }
    public ApiClient Api { get; private set; }
    public SipPhone Phone { get; } = new();
    public DialerSocket? Socket { get; private set; }

    public List<Campaign> MyCampaigns { get; set; } = new();
    public List<Campaign> WorkingCampaigns { get; private set; } = new();

    /// <summary>Raised when the window should close for real (app exit).</summary>
    public event Action? RequestClose;
    /// <summary>Raised when a call arrives — flash the taskbar and bring the window forward.</summary>
    public event Action? RequestAttention;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Api = new ApiClient(settings.ServerUrl);
        _topmost = settings.AlwaysOnTop;
        Phone.SetAudioDevices(settings.AudioOutputDeviceIndex, settings.AudioInputDeviceIndex);

        Phone.RegistrationChanged += (registered, error) => Ui(() =>
        {
            SipRegistered = registered;
            _dialer?.OnRegistrationChanged();
            if (!registered && SessionActive && error != null)
                _dialer?.ShowError("Phone disconnected — reconnecting…");
            else if (registered)
                _dialer?.ClearPhoneError();
        });
        // Only auto-answer while the agent is Ready or already handling a call (see DialerViewModel).
        Phone.ShouldAnswer = () => _dialer?.AcceptsIncomingCalls ?? false;
        Phone.CallAnswered += () => Ui(() => _dialer?.OnPhoneAnswered());
        Phone.CallEnded += () => Ui(() => _dialer?.OnPhoneEnded());

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        SignOutCommand = new AsyncCommand(SignOutAsync, () => HasAgent);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !SessionActive);
        ToggleTopmostCommand = new RelayCommand(() => Topmost = !Topmost);

        CurrentView = new LoginViewModel(this);
    }

    // ---------------------------------------------------------------- bindable state

    public object? CurrentView
    {
        get => _currentView;
        private set => Set(ref _currentView, value);
    }

    public AgentInfo? Agent
    {
        get => _agent;
        private set
        {
            if (Set(ref _agent, value)) Notify(nameof(AgentName), nameof(HasAgent));
        }
    }

    public string AgentName => Agent?.FullName ?? "";
    public bool HasAgent => Agent != null;

    /// <summary>True while the phone is registered and the agent is working (dialer or changing campaign).</summary>
    public bool SessionActive
    {
        get => _sessionActive;
        private set
        {
            if (Set(ref _sessionActive, value)) Notify(nameof(ShowClose));
        }
    }

    public bool ShowClose => !SessionActive;

    public bool SipRegistered
    {
        get => _sipRegistered;
        private set
        {
            if (Set(ref _sipRegistered, value)) Notify(nameof(RegistrationText), nameof(RegistrationColor));
        }
    }

    public string RegistrationText => SipRegistered ? "Phone registered" : "Phone not registered";
    public string RegistrationColor => SipRegistered ? "#4ADE80" : "#8A97AD";

    public bool Topmost
    {
        get => _topmost;
        set => Set(ref _topmost, value);
    }

    public ICommand SignOutCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ToggleTopmostCommand { get; }

    public void RaiseAttention() => RequestAttention?.Invoke();

    // ---------------------------------------------------------------- flow

    public async Task OnLoggedInAsync(AgentInfo agent)
    {
        Agent = agent;
        Log.Info($"Logged in as {agent.FullName} ({agent.Email}), extension {agent.Extension ?? "none"}");

        if (string.IsNullOrWhiteSpace(agent.Extension))
        {
            // resolveAgentContext() found no phones row for this user's phone_login.
            MessageBox.Show(
                "Your account doesn't have a phone assigned in ViciDial yet, so the dialer can't register your phone.\n\nAsk an admin to assign a phone to your user, then sign in again.",
                "No phone assigned", MessageBoxButton.OK, MessageBoxImage.Warning);
            await EndSessionAsync(null, callLogout: true);
            return;
        }

        var select = new CampaignSelectViewModel(this, isChange: false);
        CurrentView = select;
        await select.LoadAsync();
    }

    public async Task OnCampaignsChosenAsync(List<Campaign> campaigns, bool isChange)
    {
        WorkingCampaigns = campaigns;

        if (isChange && SessionActive && Phone.IsRegistered && Socket?.IsConnected == true)
        {
            var status = await Api.GetStatusAsync() ?? new AgentStatusInfo { Status = "NOT_READY" };
            await EnterDialerAsync(status);
            return;
        }

        var connecting = new ConnectingViewModel(this);
        CurrentView = connecting;
        await connecting.RunAsync();
    }

    public async Task RegisterPhoneAsync()
    {
        _sipCredentials ??= await Api.GetSipCredentialsAsync();

        bool ok;
        if (Settings.UseSipTunnel)
        {
            // SIP through the signed-in HTTPS connection (port 443) — works on any network.
            if (_tunnel == null)
            {
                _tunnel = new SipTunnel(Api);
                _tunnel.Start();
            }
            if (!await _tunnel.WaitConnectedAsync(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("Couldn't open the secure phone connection to the dialer server.");

            var local = _tunnel.LocalEndPoint;
            Log.Info($"Registering {_sipCredentials.Extension} through the SIP tunnel ({Api.BaseUri.Host}:443)");
            ok = await Phone.RegisterAsync("127.0.0.1", local.Port, _sipCredentials.Extension, _sipCredentials.Password,
                Settings.SipRegisterExpirySeconds, TimeSpan.FromSeconds(12), outboundProxy: local);
        }
        else
        {
            _sipHost = !string.IsNullOrWhiteSpace(Settings.SipHostOverride)
                ? Settings.SipHostOverride!.Trim()
                : new Uri(_sipCredentials.WssUrl).Host;
            Log.Info($"Registering {_sipCredentials.Extension} to {_sipHost}:{Settings.SipPort} (UDP)");
            ok = await Phone.RegisterAsync(_sipHost, Settings.SipPort, _sipCredentials.Extension,
                _sipCredentials.Password, Settings.SipRegisterExpirySeconds, TimeSpan.FromSeconds(12));
        }

        SipRegistered = ok;
        _dialer?.OnRegistrationChanged();
        if (!ok) throw new InvalidOperationException(Phone.LastError ?? "Phone registration failed.");
    }

    public async Task ConnectSocketAsync()
    {
        if (Socket != null)
        {
            Socket.MessageReceived -= OnSocketMessage;
            Socket.ConnectionChanged -= OnSocketConnectionChanged;
            Socket.SessionRejected -= OnSocketSessionRejected;
            Socket.Dispose();
        }

        Socket = new DialerSocket(Api);
        Socket.MessageReceived += OnSocketMessage;
        Socket.ConnectionChanged += OnSocketConnectionChanged;
        Socket.SessionRejected += OnSocketSessionRejected;
        Socket.Start();

        if (!await Socket.WaitConnectedAsync(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Couldn't open the live connection to the dialer server.");
    }

    /// <summary>Per the requested flow the agent always lands in Not Ready — unless a call is still in progress.</summary>
    public async Task<AgentStatusInfo> PrepareStatusAsync()
    {
        var current = await Api.GetStatusAsync();
        if (current != null && DialerViewModel.SystemStatuses.Contains(current.Status))
            return current; // mid-call or disposition pending (e.g. app restarted) — don't touch it

        return await Api.SetStatusAsync("NOT_READY", WorkingCampaigns.FirstOrDefault()?.CampaignId);
    }

    public async Task EnterDialerAsync(AgentStatusInfo status)
    {
        SessionActive = true;
        _dialer?.Dispose();
        _dialer = new DialerViewModel(this, WorkingCampaigns, status);
        CurrentView = _dialer;
        await _dialer.InitializeAsync();
    }

    /// <summary>"Change" next to the campaign name — only allowed while Not Ready and idle.</summary>
    public async Task ChangeCampaignsAsync()
    {
        _dialer?.Dispose();
        _dialer = null;
        var select = new CampaignSelectViewModel(this, isChange: true);
        CurrentView = select;
        await select.LoadAsync();
    }

    public async Task CancelCampaignChangeAsync()
    {
        var status = await Api.GetStatusAsync() ?? new AgentStatusInfo { Status = "NOT_READY" };
        await EnterDialerAsync(status);
    }

    /// <summary>From the Connecting screen's "Back": stop what was started and pick campaigns again.</summary>
    public async Task BackToCampaignSelectAsync()
    {
        StopRealtime();
        var select = new CampaignSelectViewModel(this, isChange: false);
        CurrentView = select;
        await select.LoadAsync();
    }

    private async Task SignOutAsync()
    {
        if (_dialer != null && _dialer.BlocksSignOut)
        {
            MessageBox.Show("Finish your call and save the disposition before signing out.",
                "CMX CallSuite Desktop v3", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show("Sign out of the dialer?", "CMX CallSuite Desktop v3",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await EndSessionAsync(null, callLogout: true);
    }

    public async Task EndSessionAsync(string? message, bool callLogout)
    {
        _dialer?.Dispose();
        _dialer = null;

        if (callLogout)
        {
            try { await Api.LogoutAsync(); }
            catch (Exception ex) { Log.Error("Logout failed", ex); }
        }

        StopRealtime();
        SessionActive = false;
        Agent = null;
        MyCampaigns = new();
        WorkingCampaigns = new();

        // Fresh client = fresh cookie jar, so nothing from the old session is reused.
        Api.Dispose();
        Api = new ApiClient(Settings.ServerUrl);

        var login = new LoginViewModel(this);
        if (message != null) login.Error = message;
        CurrentView = login;
    }

    private void StopRealtime()
    {
        if (Socket != null)
        {
            Socket.MessageReceived -= OnSocketMessage;
            Socket.ConnectionChanged -= OnSocketConnectionChanged;
            Socket.SessionRejected -= OnSocketSessionRejected;
            Socket.Dispose();
            Socket = null;
        }
        Phone.Stop();
        _tunnel?.Dispose();
        _tunnel = null;
        SipRegistered = false;
        _sipCredentials = null;
    }

    // ---------------------------------------------------------------- window lifetime

    /// <summary>The dialer window can't be closed while the agent is signed in to a session.</summary>
    public bool CanCloseWindow => !SessionActive;

    public void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (HasAgent)
        {
            try { Api.LogoutAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        }
        StopRealtime();
    }

    // ---------------------------------------------------------------- realtime events

    private void OnSocketMessage(JsonElement message) => Ui(() =>
    {
        if (message.Str("type") == "forceLogout")
        {
            var reason = message.Str("reason");
            var text = reason switch
            {
                "kicked_by_admin" => "An administrator has signed you out.",
                "session_timeout_12h" => "You've been signed out automatically after 12 hours. Please sign in again.",
                _ => "You've been signed out.",
            };
            _ = EndSessionAsync(text, callLogout: false);
            return;
        }

        _dialer?.HandleSocketMessage(message);
    });

    private void OnSocketConnectionChanged(bool connected) => Ui(() => _dialer?.SetServerConnected(connected));

    private void OnSocketSessionRejected(string reason) => Ui(() =>
        _ = EndSessionAsync("Your session ended. Please sign in again.", callLogout: false));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Ui(async () =>
        {
            if (!SessionActive) return;
            Log.Info("Resumed from sleep — reconnecting");
            Socket?.Start();
            try { await RegisterPhoneAsync(); }
            catch (Exception ex) { _dialer?.ShowError("Phone re-registration failed: " + ex.Message); }
        });
    }
}
