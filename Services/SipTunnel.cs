using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using CmxDialer.Infrastructure;

namespace CmxDialer.Services;

/// <summary>
/// Carries the phone's SIP signalling through the app's own signed-in HTTPS connection
/// (wss://server/ws/sip on port 443) instead of raw UDP 5060 — so it works from any office
/// or home network, without IP allow-lists and without router "SIP ALG" interference.
///
/// Locally it looks like a normal UDP SIP server on 127.0.0.1: SIPSorcery sends to
/// <see cref="LocalEndPoint"/>, and every datagram is forwarded as one WebSocket message
/// (and vice versa). On the server, the relay only accepts the connection if the app's
/// login session is valid, then hands the messages to Asterisk. Call audio (RTP) does not
/// go through here — it still flows directly over UDP.
/// </summary>
public sealed class SipTunnel : IDisposable
{
    private readonly ApiClient _api;
    private readonly Uri _uri;
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private IPEndPoint? _phone; // SIPSorcery's local socket, learned from its first packet
    private TaskCompletionSource<bool> _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SipTunnel(ApiClient api)
    {
        _api = api;
        _uri = new UriBuilder(api.BaseUri)
        {
            Scheme = api.BaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws/sip",
            Query = "",
        }.Uri;
    }

    /// <summary>Where SIPSorcery should send SIP: a local UDP endpoint on this PC.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public bool IsConnected { get; private set; }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunSocketAsync(_cts.Token);
        _ = PumpUdpAsync(_cts.Token);
    }

    public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
    {
        if (IsConnected) return true;
        var done = await Task.WhenAny(_firstConnect.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == _firstConnect.Task && _firstConnect.Task.Result;
    }

    private async Task RunSocketAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var cookie = _api.Cookies.GetCookieHeader(_api.BaseUri);
            if (!string.IsNullOrEmpty(cookie)) ws.Options.SetRequestHeader("Cookie", cookie);

            try
            {
                await ws.ConnectAsync(_uri, ct).ConfigureAwait(false);
                _ws = ws;
                attempt = 0;
                IsConnected = true;
                _firstConnect.TrySetResult(true);
                Log.Info("SIP tunnel connected");
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("SIP tunnel error", ex); }
            finally
            {
                _ws = null;
                if (IsConnected) Log.Info("SIP tunnel disconnected");
                IsConnected = false;
            }

            attempt++;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Server -> phone: one WebSocket message = one SIP datagram.
    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var phone = _phone;
            if (phone != null && message.Length > 0)
                await _udp.SendAsync(message.ToArray(), (int)message.Length, phone).ConfigureAwait(false);
            message.SetLength(0);
        }
    }

    // Phone -> server.
    private async Task PumpUdpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try { datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // e.g. ICMP "port unreachable" echoes on Windows

            _phone = datagram.RemoteEndPoint;
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) continue; // SIP retransmits on its own

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(datagram.Buffer, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("SIP tunnel send failed", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
        try { _udp.Dispose(); } catch { /* ignore */ }
    }
}
