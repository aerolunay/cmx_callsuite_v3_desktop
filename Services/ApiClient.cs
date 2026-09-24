using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmxDialer.Infrastructure;

namespace CmxDialer.Services;

public sealed class ApiException : Exception
{
    public int Status { get; }
    public string? Reason { get; }
    public string? Code { get; }

    public ApiException(int status, string message, string? reason = null, string? code = null) : base(message)
    {
        Status = status;
        Reason = reason;
        Code = code;
    }
}

/// <summary>
/// Talks to the existing Node backend exactly like frontend/src/api.js does.
/// The express-session cookie lives in <see cref="Cookies"/> and is shared with the WebSocket.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public Uri BaseUri { get; }
    public CookieContainer Cookies { get; } = new();

    public ApiClient(string serverUrl)
    {
        BaseUri = new Uri(serverUrl.TrimEnd('/') + "/");
        var handler = new HttpClientHandler { CookieContainer = Cookies, UseCookies = true };
        _http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _http.Dispose();

    // ---------------------------------------------------------------- core

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "api/" + path.TrimStart('/'));
        if (body != null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new ApiException(0, "The dialer server took too long to respond. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"HTTP {method} {path} failed", ex);
            throw new ApiException(0, "Can't reach the dialer server. Check your internet connection.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                using var empty = JsonDocument.Parse("{}");
                root = empty.RootElement.Clone();
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = root.Str("message");
                if (string.IsNullOrWhiteSpace(message)) message = $"Request failed ({(int)response.StatusCode}).";
                var reason = root.Str("reason");
                var code = root.Str("code");
                throw new ApiException((int)response.StatusCode, message,
                    string.IsNullOrEmpty(reason) ? null : reason,
                    string.IsNullOrEmpty(code) ? null : code);
            }

            return root;
        }
    }

    private static T? Read<T>(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var p)) return default;
        if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined) return default;
        return p.Deserialize<T>(Json);
    }

    private Task<JsonElement> Get(string path) => SendAsync(HttpMethod.Get, path);
    private Task<JsonElement> Post(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    private static string Q(string value) => Uri.EscapeDataString(value);

    // ---------------------------------------------------------------- auth

    public async Task<bool> CheckUserAsync(string email) =>
        (await Post("auth/check-user", new { email })).Bool("totpEnabled");

    public Task RequestOtpAsync(string email) => Post("auth/request-otp", new { email });

    public async Task<AgentInfo> VerifyOtpAsync(string email, string code) =>
        Read<AgentInfo>(await Post("auth/verify-otp", new { email, code }), "agent")
        ?? throw new ApiException(500, "Login succeeded but no agent profile was returned.");

    public async Task<AgentInfo> LoginTotpAsync(string email, string code) =>
        Read<AgentInfo>(await Post("auth/login-totp", new { email, code }), "agent")
        ?? throw new ApiException(500, "Login succeeded but no agent profile was returned.");

    public async Task<AgentInfo?> MeAsync() => Read<AgentInfo>(await Get("auth/me"), "agent");

    public Task LogoutAsync() => Post("auth/logout");

    // ---------------------------------------------------------------- campaigns

    public async Task<List<Campaign>> GetMyCampaignsAsync() =>
        Read<List<Campaign>>(await Get("campaigns/mine"), "campaigns") ?? new();

    public async Task<List<string>> GetWorkingCampaignIdsAsync() =>
        Read<List<string>>(await Get("dialer/working-campaigns"), "campaignIds") ?? new();

    public Task SetWorkingCampaignsAsync(IEnumerable<string> campaignIds) =>
        Post("dialer/working-campaigns", new { campaignIds = campaignIds.ToArray() });

    public async Task<List<CampaignAgent>> GetCampaignAgentsAsync(string campaignId) =>
        Read<List<CampaignAgent>>(await Get($"dialer/campaign-agents?campaignId={Q(campaignId)}"), "agents") ?? new();

    public async Task<CampaignDispositions> GetCampaignDispositionsAsync(string campaignId)
    {
        var root = await Get($"dialer/campaigns/{Q(campaignId)}/dispositions");
        return root.Deserialize<CampaignDispositions>(Json) ?? new CampaignDispositions();
    }

    // ---------------------------------------------------------------- status + phone

    public async Task<AgentStatusInfo?> GetStatusAsync() => Read<AgentStatusInfo>(await Get("dialer/status"), "status");

    public async Task<AgentStatusInfo> SetStatusAsync(string status, string? campaignId) =>
        Read<AgentStatusInfo>(await Post("dialer/status", new { status, campaignId }), "status")
        ?? new AgentStatusInfo { Status = status };

    /// <summary>Same endpoint the browser phone uses — extension + registration password + server host.</summary>
    public async Task<SipCredentials> GetSipCredentialsAsync() =>
        Read<SipCredentials>(await Get("dialer/webrtc-credentials"), "credentials")
        ?? throw new ApiException(500, "The server did not return phone credentials.");

    // ---------------------------------------------------------------- outbound calls

    public async Task<bool> HasLeadsAsync(string campaignId) =>
        (await Get($"dialer/has-leads?campaignId={Q(campaignId)}")).Bool("hasLead");

    public async Task<JsonObject> NextLeadAsync(string campaignId) =>
        Read<JsonObject>(await Post("dialer/next-lead", new { campaignId }), "lead")
        ?? throw new ApiException(404, "No eligible leads found for this campaign right now.");

    public async Task<StartCallResult> StartCallAsync(string campaignId, long leadId, string phoneNumber, JsonObject lead, string callType)
    {
        var root = await Post("dialer/start-call", new { campaignId, leadId, phoneNumber, lead, callType });
        return root.Deserialize<StartCallResult>(Json) ?? new StartCallResult();
    }

    public async Task<CallInfo?> GetCurrentCallAsync() => Read<CallInfo>(await Get("dialer/current-call"), "call");

    public async Task<InboundCallInfo?> GetCurrentInboundCallAsync() =>
        Read<InboundCallInfo>(await Get("dialer/inbound/current"), "call");

    public Task EndCallAsync(string callId) => Post($"dialer/end-call/{Q(callId)}");

    public async Task<bool> HoldCallAsync(string callId, bool hold)
    {
        var root = await Post(hold ? $"dialer/hold/{Q(callId)}" : $"dialer/unhold/{Q(callId)}");
        return root.TryGetProperty("status", out var s) ? s.Bool("onHold") : hold;
    }

    public Task EndInboundCallAsync(string callId) => Post("dialer/inbound/end-call", new { callId });

    public async Task<bool> HoldInboundAsync(string callId, bool hold)
    {
        var root = await Post(hold ? "dialer/inbound/hold" : "dialer/inbound/unhold", new { callId });
        return root.TryGetProperty("status", out var s) ? s.Bool("onHold") : hold;
    }

    public Task SaveDispositionAsync(string callId, object payload) => Post($"dialer/disposition/{Q(callId)}", payload);

    public Task SaveInboundDispositionAsync(object payload) => Post("dialer/inbound-disposition", payload);

    public async Task<CallbackPending?> CheckCallbackPendingAsync(string phoneNumber) =>
        Read<CallbackPending>(await Get($"dialer/check-callback-pending?phoneNumber={Q(phoneNumber)}"), "pending");

    // ---------------------------------------------------------------- transfer / line 2

    public Task TransferBlindAsync(string target, bool isExtension) =>
        Post("dialer/transfer-blind", new { target, isExtension });

    public Task StartLineTwoAsync(string target, bool isExtension) =>
        Post("dialer/line-two/start", new { target, isExtension });

    /// <summary>action: "transfer" (agent leaves) or "conference" (everyone together).</summary>
    public Task CompleteLineTwoAsync(string action) => Post("dialer/line-two/complete", new { action });

    public Task CancelLineTwoAsync() => Post("dialer/line-two/cancel");

    public Task SwitchLineAsync(int line) => Post("dialer/line-two/switch", new { line });

    public async Task<LineTwoStatus> GetLineTwoStatusAsync()
    {
        var root = await Get("dialer/line-two/status");
        // Route returns { success, ...status } or { success, status: {...} } — accept both.
        if (root.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return nested.Deserialize<LineTwoStatus>(Json) ?? new LineTwoStatus();
        return root.Deserialize<LineTwoStatus>(Json) ?? new LineTwoStatus();
    }

    public Task HoldLineTwoAsync(bool hold) => Post(hold ? "dialer/line-two/hold" : "dialer/line-two/unhold");

    // ---------------------------------------------------------------- callbacks page

    public async Task<List<CallbackRow>> GetAbandonedVoicemailAsync(string? campaignId)
    {
        var path = "dialer/abandoned-voicemail" + (string.IsNullOrEmpty(campaignId) ? "" : $"?campaignId={Q(campaignId)}");
        return Read<List<CallbackRow>>(await Get(path), "rows") ?? new();
    }

    public async Task<string> GetVoicemailPlaybackUrlAsync(long voicemailLogId)
    {
        var url = (await Get($"dialer/voicemail/{voicemailLogId}/playback-url")).Str("url");
        if (string.IsNullOrWhiteSpace(url)) throw new ApiException(404, "This voicemail has no recording available.");
        return url;
    }
}
