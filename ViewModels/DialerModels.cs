using System.Text.Json.Nodes;
using CmxDialer.Infrastructure;
using CmxDialer.Services;

namespace CmxDialer.ViewModels;

public enum PhoneMode { Idle, Connecting, InCall, Keypad, LineTwoSetup, LineTwoActive, WrapUp }

public sealed record StatusOption(string Value, string Label);

public sealed record DateOption(DateTime Date, string Label);

/// <summary>An outbound call this app started (dial next, manual, callback).</summary>
public sealed class OutboundCall
{
    public string CallId { get; init; } = "";
    public string Room { get; init; } = "";
    public string CampaignId { get; init; } = "";
    public JsonObject Lead { get; init; } = new();
    /// <summary>REGULAR or CALLBACK.</summary>
    public string CallType { get; init; } = "REGULAR";
    public string? CallbackSourceType { get; init; }
    public long? CallbackSourceId { get; init; }

    public string Status { get; set; } = "ringing_agent";
    public bool OnHold { get; set; }
    public DateTime StartedLocal { get; set; } = DateTime.Now;
    public DateTime? EndedLocal { get; set; }
    public bool CustomerConnectedSeen { get; set; }

    public bool IsLive => Status != "ended";
    public long LeadId => Lead.Long("lead_id");
    public string PhoneNumber => Lead.Str("phone_number");
    public string FirstName => Lead.Str("first_name");
    public string LastName => Lead.Str("last_name");
}

/// <summary>An inbound call routed to this agent (WebSocket "inboundCall" messages).</summary>
public sealed class InboundCall
{
    public string CallId { get; init; } = "";
    public string? CampaignId { get; set; }
    public string Room { get; set; } = "";
    public string CallerIdNumber { get; set; } = "";
    public string Status { get; set; } = "ringing_agent";
    public bool OnHold { get; set; }
    public DateTime StartedLocal { get; set; } = DateTime.Now;
    public DateTime? EndedLocal { get; set; }
    public bool AgentConnectedSeen { get; set; }

    public bool IsLive => Status != "ended";
}

public sealed class DispositionItem : ObservableObject
{
    private readonly Action<DispositionItem> _onSelected;
    private bool _isSelected;

    public DispositionItem(DispositionOption option, Action<DispositionItem> onSelected)
    {
        Value = option.Value;
        Label = option.Label;
        _onSelected = onSelected;
    }

    public string Value { get; }
    public string Label { get; }
    public bool IsDanger => Value == "DO_NOT_CALL";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) _onSelected(this);
        }
    }

    internal void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}
