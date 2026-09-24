using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CmxDialer.Infrastructure;
using CmxDialer.Services;

namespace CmxDialer.ViewModels;

/// <summary>
/// The fixed-size dialer: aux status, SIP phone states, Line 2 and the disposition form.
/// All call control goes through the existing backend endpoints; the local SIP phone only
/// carries audio (auto-answer, mute, DTMF).
/// </summary>
public sealed class DialerViewModel : ObservableObject, IDisposable
{
    public static readonly HashSet<string> SystemStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "IN_CALL", "ON_HOLD", "MICROSIP_OUTBOUND", "AFTER_CALL_WORK",
    };

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _lineTwoPoll;
    private readonly Stopwatch _statusWatch = new();
    private readonly Dictionary<string, CampaignDispositions> _dispositionCache = new();
    private bool _disposed;

    // status
    private string _status = "";
    private int _statusBaseSeconds;
    private StatusOption? _selectedStatus;

    // calls
    private OutboundCall? _call;
    private InboundCall? _inbound;
    private bool _busy;
    private bool _hasLeads = true;
    private DateTime _lastLeadCheck = DateTime.MinValue;
    private bool _autoDialInFlight;
    private DateTime _nextAutoDialAt = DateTime.MinValue;
    private bool _serverConnected = true;
    private string _manualNumber = "";
    private bool _keypadOpen;
    private string _dtmfSent = "";
    private bool _isMuted;

    // line 2
    private bool _lineTwoPanelOpen;
    private LineTwoStatus? _lineTwo;
    private bool _targetIsAgent;
    private string _targetNumber = "";
    private CampaignAgent? _selectedAgent;
    private string _lineTwoTargetLabel = "";
    private DateTime _lineTwoStarted;
    private string? _lineTwoError;

    // disposition form
    private DispositionItem? _selectedDisposition;
    private IReadOnlyList<DispositionOption> _dispositionOptions = Array.Empty<DispositionOption>();
    private bool _transferDetected;
    private const string TransferDisposition = "XFER_CONF";
    private string _comments = "";
    private DateOption? _callbackDate;
    private string? _callbackTime;
    private bool _setNotReadyAfterSave;
    private string _callbackNumber = "";

    // messages + tabs
    private string? _error;
    private string? _notice;
    private bool _showCallbacks;

    public DialerViewModel(MainViewModel main, List<Campaign> workingCampaigns, AgentStatusInfo status)
    {
        _main = main;
        WorkingCampaigns = workingCampaigns;

        ManualStatuses = new[]
        {
            new StatusOption("READY", "Ready"),
            new StatusOption("NOT_READY", "Not Ready"),
            new StatusOption("AD_HOC", "Ad-Hoc"),
            new StatusOption("LUNCH_BREAK", "Lunch/Break"),
            new StatusOption("BIO_BREAK", "Bio-Break"),
            new StatusOption("ADMIN", "Admin"),
            new StatusOption("MEETING", "Meeting"),
            new StatusOption("TRAINING", "Training"),
        };

        CallbackDates = Enumerable.Range(0, 30)
            .Select(i => DateTime.Today.AddDays(i))
            .Select(d => new DateOption(d, DayLabel(d)))
            .ToList();
        CallbackTimes = Enumerable.Range(0, 13 * 4) // 8:00 AM → 8:45 PM in 15-minute steps
            .Select(i => DateTime.Today.AddHours(8).AddMinutes(15 * i).ToString("h:mm tt", CultureInfo.InvariantCulture))
            .ToList();

        Callbacks = new CallbacksViewModel(main, this);

        // commands
        DialNextCommand = new AsyncCommand(DialNextAsync, () => CanDial && ShowDialNext);
        ManualDialCommand = new AsyncCommand(ManualDialAsync, () => CanDial && Format.Digits(ManualNumber).Length >= 7);
        HangUpCommand = new AsyncCommand(HangUpAsync, () => IsCallLive);
        ToggleMuteCommand = new AsyncCommand(ToggleMuteAsync, () => IsCallLive && _main.Phone.IsInCall);
        ToggleHoldCommand = new AsyncCommand(ToggleHoldAsync, () => IsCallLive && !Busy && !LineTwoActive);
        ToggleKeypadCommand = new RelayCommand(() => { KeypadOpen = !KeypadOpen; RefreshAll(); }, () => IsCallLive);
        DtmfCommand = new AsyncCommand(p => SendDtmfAsync(p as string), _ => IsCallLive);
        OpenLineTwoCommand = new AsyncCommand(OpenLineTwoAsync, () => IsCallLive && !IsConnecting && !Busy);
        CloseLineTwoCommand = new RelayCommand(() => { LineTwoPanelOpen = false; LineTwoError = null; RefreshAll(); }, () => !Busy);
        SetTargetModeCommand = new RelayCommand(p => { TargetIsAgent = (p as string) == "agent"; RefreshAll(); });
        CallLineTwoCommand = new AsyncCommand(CallLineTwoAsync, () => IsCallLive && !Busy && HasLineTwoTarget);
        BlindTransferCommand = new AsyncCommand(BlindTransferAsync, () => IsCallLive && !Busy && HasLineTwoTarget);
        CompleteTransferCommand = new AsyncCommand(() => CompleteLineTwoAsync("transfer"), () => LineTwoActive && !Busy);
        ConferenceCommand = new AsyncCommand(() => CompleteLineTwoAsync("conference"), () => LineTwoActive && !Busy);
        SwitchLineCommand = new AsyncCommand(SwitchLineAsync, () => LineTwoActive && !Busy && _lineTwo?.Line2HasConnected == true);
        EndLineTwoCommand = new AsyncCommand(EndLineTwoAsync, () => LineTwoActive && !Busy);
        SaveDispositionCommand = new AsyncCommand(SaveDispositionAsync, () => CanSave);
        ShowPhoneTabCommand = new RelayCommand(() => ShowCallbacks = false);
        ShowCallbacksTabCommand = new AsyncCommand(async () => { ShowCallbacks = true; await Callbacks.RefreshAsync(); }, () => !IsCallLive);
        ChangeCampaignCommand = new AsyncCommand(() => _main.ChangeCampaignsAsync(), () => CanChangeCampaign);
        DismissMessageCommand = new RelayCommand(() => { Error = null; Notice = null; });

        ApplyStatus(status.Status, status.ElapsedSeconds);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();

        _lineTwoPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _lineTwoPoll.Tick += async (_, _) => await RefreshLineTwoAsync();

    }

    private static string DayLabel(DateTime d) =>
        d == DateTime.Today ? "Today"
        : d == DateTime.Today.AddDays(1) ? "Tomorrow"
        : d.ToString("ddd, MMM d", CultureInfo.CurrentCulture);

    // ================================================================= startup

    public async Task InitializeAsync()
    {
        // Idle view shows the working campaign's list (disabled) until a call picks the real one.
        var first = WorkingCampaigns.FirstOrDefault();
        if (first != null) await LoadDispositionsAsync(first.CampaignId, inbound: first.IsBlended);

        // Restore an in-progress call (e.g. the app was restarted mid-call).
        try
        {
            var current = await _main.Api.GetCurrentCallAsync();
            if (current?.CallId != null)
            {
                _call = new OutboundCall
                {
                    CallId = current.CallId,
                    Room = current.Room ?? "",
                    CampaignId = current.CampaignId ?? first?.CampaignId ?? "",
                    Lead = current.Lead ?? new JsonObject { ["lead_id"] = 0, ["phone_number"] = current.PhoneNumber ?? "" },
                    Status = current.Status ?? "customer_connected",
                    OnHold = current.OnHold,
                    StartedLocal = current.StartedAt?.LocalDateTime ?? DateTime.Now,
                    CustomerConnectedSeen = current.Status == "customer_connected",
                };
                await LoadDispositionsAsync(_call.CampaignId, inbound: false);
            }
            else
            {
                var inbound = await _main.Api.GetCurrentInboundCallAsync();
                if (inbound?.CallId != null)
                {
                    _inbound = new InboundCall
                    {
                        CallId = inbound.CallId,
                        CampaignId = inbound.CampaignId,
                        Room = inbound.Room ?? "",
                        CallerIdNumber = inbound.CallerIdNumber ?? "",
                        Status = inbound.Status ?? "agent_connected",
                        OnHold = inbound.OnHold,
                        AgentConnectedSeen = inbound.Status == "agent_connected",
                    };
                    await LoadDispositionsAsync(_inbound.CampaignId, inbound: true);
                }
            }
        }
        catch (ApiException ex)
        {
            Log.Error("Restoring current call failed", ex);
        }

        if (_status == "AFTER_CALL_WORK" && !HasCall)
            Error = "Your last call still needs a disposition, but it can't be restored here. Ask a supervisor to clear it.";

        await CheckLeadsAsync(force: true);
        _ = Callbacks.RefreshAsync();
        RefreshAll();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clock.Stop();
        _lineTwoPoll.Stop();
        Callbacks.Dispose();
    }

    // ================================================================= campaign

    public List<Campaign> WorkingCampaigns { get; }

    /// <summary>Same rule as DialerPage.jsx: lead-dialing only when exactly one campaign is worked.</summary>
    public Campaign? OutboundCampaign => WorkingCampaigns.Count == 1 ? WorkingCampaigns[0] : null;

    public string CampaignSummary => string.Join(", ", WorkingCampaigns.Select(c => c.DisplayName));

    public bool CanChangeCampaign => _status == "NOT_READY" && !HasCall && !Busy;

    // ================================================================= status

    public IReadOnlyList<StatusOption> ManualStatuses { get; }

    public string Status => _status;
    public string AgentName => _main.AgentName;

    
    // Header indicator: shows the aux status while the phone is registered,
    // "Not Registered" (red) when it isn't — calls can't reach the agent then.
    public bool PhoneRegistered => _main.SipRegistered;
    public string HeaderStatusText => PhoneRegistered ? StatusLabel : "Not Registered";
    public string HeaderStatusColor => PhoneRegistered ? StatusColor : "#C0392B";
    public string HeaderStatusTooltip => PhoneRegistered
        ? "Phone registered"
        : "Phone not registered — calls can't reach you until it reconnects";

    public void OnRegistrationChanged() =>
        Notify(nameof(PhoneRegistered), nameof(HeaderStatusText), nameof(HeaderStatusColor), nameof(HeaderStatusTooltip));

    /// <summary>
    /// Read by the SIP phone (background thread) before auto-answering. The backend only
    /// routes calls to Ready agents, but a Line 2 transfer to an agent doesn't check status —
    /// so the phone itself also refuses calls unless the agent is Ready or already on a call.
    /// </summary>
    public bool AcceptsIncomingCalls =>
        _status is "READY" or "IN_CALL" or "ON_HOLD" || _call != null || _inbound != null;
    public bool IsSystemStatus => SystemStatuses.Contains(_status);
    public bool CanChangeStatus => !IsSystemStatus && !HasCall && !Busy;
    public string StatusLabel => StatusLabels.For(_status);

    public string StatusColor => _status switch
    {
        "READY" => "#1E7E34",
        "IN_CALL" => "#0090C7",
        "ON_HOLD" => "#B8560F",
        "AFTER_CALL_WORK" => "#B8560F",
        "NOT_READY" => "#8A97AD",
        _ => "#5A6A85",
    };

    public string StatusTimer => Format.LongClock(TimeSpan.FromSeconds(_statusBaseSeconds) + _statusWatch.Elapsed);

    public StatusOption? SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedStatus)) return;
            _selectedStatus = value;
            OnPropertyChanged();
            _ = ChangeStatusAsync(value);
        }
    }

    private void ApplyStatus(string status, int elapsedSeconds)
    {
        _status = status ?? "";
        _statusBaseSeconds = Math.Max(0, elapsedSeconds);
        _statusWatch.Restart();
        _selectedStatus = ManualStatuses.FirstOrDefault(s => s.Value == _status);

        // An inbound call that ended before the agent ever connected needs no disposition.
        if (_inbound is { IsLive: false, AgentConnectedSeen: false } && !SystemStatuses.Contains(_status))
            ClearCall();
    }

    private async Task ChangeStatusAsync(StatusOption option)
    {
        if (option.Value == _status) return;
        Error = null;
        Busy = true;
        try
        {
            var result = await _main.Api.SetStatusAsync(option.Value, WorkingCampaigns.FirstOrDefault()?.CampaignId);
            ApplyStatus(result.Status, result.ElapsedSeconds);
            if (_status == "READY") await CheckLeadsAsync(force: true);
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
            // Snap the dropdown back to the real status.
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _selectedStatus = ManualStatuses.FirstOrDefault(s => s.Value == _status);
                OnPropertyChanged(nameof(SelectedStatus));
            });
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    // ================================================================= call state

    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HasCall => _call != null || _inbound != null;
    public bool IsInbound => _inbound != null;
    public bool IsCallLive => (_call?.IsLive ?? false) || (_inbound?.IsLive ?? false);
    public bool IsWrapUp => HasCall && !IsCallLive;

    /// <summary>Agent's own phone leg not up yet — auto-answer in progress.</summary>
    public bool IsConnecting => (_call is { IsLive: true } c && c.Status == "ringing_agent")
                                || (_inbound is { IsLive: true } i && i.Status == "ringing_agent");

    public bool CanDial => _status == "READY" && !HasCall && !Busy && _serverConnected;

    /// <summary>Sign-out is blocked on a live call or while a disposition is still owed.</summary>
    public bool BlocksSignOut => HasCall || _status == "AFTER_CALL_WORK";

    public PhoneMode Mode
    {
        get
        {
            if (!HasCall) return PhoneMode.Idle;
            if (IsWrapUp) return PhoneMode.WrapUp;
            if (IsConnecting) return PhoneMode.Connecting;
            if (LineTwoActive) return PhoneMode.LineTwoActive;
            if (LineTwoPanelOpen) return PhoneMode.LineTwoSetup;
            if (KeypadOpen) return PhoneMode.Keypad;
            return PhoneMode.InCall;
        }
    }

    public bool ShowIdle => Mode == PhoneMode.Idle;
    public bool ShowConnecting => Mode == PhoneMode.Connecting;
    public bool ShowInCall => Mode == PhoneMode.InCall;
    public bool ShowKeypad => Mode == PhoneMode.Keypad;
    public bool ShowLineTwoSetup => Mode == PhoneMode.LineTwoSetup;
    public bool ShowLineTwoActive => Mode == PhoneMode.LineTwoActive;
    public bool ShowWrapUp => Mode == PhoneMode.WrapUp;

    public string ContactName
    {
        get
        {
            if (_inbound != null) return "Inbound caller";
            if (_call == null) return "";
            var name = $"{_call.FirstName} {_call.LastName}".Trim();
            if (name.Length > 0) return name;
            return _call.CallType == "CALLBACK" ? "Callback" : "Manual dial";
        }
    }

    public string ContactNumber => Format.Phone(_inbound?.CallerIdNumber ?? _call?.PhoneNumber);

    public string DirectionText => _inbound != null
        ? "Inbound"
        : _call?.CallType == "CALLBACK" ? "Callback" : "Outbound";

    public bool IsOnHold => _call?.OnHold ?? _inbound?.OnHold ?? false;

    public string CallStateText
    {
        get
        {
            if (IsOnHold) return "On hold";
            if (_inbound != null)
                return _inbound.Status == "agent_connected" ? $"Connected · {DirectionText}" : "Connecting…";
            return _call?.Status switch
            {
                "agent_connected" => "Dialing customer…",
                "ringing_customer" => "Ringing customer…",
                "customer_connected" => $"Connected · {DirectionText}",
                _ => "Connecting…",
            };
        }
    }

    public string CallStateColor => IsOnHold
        ? "#8A3F0B"
        : (_call?.Status == "customer_connected" || _inbound?.Status == "agent_connected") ? "#1E7E34" : "#0079A8";

    public string CallTimer
    {
        get
        {
            var started = _inbound?.StartedLocal ?? _call?.StartedLocal;
            if (started == null) return "";
            var ended = _inbound?.EndedLocal ?? _call?.EndedLocal ?? DateTime.Now;
            return Format.Clock(ended - started.Value);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => Set(ref _isMuted, value);
    }

    public bool KeypadOpen
    {
        get => _keypadOpen;
        private set { if (Set(ref _keypadOpen, value) && !value) DtmfSent = ""; }
    }

    public string DtmfSent { get => _dtmfSent; private set => Set(ref _dtmfSent, value); }

    public string ManualNumber
    {
        get => _manualNumber;
        set { if (Set(ref _manualNumber, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool ShowDialNext => OutboundCampaign is { IsBlended: false, IsAutoDial: false } && _hasLeads;
    public bool ShowAutoDialInfo => OutboundCampaign is { IsBlended: false, IsAutoDial: true } && _hasLeads;

    public string IdleMessage
    {
        get
        {
            if (!_serverConnected) return "Reconnecting to the dialer server…";
            if (_status != "READY") return "Set your status to Ready to take and place calls.";
            if (OutboundCampaign == null || OutboundCampaign.IsBlended) return "Waiting for inbound calls. You can also manual dial or work Callbacks.";
            if (!_hasLeads) return "No leads available right now — checking again automatically.";
            if (OutboundCampaign.IsAutoDial) return "Auto-dial is on — the next lead dials automatically.";
            return "Ready for the next lead.";
        }
    }

    // ================================================================= messages + tabs

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Notify(nameof(HasMessage), nameof(MessageText), nameof(MessageIsError)); }
    }

    public string? Notice
    {
        get => _notice;
        private set { if (Set(ref _notice, value)) Notify(nameof(HasMessage), nameof(MessageText), nameof(MessageIsError)); }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Error) || !string.IsNullOrEmpty(Notice);
    public string MessageText => Error ?? Notice ?? "";
    public bool MessageIsError => !string.IsNullOrEmpty(Error);

    public void ShowError(string message) => Error = message;

    public void ClearPhoneError()
    {
        if (Error?.StartsWith("Phone ", StringComparison.Ordinal) == true) Error = null;
    }

    public bool ShowCallbacks
    {
        get => _showCallbacks;
        set { if (Set(ref _showCallbacks, value)) Notify(nameof(ShowPhoneTab)); }
    }

    public bool ShowPhoneTab => !ShowCallbacks;

    public CallbacksViewModel Callbacks { get; }

    public void SetServerConnected(bool connected)
    {
        _serverConnected = connected;
        if (connected && Error == "Lost the live connection to the dialer — reconnecting…") Error = null;
        if (!connected) Error = "Lost the live connection to the dialer — reconnecting…";
        RefreshAll();
    }

    // ================================================================= commands

    public ICommand DialNextCommand { get; }
    public ICommand ManualDialCommand { get; }
    public ICommand HangUpCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleHoldCommand { get; }
    public ICommand ToggleKeypadCommand { get; }
    public ICommand DtmfCommand { get; }
    public ICommand OpenLineTwoCommand { get; }
    public ICommand CloseLineTwoCommand { get; }
    public ICommand SetTargetModeCommand { get; }
    public ICommand CallLineTwoCommand { get; }
    public ICommand BlindTransferCommand { get; }
    public ICommand CompleteTransferCommand { get; }
    public ICommand ConferenceCommand { get; }
    public ICommand SwitchLineCommand { get; }
    public ICommand EndLineTwoCommand { get; }
    public ICommand SaveDispositionCommand { get; }
    public ICommand ShowPhoneTabCommand { get; }
    public ICommand ShowCallbacksTabCommand { get; }
    public ICommand ChangeCampaignCommand { get; }
    public ICommand DismissMessageCommand { get; }

    // ================================================================= realtime

    public void HandleSocketMessage(JsonElement m)
    {
        switch (m.Str("type"))
        {
            case "agentStatus":
                ApplyStatus(m.Str("status"), m.Int("elapsedSeconds"));
                if (_status == "READY") _ = CheckLeadsAsync(force: true);
                RefreshAll();
                break;

            case "callStatus":
                if (_call == null || _call.CallId != m.Str("callId")) return;
                ApplyOutboundStatus(m.Str("status"), m.Bool("onHold"));
                break;

            case "callAutoResolved":
                if (_call == null || _call.CallId != m.Str("callId")) return;
                ClearCall();
                Notice = m.Str("outcome") switch
                {
                    "machine" => "Answering machine detected — moved to the next lead.",
                    "busy" => "Line was busy — moved to the next lead.",
                    _ => "No answer — moved to the next lead.",
                };
                RefreshAll();
                break;

            case "inboundCall":
                ApplyInbound(m);
                break;
        }
    }

    private void ApplyOutboundStatus(string status, bool onHold)
    {
        if (_call == null) return;
        _call.Status = status;
        _call.OnHold = onHold;

        if (status == "customer_connected" && !_call.CustomerConnectedSeen)
        {
            _call.CustomerConnectedSeen = true;
            _call.StartedLocal = DateTime.Now;
            Tones.Connected();
        }

        if (status == "ended")
        {
            _call.EndedLocal ??= DateTime.Now;
            CloseCallSidePanels();
            PreselectHungUp("CX_HUNG_UP");
        }
        RefreshAll();
    }

    private void ApplyInbound(JsonElement m)
    {
        var callId = m.Str("callId");
        var status = m.Str("status");

        if (_inbound == null || _inbound.CallId != callId)
        {
            // Only take calls that are actually being routed to us.
            if (status is not ("ringing_agent" or "agent_connected")) return;
            if (_call != null) return; // shouldn't happen — backend routes only to READY agents

            _inbound = new InboundCall { CallId = callId };
            ResetDispositionForm();
            _ = LoadDispositionsAsync(m.Str("campaignId"), inbound: true);
            ShowCallbacks = false;
            Callbacks.StopPlayback();
            _main.RaiseAttention();
        }

        _inbound.Status = status;
        _inbound.OnHold = m.Bool("onHold");
        var campaignId = m.Str("campaignId");
        if (!string.IsNullOrEmpty(campaignId)) _inbound.CampaignId = campaignId;
        var room = m.Str("room");
        if (!string.IsNullOrEmpty(room)) _inbound.Room = room;
        var caller = m.Str("callerIdNumber");
        if (!string.IsNullOrEmpty(caller)) _inbound.CallerIdNumber = caller;

        if (status == "agent_connected" && !_inbound.AgentConnectedSeen)
        {
            _inbound.AgentConnectedSeen = true;
            _inbound.StartedLocal = DateTime.Now;
            Tones.Connected();
        }

        if (status == "ended")
        {
            _inbound.EndedLocal ??= DateTime.Now;
            CloseCallSidePanels();
            PreselectHungUp("CALLER_HUNG_UP");
        }
        RefreshAll();
    }

    public void OnPhoneAnswered() => RefreshAll();

    public void OnPhoneEnded()
    {
        IsMuted = false;
        RefreshAll();
    }

    // ================================================================= clock

    private void OnClockTick()
    {
        OnPropertyChanged(nameof(StatusTimer));
        if (HasCall) OnPropertyChanged(nameof(CallTimer));
        if (LineTwoActive) OnPropertyChanged(nameof(LineTwoTimer));

        // Auto-dial (RATIO campaigns) — same conditions as DialerPage.jsx.
        var campaign = OutboundCampaign;
        if (campaign is { IsAutoDial: true, IsBlended: false } && CanDial && _hasLeads
            && !_autoDialInFlight && DateTime.Now >= _nextAutoDialAt)
        {
            _autoDialInFlight = true;
            _nextAutoDialAt = DateTime.Now.AddSeconds(5); // never hammer the server if dialing keeps failing
            _ = DialNextAsync().ContinueWith(_ => Ui(() => _autoDialInFlight = false));
        }

        // Self-heal "no leads" every 20s while Ready.
        if (!_hasLeads && _status == "READY" && DateTime.Now - _lastLeadCheck > TimeSpan.FromSeconds(20))
            _ = CheckLeadsAsync(force: false);
    }

    private async Task CheckLeadsAsync(bool force)
    {
        var campaign = OutboundCampaign;
        if (campaign == null || campaign.IsBlended) return;
        if (!force && DateTime.Now - _lastLeadCheck < TimeSpan.FromSeconds(20)) return;
        _lastLeadCheck = DateTime.Now;
        try
        {
            _hasLeads = await _main.Api.HasLeadsAsync(campaign.CampaignId);
        }
        catch
        {
            _hasLeads = true; // fail open, like the web app
        }
        RefreshAll();
    }

    // ================================================================= dialing

    private async Task DialNextAsync()
    {
        var campaign = OutboundCampaign;
        if (campaign == null || !CanDial) return;

        Error = null;
        Notice = null;
        Busy = true;
        try
        {
            var lead = await _main.Api.NextLeadAsync(campaign.CampaignId);
            var result = await _main.Api.StartCallAsync(campaign.CampaignId, lead.Long("lead_id"), lead.Str("phone_number"), lead, "REGULAR");
            BeginOutboundCall(result, campaign.CampaignId, lead, "REGULAR", null, null);
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            _hasLeads = false;
        }
        catch (ApiException ex) when (ex.Status == 403)
        {
            _hasLeads = false;
            Error = ex.Message; // outside calling hours
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    private async Task ManualDialAsync()
    {
        var number = Format.Digits(ManualNumber);
        var campaign = OutboundCampaign ?? WorkingCampaigns.FirstOrDefault();
        if (campaign == null || !CanDial) return;

        Error = null;
        Notice = null;
        Busy = true;
        try
        {
            string callType = "REGULAR";
            string? sourceType = null;
            long? sourceId = null;

            var pending = await _main.Api.CheckCallbackPendingAsync(number);
            if (pending != null)
            {
                var kind = pending.Type == "voicemail" ? "voicemail" : "abandoned call";
                var answer = MessageBox.Show(
                    $"This number has an open {kind} waiting in Callbacks.\n\nYes — call it as that callback (it's cleared when you save the disposition)\nNo — call it as a regular manual dial",
                    "Pending callback", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes)
                {
                    callType = "CALLBACK";
                    sourceType = pending.Type;
                    sourceId = pending.SourceId;
                }
            }

            var lead = new JsonObject { ["lead_id"] = 0, ["first_name"] = "", ["last_name"] = "", ["phone_number"] = number };
            var result = await _main.Api.StartCallAsync(campaign.CampaignId, 0, number, lead, callType);
            BeginOutboundCall(result, campaign.CampaignId, lead, callType, sourceType, sourceId);
            ManualNumber = "";
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    /// <summary>Called from the Callbacks page — same flow as handleAbandonedVoicemailCallback.</summary>
    public async Task PlaceCallbackAsync(CallbackRow row)
    {
        if (_status != "READY") { Error = "You must be Ready to place a callback."; return; }
        if (HasCall) { Error = "You're already on a call."; return; }
        if (string.IsNullOrEmpty(row.CampaignId)) { Error = "This entry has no campaign on file."; return; }

        Error = null;
        Notice = null;
        Busy = true;
        try
        {
            var number = row.CallerIdNumber ?? "";
            var lead = new JsonObject { ["lead_id"] = 0, ["first_name"] = "", ["last_name"] = "", ["phone_number"] = number };
            var result = await _main.Api.StartCallAsync(row.CampaignId!, 0, number, lead, "CALLBACK");
            BeginOutboundCall(result, row.CampaignId!, lead, "CALLBACK", row.Type, row.SourceId);
            ShowCallbacks = false;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    private void BeginOutboundCall(StartCallResult result, string campaignId, JsonObject lead, string callType, string? sourceType, long? sourceId)
    {
        _call = new OutboundCall
        {
            CallId = result.CallId ?? "",
            Room = result.Room ?? "",
            CampaignId = campaignId,
            Lead = lead,
            CallType = callType,
            CallbackSourceType = sourceType,
            CallbackSourceId = sourceId,
            Status = "ringing_agent",
        };
        ResetDispositionForm();
        _ = LoadDispositionsAsync(campaignId, inbound: false);
        RefreshAll();
    }

    // ================================================================= in-call controls

    private async Task HangUpAsync()
    {
        Error = null;
        try
        {
            if (_call != null) await _main.Api.EndCallAsync(_call.CallId);
            else if (_inbound != null) await _main.Api.EndInboundCallAsync(_inbound.CallId);
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
            _main.Phone.HangUp(); // make sure the agent is at least off the line
        }
    }

    private async Task ToggleMuteAsync()
    {
        await _main.Phone.SetMutedAsync(!IsMuted);
        IsMuted = _main.Phone.IsMuted;
    }

    private async Task ToggleHoldAsync()
    {
        Error = null;
        Busy = true;
        try
        {
            if (_call != null) _call.OnHold = await _main.Api.HoldCallAsync(_call.CallId, !_call.OnHold);
            else if (_inbound != null) _inbound.OnHold = await _main.Api.HoldInboundAsync(_inbound.CallId, !_inbound.OnHold);
        }
        catch (ApiException ex) { Error = ex.Message; }
        finally { Busy = false; RefreshAll(); }
    }

    private async Task SendDtmfAsync(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        await _main.Phone.SendDtmfAsync(key[0]);
        DtmfSent = (DtmfSent + key).Length > 18 ? (DtmfSent + key)[^18..] : DtmfSent + key;
    }

    private void CloseCallSidePanels()
    {
        KeypadOpen = false;
        LineTwoPanelOpen = false;
        _lineTwo = null;
        _lineTwoPoll.Stop();
        LineTwoError = null;
    }

    // ================================================================= line 2

    public bool LineTwoPanelOpen { get => _lineTwoPanelOpen; private set => Set(ref _lineTwoPanelOpen, value); }
    public bool LineTwoActive => _lineTwo?.Active == true;

    public bool TargetIsAgent
    {
        get => _targetIsAgent;
        set { if (Set(ref _targetIsAgent, value)) { Notify(nameof(TargetIsNumber)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool TargetIsNumber => !TargetIsAgent;

    public string TargetNumber
    {
        get => _targetNumber;
        set { if (Set(ref _targetNumber, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public ObservableCollection<CampaignAgent> Agents { get; } = new();

    public CampaignAgent? SelectedAgent
    {
        get => _selectedAgent;
        set { if (Set(ref _selectedAgent, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private bool HasLineTwoTarget => TargetIsAgent
        ? !string.IsNullOrWhiteSpace(SelectedAgent?.Extension)
        : Format.Digits(TargetNumber).Length >= 3;

    public string? LineTwoError
    {
        get => _lineTwoError;
        private set { if (Set(ref _lineTwoError, value)) Notify(nameof(HasLineTwoError)); }
    }

    public bool HasLineTwoError => !string.IsNullOrEmpty(LineTwoError);

    public string LineOneStateText => _lineTwo?.Line1OnHold == true || IsOnHold
        ? "On hold · hearing music"
        : _lineTwo?.ActiveLine == 1 ? "Talking" : "Waiting";

    public bool LineOneHeld => _lineTwo?.Line1OnHold == true || IsOnHold;
    public bool LineTwoIsCurrent => _lineTwo?.ActiveLine == 2;

    public string LineTwoTargetLabel => _lineTwoTargetLabel;

    public string LineTwoStateText
    {
        get
        {
            if (_lineTwo == null) return "";
            if (_lineTwo.Status == "failed") return "Didn't connect";
            if (!_lineTwo.Line2HasConnected) return "Ringing…";
            if (_lineTwo.Line2OnHold) return "On hold";
            return _lineTwo.ActiveLine == 2 ? "Talking privately" : "Waiting";
        }
    }

    public string LineTwoTimer => LineTwoActive ? Format.Clock(DateTime.Now - _lineTwoStarted) : "";

    public string SwitchLineText => _lineTwo?.ActiveLine == 2 ? "Switch to Line 1" : "Switch to Line 2";

    private async Task OpenLineTwoAsync()
    {
        LineTwoError = null;
        KeypadOpen = false;
        LineTwoPanelOpen = true;
        RefreshAll();

        var campaignId = _call?.CampaignId ?? _inbound?.CampaignId ?? WorkingCampaigns.FirstOrDefault()?.CampaignId;
        if (string.IsNullOrEmpty(campaignId)) return;
        try
        {
            var agents = await _main.Api.GetCampaignAgentsAsync(campaignId);
            Agents.Clear();
            foreach (var a in agents.OrderByDescending(a => a.Status == "READY").ThenBy(a => a.FullName)) Agents.Add(a);
        }
        catch (ApiException ex) { LineTwoError = ex.Message; }
    }

    private (string target, bool isExtension, string label) LineTwoTarget() => TargetIsAgent
        ? (SelectedAgent!.Extension!, true, SelectedAgent.FullName)
        : (Format.Digits(TargetNumber), false, Format.Phone(TargetNumber));

    private async Task CallLineTwoAsync()
    {
        var (target, isExtension, label) = LineTwoTarget();
        LineTwoError = null;
        Busy = true;
        try
        {
            await _main.Api.StartLineTwoAsync(target, isExtension);
            _lineTwoTargetLabel = label;
            _lineTwoStarted = DateTime.Now;
            await RefreshLineTwoAsync();
            _lineTwoPoll.Start();
            TargetNumber = "";
        }
        catch (ApiException ex) { LineTwoError = ex.Message; }
        finally { Busy = false; RefreshAll(); }
    }

    private async Task BlindTransferAsync()
    {
        var (target, isExtension, label) = LineTwoTarget();
        if (MessageBox.Show($"Send this call to {label} and leave it?", "Blind transfer",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        LineTwoError = null;
        Busy = true;
        try
        {
            await _main.Api.TransferBlindAsync(target, isExtension);
            LineTwoPanelOpen = false;
            MarkTransferDetected();
            Notice = $"Transferred to {label}.";
        }
        catch (ApiException ex) { LineTwoError = ex.Message; }
        finally { Busy = false; RefreshAll(); }
    }

    private async Task CompleteLineTwoAsync(string action)
    {
        LineTwoError = null;
        Busy = true;
        try
        {
            await _main.Api.CompleteLineTwoAsync(action);
            _lineTwo = null;
            _lineTwoPoll.Stop();
            LineTwoPanelOpen = false;
            MarkTransferDetected();
            Notice = action == "transfer" ? $"Transferred to {_lineTwoTargetLabel}." : "Conference started — everyone is on the call.";
        }
        catch (ApiException ex)
        {
            LineTwoError = ex.Message;
            if (ex.Reason == "customer_disconnected")
            {
                _lineTwo = null;
                _lineTwoPoll.Stop();
                LineTwoPanelOpen = false;
                Error = ex.Message;
            }
        }
        finally { Busy = false; RefreshAll(); }
    }

    /// <summary>Backend requires the line you're leaving to be held first (switchToLineOne/Two).</summary>
    private async Task SwitchLineAsync()
    {
        if (_lineTwo == null) return;
        LineTwoError = null;
        Busy = true;
        try
        {
            if (_lineTwo.ActiveLine == 2)
            {
                if (_lineTwo.Line2HasConnected && !_lineTwo.Line2OnHold) await _main.Api.HoldLineTwoAsync(true);
                await _main.Api.SwitchLineAsync(1);
            }
            else
            {
                if (!IsOnHold)
                {
                    if (_call != null) _call.OnHold = await _main.Api.HoldCallAsync(_call.CallId, true);
                    else if (_inbound != null) _inbound.OnHold = await _main.Api.HoldInboundAsync(_inbound.CallId, true);
                }
                await _main.Api.SwitchLineAsync(2);
                if (_lineTwo.Line2OnHold) await _main.Api.HoldLineTwoAsync(false);
            }
            await RefreshLineTwoAsync();
        }
        catch (ApiException ex) { LineTwoError = ex.Message; }
        finally { Busy = false; RefreshAll(); }
    }

    private async Task EndLineTwoAsync()
    {
        LineTwoError = null;
        Busy = true;
        try
        {
            await _main.Api.CancelLineTwoAsync();
            _lineTwo = null;
            _lineTwoPoll.Stop();
            LineTwoPanelOpen = false;
        }
        catch (ApiException ex) { LineTwoError = ex.Message; }
        finally { Busy = false; RefreshAll(); }
    }

    private async Task RefreshLineTwoAsync()
    {
        if (!IsCallLive) { _lineTwoPoll.Stop(); return; }
        try
        {
            var status = await _main.Api.GetLineTwoStatusAsync();
            _lineTwo = status.Active ? status : null;
            if (!status.Active) _lineTwoPoll.Stop();
            if (status.Status == "failed" && !string.IsNullOrEmpty(status.FailureReason))
                LineTwoError = $"Line 2 didn't connect: {status.FailureReason}. You can end Line 2 or try again.";

            // Keep the local hold flag in sync for Line 1.
            if (_call != null) _call.OnHold = status.Line1OnHold;
            else if (_inbound != null) _inbound.OnHold = status.Line1OnHold;
        }
        catch (ApiException)
        {
            // transient — next tick retries
        }
        RefreshAll();
    }

    // ================================================================= disposition

    public ObservableCollection<DispositionItem> Dispositions { get; } = new();
    public IReadOnlyList<DateOption> CallbackDates { get; }
    public IReadOnlyList<string> CallbackTimes { get; }

    public bool DispositionEnabled => HasCall && !IsConnecting;

    public string DispositionHint => _transferDetected ? "Transfer / conference detected" : Mode switch
    {
        PhoneMode.Idle => "Available once a call starts",
        PhoneMode.Connecting => "Available once connected",
        PhoneMode.WrapUp => "Required before next call",
        _ => "Pick now, save after hang-up",
    };

    public string DispositionHintColor => Mode == PhoneMode.WrapUp ? "#A12F23" : "#5A6A85";

    public bool NeedsCallbackTime => DispositionCatalog.NeedsCallbackTime(_selectedDisposition?.Value);
    public bool ShowCallbackNumber => IsInbound && NeedsCallbackTime;

    public string Comments
    {
        get => _comments;
        set { if (Set(ref _comments, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public DateOption? CallbackDate
    {
        get => _callbackDate;
        set { if (Set(ref _callbackDate, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string? CallbackTime
    {
        get => _callbackTime;
        set { if (Set(ref _callbackTime, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool SetNotReadyAfterSave
    {
        get => _setNotReadyAfterSave;
        set => Set(ref _setNotReadyAfterSave, value);
    }

    /// <summary>Backend requires exactly 10 digits (US, no country code) when given.</summary>
    public string CallbackNumber
    {
        get => _callbackNumber;
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).Take(10).ToArray());
            if (Set(ref _callbackNumber, digits)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanSave =>
        IsWrapUp && !Busy
        && _selectedDisposition != null
        && !string.IsNullOrWhiteSpace(Comments)
        && (!NeedsCallbackTime || (CallbackDate != null && CallbackTime != null))
        && (!ShowCallbackNumber || CallbackNumber.Length is 0 or 10);

    private void OnDispositionSelected(DispositionItem item)
    {
        _selectedDisposition = item;
        foreach (var other in Dispositions)
            if (!ReferenceEquals(other, item) && other.IsSelected) other.SetSelectedSilently(false);
        if (NeedsCallbackTime && CallbackDate == null) CallbackDate = CallbackDates[0];
        Notify(nameof(NeedsCallbackTime), nameof(ShowCallbackNumber));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>After a hang-up the likely disposition is pre-selected; the agent still confirms it.</summary>
    private void PreselectHungUp(string value)
    {
        if (_transferDetected || _selectedDisposition != null) return;
        var match = Dispositions.FirstOrDefault(d => d.Value == value);
        if (match != null) match.IsSelected = true;
    }

    private void ResetDispositionForm()
    {
        _transferDetected = false;
        _selectedDisposition = null;
        foreach (var d in Dispositions) d.SetSelectedSilently(false);
        Comments = "";
        CallbackDate = null;
        CallbackTime = null;
        CallbackNumber = "";
        SetNotReadyAfterSave = false;
        IsMuted = false;
    }

    private async Task LoadDispositionsAsync(string? campaignId, bool inbound)
    {
        // Show the fallback list immediately, then swap in the campaign's configured list.
        SetDispositionList(DispositionCatalog.For(campaignId, inbound));
        if (string.IsNullOrEmpty(campaignId)) return;

        try
        {
            if (!_dispositionCache.TryGetValue(campaignId, out var configured))
            {
                configured = await _main.Api.GetCampaignDispositionsAsync(campaignId);
                _dispositionCache[campaignId] = configured;
            }

            var list = inbound
                ? (configured.InboundEnabled && configured.Inbound.Count > 0 ? configured.Inbound : null)
                : (configured.OutboundEnabled && configured.Outbound.Count > 0 ? configured.Outbound : null);
            if (list != null) SetDispositionList(list);
        }
        catch (ApiException ex)
        {
            Log.Error("Loading campaign dispositions failed", ex);
        }
    }

    private void SetDispositionList(IReadOnlyList<DispositionOption> options)
    {
        _dispositionOptions = options;
        ApplyDispositionOptions();
    }

    /// <summary>
    /// "Transferred Call / Conference Call" is never offered as a choice: the app knows when a
    /// Line 2 transfer, conference or blind transfer actually happened. Once one does, it's the
    /// only disposition shown, already selected.
    /// </summary>
    private void ApplyDispositionOptions()
    {
        var keep = _selectedDisposition?.Value;
        _selectedDisposition = null;
        Dispositions.Clear();

        if (_transferDetected)
        {
            var xfer = _dispositionOptions.FirstOrDefault(o => o.Value == TransferDisposition)
                       ?? new DispositionOption(TransferDisposition, "Transferred Call / Conference Call");
            var item = new DispositionItem(xfer, OnDispositionSelected);
            Dispositions.Add(item);
            item.IsSelected = true;
        }
        else
        {
            foreach (var o in _dispositionOptions.Where(o => o.Value != TransferDisposition))
                Dispositions.Add(new DispositionItem(o, OnDispositionSelected));
            if (keep != null)
            {
                var again = Dispositions.FirstOrDefault(d => d.Value == keep);
                if (again != null) again.IsSelected = true;
            }
        }

        Notify(nameof(NeedsCallbackTime), nameof(ShowCallbackNumber), nameof(DispositionHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void MarkTransferDetected()
    {
        if (_transferDetected) return;
        _transferDetected = true;
        ApplyDispositionOptions();
    }

    private string? BuildCallbackAt()
    {
        if (!NeedsCallbackTime || CallbackDate == null || CallbackTime == null) return null;
        var time = DateTime.ParseExact(CallbackTime, "h:mm tt", CultureInfo.InvariantCulture);
        return $"{CallbackDate.Date:yyyy-MM-dd}T{time:HH:mm}"; // same shape as <input type="datetime-local">
    }

    private async Task SaveDispositionAsync()
    {
        var selected = _selectedDisposition;
        if (selected == null) return;

        Error = null;
        Busy = true;
        try
        {
            if (_call != null)
            {
                await _main.Api.SaveDispositionAsync(_call.CallId, new
                {
                    campaignId = _call.CampaignId,
                    leadId = _call.LeadId,
                    phoneNumber = _call.PhoneNumber,
                    firstName = _call.FirstName,
                    lastName = _call.LastName,
                    room = _call.Room,
                    disposition = selected.Value,
                    comments = Comments.Trim(),
                    callbackAt = BuildCallbackAt(),
                    setNotReady = SetNotReadyAfterSave,
                    callbackSourceType = _call.CallbackSourceType,
                    callbackSourceId = _call.CallbackSourceId,
                    dispositionLabel = selected.Label,
                });
            }
            else if (_inbound != null)
            {
                await _main.Api.SaveInboundDispositionAsync(new
                {
                    callId = _inbound.CallId,
                    callerIdNumber = _inbound.CallerIdNumber,
                    firstName = "",
                    lastName = "",
                    comments = Comments.Trim(),
                    disposition = selected.Value,
                    callbackAt = BuildCallbackAt(),
                    callbackNumber = ShowCallbackNumber && CallbackNumber.Length == 10 ? CallbackNumber : null,
                    setNotReady = SetNotReadyAfterSave,
                });
            }

            ClearCall();
            Notice = "Disposition saved.";
            _ = Callbacks.RefreshAsync(); // a saved callback disposition clears it from the list
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshAll();
        }
    }

    private void ClearCall()
    {
        _call = null;
        _inbound = null;
        CloseCallSidePanels();
        ResetDispositionForm();
        var first = WorkingCampaigns.FirstOrDefault();
        if (first != null) _ = LoadDispositionsAsync(first.CampaignId, inbound: first.IsBlended);
    }
}