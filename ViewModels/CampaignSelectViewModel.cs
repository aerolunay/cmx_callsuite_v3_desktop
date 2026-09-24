using System.Collections.ObjectModel;
using System.Windows.Input;
using CmxDialer.Infrastructure;
using CmxDialer.Services;

namespace CmxDialer.ViewModels;

public sealed class CampaignChoice : ObservableObject
{
    private readonly CampaignSelectViewModel _owner;
    private bool _isSelected;

    public CampaignChoice(CampaignSelectViewModel owner, Campaign campaign, bool selected)
    {
        _owner = owner;
        Campaign = campaign;
        _isSelected = selected;
    }

    public Campaign Campaign { get; }
    public string Name => Campaign.DisplayName;
    public bool IsOutbound => !Campaign.IsBlended;
    public string TypeLabel => Campaign.IsBlended ? "Blended · inbound + outbound" : Campaign.IsAutoDial ? "Outbound · auto-dial" : "Outbound";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
            {
                _owner.OnChoiceChanged(this);
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}

/// <summary>
/// Mirrors the backend's POST /dialer/working-campaigns rules so agents see them up front:
/// one campaign unless multi_campaign_enabled, and an OUTBOUND campaign is always worked alone.
/// (The server still enforces these.)
/// </summary>
public sealed class CampaignSelectViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly bool _isChange;
    private bool _isLoading = true;
    private bool _busy;
    private string? _error;

    public CampaignSelectViewModel(MainViewModel main, bool isChange)
    {
        _main = main;
        _isChange = isChange;
        ContinueCommand = new AsyncCommand(ContinueAsync, () => !Busy && Choices.Any(c => c.IsSelected));
        CancelCommand = new AsyncCommand(() => _main.CancelCampaignChangeAsync(), () => !Busy && _isChange);
        RetryCommand = new AsyncCommand(LoadAsync, () => !Busy);
    }

    public ObservableCollection<CampaignChoice> Choices { get; } = new();

    public bool MultiSelect => _main.Agent?.MultiCampaignEnabled == true;
    public bool IsChange => _isChange;

    public string Instruction => MultiSelect
        ? "Choose the campaigns you're working. An outbound campaign has to be worked on its own."
        : "Choose the campaign you're working.";

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool IsEmpty => !IsLoading && Error == null && Choices.Count == 0;

    public ICommand ContinueCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var campaigns = await _main.Api.GetMyCampaignsAsync();
            var working = await _main.Api.GetWorkingCampaignIdsAsync();
            _main.MyCampaigns = campaigns;

            Choices.Clear();
            foreach (var c in campaigns)
                Choices.Add(new CampaignChoice(this, c, working.Contains(c.CampaignId)));

            // If the saved selection breaks the rules (e.g. settings changed), keep only the first.
            var selected = Choices.Where(c => c.IsSelected).ToList();
            if (selected.Count > 1 && (!MultiSelect || selected.Any(c => c.IsOutbound)))
                foreach (var extra in selected.Skip(1)) extra.IsSelected = false;

            if (Choices.Count == 1) Choices[0].IsSelected = true;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Notify(nameof(IsEmpty));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal void OnChoiceChanged(CampaignChoice changed)
    {
        if (!changed.IsSelected) return;
        foreach (var other in Choices)
        {
            if (ReferenceEquals(other, changed) || !other.IsSelected) continue;
            var mustDeselect = !MultiSelect || changed.IsOutbound || other.IsOutbound;
            if (mustDeselect) other.IsSelected = false;
        }
    }

    private async Task ContinueAsync()
    {
        var chosen = Choices.Where(c => c.IsSelected).Select(c => c.Campaign).ToList();
        if (chosen.Count == 0) return;

        Error = null;
        Busy = true;
        try
        {
            await _main.Api.SetWorkingCampaignsAsync(chosen.Select(c => c.CampaignId));
            Busy = false;
            await _main.OnCampaignsChosenAsync(chosen, _isChange);
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
