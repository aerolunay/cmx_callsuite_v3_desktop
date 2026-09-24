using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CmxDialer.Infrastructure;
using CmxDialer.Services;

namespace CmxDialer.ViewModels;

public sealed record CampaignFilter(string? CampaignId, string Label);

public sealed class CallbackItem : ObservableObject
{
    private bool _isPlaying;

    public CallbackItem(CallbackRow row) => Row = row;

    public CallbackRow Row { get; }
    public bool IsVoicemail => Row.IsVoicemail;
    public string TypeLabel => IsVoicemail ? "VOICEMAIL" : "ABANDONED";
    public string BadgeBackground => IsVoicemail ? "#E0F5FD" : "#FFF4EC";
    public string BadgeForeground => IsVoicemail ? "#0079A8" : "#8A3F0B";
    public string NumberText => Format.Phone(Row.CallerIdNumber);
    public string TimeText => Format.TimeOfDay(Row.Timestamp);

    public string DetailText
    {
        get
        {
            var parts = new List<string> { Row.CampaignName ?? Row.CampaignId ?? "" };
            if (IsVoicemail)
            {
                if (Row.DurationSeconds is > 0) parts.Add($"{Format.Seconds(Row.DurationSeconds)} message");
                if (Row.IsAfterHours) parts.Add("after hours");
            }
            else if (Row.WaitSeconds is > 0)
            {
                parts.Add($"waited {Format.Seconds(Row.WaitSeconds)}");
            }
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public bool CanPlay => IsVoicemail && Row.HasRecording && Row.VoicemailLogId != null;

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) Notify(nameof(IsNotPlaying)); }
    }

    public bool IsNotPlaying => !IsPlaying;
}

/// <summary>
/// "Callbacks" page — today's abandoned calls and voicemails still in NEW status for the
/// agent's campaigns (GET /dialer/abandoned-voicemail), filterable by campaign.
/// "Call back" uses the same CALLBACK call type as the web dialer, so saving the
/// disposition afterwards clears the entry server-side.
/// </summary>
public sealed class CallbacksViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly DialerViewModel _dialer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly VoicemailPlayer _player = new();
    private CampaignFilter? _selectedFilter;
    private bool _isLoading;
    private string? _error;
    private CallbackItem? _playing;
    private bool _loadedOnce;

    public CallbacksViewModel(MainViewModel main, DialerViewModel dialer)
    {
        _main = main;
        _dialer = dialer;

        Filters.Add(new CampaignFilter(null, "All my campaigns"));
        var campaigns = main.MyCampaigns.Count > 0 ? main.MyCampaigns : main.WorkingCampaigns;
        foreach (var c in campaigns) Filters.Add(new CampaignFilter(c.CampaignId, c.DisplayName));

        // Default to the campaign being worked when there's exactly one.
        _selectedFilter = main.WorkingCampaigns.Count == 1
            ? Filters.FirstOrDefault(f => f.CampaignId == main.WorkingCampaigns[0].CampaignId) ?? Filters[0]
            : Filters[0];

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        CallBackCommand = new AsyncCommand(p => CallBackAsync(p as CallbackItem), p => p is CallbackItem && _dialer.CanDial);
        PlayCommand = new AsyncCommand(p => TogglePlayAsync(p as CallbackItem), p => p is CallbackItem { CanPlay: true });

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
    }

    public ObservableCollection<CampaignFilter> Filters { get; } = new();
    public ObservableCollection<CallbackItem> Items { get; } = new();

    public CampaignFilter? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value == null || !Set(ref _selectedFilter, value)) return;
            _ = RefreshAsync();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (Set(ref _isLoading, value)) { Notify(nameof(IsEmpty)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string? Error { get => _error; private set { if (Set(ref _error, value)) Notify(nameof(IsEmpty)); } }

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => _loadedOnce && !IsLoading && Error == null && Items.Count == 0;

    public ICommand RefreshCommand { get; }
    public ICommand CallBackCommand { get; }
    public ICommand PlayCommand { get; }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            var rows = await _main.Api.GetAbandonedVoicemailAsync(SelectedFilter?.CampaignId);
            var playingId = _playing?.Row.VoicemailLogId;

            Items.Clear();
            foreach (var row in rows)
            {
                var item = new CallbackItem(row);
                if (playingId != null && row.VoicemailLogId == playingId)
                {
                    item.IsPlaying = true;
                    _playing = item;
                }
                Items.Add(item);
            }
            _loadedOnce = true;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Notify(nameof(Count), nameof(HasItems), nameof(IsEmpty));
        }
    }

    private async Task CallBackAsync(CallbackItem? item)
    {
        if (item == null) return;
        StopPlayback();
        await _dialer.PlaceCallbackAsync(item.Row);
    }

    private async Task TogglePlayAsync(CallbackItem? item)
    {
        if (item?.Row.VoicemailLogId is not long id) return;

        if (item.IsPlaying)
        {
            StopPlayback();
            return;
        }

        StopPlayback();
        try
        {
            var url = await _main.Api.GetVoicemailPlaybackUrlAsync(id);
            item.IsPlaying = true;
            _playing = item;
            await _player.PlayAsync(url, () => Ui(() =>
            {
                item.IsPlaying = false;
                if (ReferenceEquals(_playing, item)) _playing = null;
            }));
        }
        catch (Exception ex)
        {
            item.IsPlaying = false;
            _playing = null;
            Log.Error("Voicemail playback failed", ex);
            Error = ex is ApiException ? ex.Message : "Couldn't play this voicemail.";
        }
    }

    public void StopPlayback()
    {
        _player.Stop();
        if (_playing != null) _playing.IsPlaying = false;
        _playing = null;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _player.Dispose();
    }
}
