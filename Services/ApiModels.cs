using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmxDialer.Infrastructure;

namespace CmxDialer.Services;

// Shapes match what the Node backend returns today (authRoutes.js / dialerRoutes.js).

/// <summary>req.session.agent — built by buildSessionAgent() in authRoutes.js.</summary>
public sealed class AgentInfo
{
    public int AppUserId { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? AccessLevel { get; set; }
    public string? Username { get; set; }
    /// <summary>ccNNN — resolved from vicidial_users.phone_login → phones.login.</summary>
    public string? Extension { get; set; }
    public string? Protocol { get; set; }
    public string? UserGroup { get; set; }
    public bool TotpEnabled { get; set; }
    public bool MultiCampaignEnabled { get; set; }
}

public sealed class Campaign
{
    [JsonPropertyName("campaign_id")] public string CampaignId { get; set; } = "";
    [JsonPropertyName("campaign_name")] public string? CampaignName { get; set; }
    [JsonPropertyName("campaign_cid")] [JsonConverter(typeof(FlexibleStringConverter))] public string? CampaignCid { get; set; }
    [JsonPropertyName("dial_method")] public string? DialMethod { get; set; }
    /// <summary>OUTBOUND or BLENDED (cmx_dialer.campaign_settings).</summary>
    [JsonPropertyName("campaign_type")] public string? CampaignType { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(CampaignName) ? CampaignId : CampaignName!;
    public bool IsBlended => string.Equals(CampaignType, "BLENDED", StringComparison.OrdinalIgnoreCase);
    public bool IsAutoDial => string.Equals(DialMethod, "RATIO", StringComparison.OrdinalIgnoreCase);
}

public sealed class AgentStatusInfo
{
    public string Status { get; set; } = "";
    public int ElapsedSeconds { get; set; }
    public string? RelatedCampaignId { get; set; }
}

public sealed class SipCredentials
{
    public string Extension { get; set; } = "";
    public string Password { get; set; } = "";
    public string WssUrl { get; set; } = "";
}

public sealed class StartCallResult
{
    [JsonConverter(typeof(FlexibleStringConverter))] public string? CallId { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? Room { get; set; }
}

/// <summary>dialerService.getCallStatus() — used by GET /dialer/current-call.</summary>
public sealed class CallInfo
{
    [JsonConverter(typeof(FlexibleStringConverter))] public string? CallId { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? Room { get; set; }
    public string? Status { get; set; }
    public string? CampaignId { get; set; }
    public JsonObject? Lead { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? PhoneNumber { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public bool OnHold { get; set; }
}

public sealed class InboundCallInfo
{
    [JsonConverter(typeof(FlexibleStringConverter))] public string? CallId { get; set; }
    public string? Status { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? Room { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? CallerIdNumber { get; set; }
    public bool OnHold { get; set; }
    public string? CampaignId { get; set; }
}

public sealed class DispositionOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    public DispositionOption() { }
    public DispositionOption(string value, string label) { Value = value; Label = label; }
}

public sealed class CampaignDispositions
{
    public bool InboundEnabled { get; set; }
    public bool OutboundEnabled { get; set; }
    public List<DispositionOption> Inbound { get; set; } = new();
    public List<DispositionOption> Outbound { get; set; } = new();
}

/// <summary>One row of GET /dialer/abandoned-voicemail.</summary>
public sealed class CallbackRow
{
    /// <summary>"abandoned" or "voicemail".</summary>
    public string Type { get; set; } = "";
    public long? AbandonedCallLogId { get; set; }
    public long? VoicemailLogId { get; set; }
    public string? CampaignId { get; set; }
    public string? CampaignName { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))] public string? CallerIdNumber { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public int? WaitSeconds { get; set; }
    public int? DurationSeconds { get; set; }
    public bool HasRecording { get; set; }
    public bool IsAfterHours { get; set; }
    public string? AbandonReason { get; set; }

    public bool IsVoicemail => string.Equals(Type, "voicemail", StringComparison.OrdinalIgnoreCase);
    public long? SourceId => IsVoicemail ? VoicemailLogId : AbandonedCallLogId;
}

public sealed class CallbackPending
{
    public string Type { get; set; } = "";
    public long? VoicemailLogId { get; set; }
    public long? AbandonedCallLogId { get; set; }
    public long? SourceId => string.Equals(Type, "voicemail", StringComparison.OrdinalIgnoreCase) ? VoicemailLogId : AbandonedCallLogId;
}

public sealed class LineTwoStatus
{
    public bool Active { get; set; }
    public int ActiveLine { get; set; } = 1;
    /// <summary>"ringing", "connected", "failed", …</summary>
    public string? Status { get; set; }
    public string? FailureReason { get; set; }
    public bool Line1OnHold { get; set; }
    public bool Line2OnHold { get; set; }
    public bool Line2HasConnected { get; set; }
}

public sealed class CampaignAgent
{
    public int AppUserId { get; set; }
    public string FullName { get; set; } = "";
    /// <summary>What the backend accepts as a transfer target when isExtension = true.</summary>
    [JsonConverter(typeof(FlexibleStringConverter))] public string? Extension { get; set; }
    public string Status { get; set; } = "";

    public string Display => $"{FullName} · {StatusLabels.For(Status)}";
}

public static class StatusLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["READY"] = "Ready",
        ["NOT_READY"] = "Not Ready",
        ["AD_HOC"] = "Ad-Hoc",
        ["LUNCH_BREAK"] = "Lunch/Break",
        ["BIO_BREAK"] = "Bio-Break",
        ["ADMIN"] = "Admin",
        ["MEETING"] = "Meeting",
        ["TRAINING"] = "Training",
        ["ON_HOLD"] = "On Hold",
        ["IN_CALL"] = "In Call",
        ["AFTER_CALL_WORK"] = "After Call Work",
        ["MICROSIP_OUTBOUND"] = "Direct Call",
        ["LOGGED_OUT"] = "Logged Out",
    };

    public static string For(string? status) =>
        status != null && Labels.TryGetValue(status, out var l) ? l : (status ?? "");
}
