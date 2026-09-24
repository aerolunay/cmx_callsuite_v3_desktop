using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CmxDialer.Infrastructure;

namespace CmxDialer.Services;

/// <summary>
/// The same /ws/dialer socket the web app uses (backend config/ws.js).
///
/// IMPORTANT: the backend destroys the session if this socket stays closed for
/// 15 seconds, so reconnects are fast (1s, 2s, 3s, then every 4s).
/// Close code 4001 means the server rejected the session — the app must return to login.
/// </summary>
public sealed class DialerSocket : IDisposable
{
    private readonly ApiClient _api;
    private readonly Uri _socketUri;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool> _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<JsonElement>? MessageReceived;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? SessionRejected;

    public bool IsConnected { get; private set; }

    public DialerSocket(ApiClient api)
    {
        _api = api;
        var builder = new UriBuilder(api.BaseUri)
        {
            Scheme = api.BaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws/dialer",
            Query = "",
        };
        _socketUri = builder.Uri;
    }

    public void Start()
    {
        Stop();
        _firstConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_firstConnect.Task, Task.Delay(timeout));
        return done == _firstConnect.Task && _firstConnect.Task.Result;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
        SetConnected(false);
    }

    public void Dispose() => Stop();

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            // Send the express-session cookie explicitly (taken from the https URI so
            // the Secure cookie is included).
            var cookieHeader = _api.Cookies.GetCookieHeader(_api.BaseUri);
            if (!string.IsNullOrEmpty(cookieHeader)) ws.Options.SetRequestHeader("Cookie", cookieHeader);

            try
            {
                await ws.ConnectAsync(_socketUri, ct).ConfigureAwait(false);
                attempt = 0;
                Log.Info("WebSocket connected");
                SetConnected(true);
                _firstConnect.TrySetResult(true);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("WebSocket error", ex);
            }

            SetConnected(false);

            if (ws.CloseStatus.HasValue && (int)ws.CloseStatus.Value == 4001)
            {
                Log.Info($"WebSocket rejected by server: {ws.CloseStatusDescription}");
                _firstConnect.TrySetResult(false);
                SessionRejected?.Invoke(ws.CloseStatusDescription ?? "Session ended.");
                break;
            }

            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(attempt, 4));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
                catch { /* ignore */ }
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);

            try
            {
                using var doc = JsonDocument.Parse(text);
                MessageReceived?.Invoke(doc.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                Log.Error("Bad WebSocket message", ex);
            }
        }
    }
}
