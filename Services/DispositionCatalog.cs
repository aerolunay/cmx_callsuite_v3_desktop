namespace CmxDialer.Services;

/// <summary>
/// Fallback lists, ported from frontend/src/constants/dispositions.js. Used only when the
/// campaign has no lists configured in cmx_dialer.campaign_dispositions (same rule as
/// DialerPage.jsx's effectiveInbound/OutboundDispositions).
/// </summary>
public static class DispositionCatalog
{
    private static readonly DispositionOption[] Outbound =
    {
        new("CALL_ENDED", "Call Ended"),
        new("CX_HUNG_UP", "CX Hung Up"),
        new("NO_ANSWER", "No Answer"),
        new("VOICEMAIL", "Voicemail"),
        new("WRONG_NUMBER", "Wrong Number"),
        new("NOT_INTERESTED", "Not Interested"),
        new("DO_NOT_CALL", "Do Not Call (DNC)"),
        new("CALLBACK", "Callback Requested"),
        new("XFER_CONF", "Transferred Call / Conference Call"),
    };

    private static readonly DispositionOption[] ScreeningOutbound =
        new[] { new DispositionOption("SCREENING_COMPLETED", "Screening Completed"), new DispositionOption("NOT_ELIGIBLE", "Not Eligible") }
        .Concat(Outbound).ToArray();

    private static readonly DispositionOption[] Inbound =
    {
        new("RESOLVED", "Resolved"),
        new("INFO_PROVIDED", "Information Provided"),
        new("TRANSFERRED", "Transferred"),
        new("CALLBACK_REQUESTED", "Callback Requested"),
        new("SALES_INQUIRY", "Sales Inquiry / New Lead"),
        new("COMPLAINT", "Complaint Logged"),
        new("WRONG_NUMBER", "Wrong Number / Misdial"),
        new("CALLER_HUNG_UP", "Caller Hung Up"),
        new("CALL_DISCONNECTED", "Call Disconnected"),
        new("GHOST_CALL", "Ghost Call"),
        new("XFER_CONF", "Transferred Call / Conference Call"),
    };

    private static readonly DispositionOption[] BsmscInbound =
    {
        new("SCREENING_COMPLETED", "Screening Completed"),
        new("UNABLE_TO_COMPLETE_SCREENING", "Unable to Complete Screening"),
        new("CALLER_HUNG_UP", "Caller Hung Up"),
        new("CALLBACK_REQUESTED", "Callback Scheduled"),
        new("INFO_PROVIDED", "Information Provided"),
        new("MISROUTED_CALL", "Misrouted Call"),
        new("GHOST_CALL", "Ghost Call"),
        new("XFER_CONF", "Transferred Call / Conference Call"),
    };

    private static readonly DispositionOption[] BscsrInbound =
        new[] { new DispositionOption("SCREENING_COMPLETED", "Screening Completed") }.Concat(Inbound).ToArray();

    public static IReadOnlyList<DispositionOption> For(string? campaignId, bool inbound)
    {
        if (inbound)
        {
            return campaignId switch
            {
                "CMXBSMSC" => BsmscInbound,
                "CMXBSCSR" => BscsrInbound,
                _ => Inbound,
            };
        }

        return campaignId switch
        {
            "CMXBSMSC" or "CMXBSCSR" => ScreeningOutbound,
            _ => Outbound,
        };
    }

    /// <summary>Dispositions that need a callback date/time before saving.</summary>
    public static bool NeedsCallbackTime(string? value) =>
        value is "CALLBACK" or "CALLBACK_REQUESTED";
}
