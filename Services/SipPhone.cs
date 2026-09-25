using System.Net;
using CmxDialer.Infrastructure;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Windows;

namespace CmxDialer.Services;

/// <summary>
/// Built-in softphone (replaces MicroSIP). Registers the agent's ccNNN PJSIP endpoint over
/// UDP and AUTO-ANSWERS every call that comes from the Asterisk server — which is how every
/// call reaches the agent in this system (the backend AMI-Originates PJSIP/ccNNN for outbound,
/// inbound, callbacks and silent-listen alike). The phone never dials out itself.
///
/// Calls from any other address are refused (403), so nobody can dial ccNNN directly and be
/// auto-answered into the agent's headset.
/// </summary>
public sealed class SipPhone : IDisposable
{
    private readonly object _gate = new();
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _activeCall;
    private WindowsAudioEndPoint? _audio;
    private CallAudioPlayer? _player;
    private HashSet<IPAddress> _serverAddresses = new();
    private int _audioOutIndex = -1;
    private int _audioInIndex = -1;

    // Auto-recovery: remembered so a dead registration can be rebuilt on a fresh UDP socket.
    private (string Host, int Port, string Extension, string Password, int Expiry, IPEndPoint? Proxy)? _registrationParams;
    private SIPEndPoint? _outboundProxy; // set when SIP runs through the SipTunnel
    private int _recovering;

    public event Action<bool, string?>? RegistrationChanged;
    public event Action? CallAnswered;
    public event Action? CallEnded;

    /// <summary>
    /// Asked before auto-answering. Returns false when the agent isn't taking calls
    /// (Not Ready, breaks, After Call Work…) so the call is refused instead of answered.
    /// </summary>
    public Func<bool>? ShouldAnswer { get; set; }

    /// <summary>Caller ID number the backend uses for Silent Listen calls (monitoringService.js).</summary>
    private const string SilentListenCallerId = "9999";

    public bool IsRegistered { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsInCall => _activeCall?.IsCallActive == true;
    public string? LastError { get; private set; }

    public void SetAudioDevices(int outputIndex, int inputIndex)
    {
        _audioOutIndex = outputIndex;
        _audioInIndex = inputIndex;
    }

    /// <summary>Registers and waits for the first success/failure (or timeout).</summary>
    /// <param name="outboundProxy">
    /// When set (tunnel mode), all SIP goes to this local endpoint, which forwards it through
    /// the signed-in HTTPS connection; the SIP socket then only listens on 127.0.0.1.
    /// </param>
    public async Task<bool> RegisterAsync(string host, int port, string extension, string password, int expirySeconds,
        TimeSpan timeout, IPEndPoint? outboundProxy = null)
    {
        _registrationParams = (host, port, extension, password, expirySeconds, outboundProxy);
        TearDown();
        _outboundProxy = outboundProxy == null ? null : new SIPEndPoint(SIPProtocolsEnum.udp, outboundProxy);
        LastError = null;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            _serverAddresses = addresses.Select(Normalize).ToHashSet();
        }
        catch (Exception ex)
        {
            LastError = $"Can't find the phone server \"{host}\".";
            Log.Error("SIP DNS lookup failed", ex);
            return false;
        }

        var transport = new SIPTransport();
        var bindAddress = outboundProxy != null ? IPAddress.Loopback : IPAddress.Any;
        transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(bindAddress, 0)));
        transport.SIPTransportRequestReceived += OnRequestReceived;
        _transport = transport;

        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new SIPRegistrationUserAgent(transport, extension, password, $"{host}:{port}", expirySeconds);

        registration.RegistrationSuccessful += (uri, response) =>
        {
            if (!ReferenceEquals(_registration, registration)) return; // an old, replaced socket
            Log.Info($"SIP registered {uri}");
            IsRegistered = true;
            LastError = null;
            RegistrationChanged?.Invoke(true, null);
            outcome.TrySetResult(true);
        };
        registration.RegistrationTemporaryFailure += (uri, response, message) =>
        {
            if (!ReferenceEquals(_registration, registration)) return; // an old, replaced socket
            Log.Info($"SIP registration temporary failure: {message}");
            IsRegistered = false;
            LastError = message;
            RegistrationChanged?.Invoke(false, message);
            if (!outcome.TrySetResult(false)) ScheduleRecovery();
        };
        registration.RegistrationFailed += (uri, response, message) =>
        {
            if (!ReferenceEquals(_registration, registration)) return; // an old, replaced socket
            Log.Info($"SIP registration failed: {message}");
            IsRegistered = false;
            LastError = message;
            RegistrationChanged?.Invoke(false, message);
            if (!outcome.TrySetResult(false)) ScheduleRecovery();
        };

        _registration = registration;
        registration.Start();

        var finished = await Task.WhenAny(outcome.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != outcome.Task)
        {
            LastError = $"No answer from the phone server at {host}:{port} (UDP). Check the firewall / VPN.";
            return false;
        }

        if (!outcome.Task.Result && string.IsNullOrWhiteSpace(LastError))
            LastError = "The phone server rejected the registration.";
        return outcome.Task.Result;
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private bool IsFromServer(SIPEndPoint remote)
    {
        var address = remote?.GetIPEndPoint()?.Address;
        return address != null && _serverAddresses.Contains(Normalize(address));
    }

    private async Task OnRequestReceived(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        try
        {
            // In-dialog requests (BYE, re-INVITE, INFO…) belong to the active SIPUserAgent.
            if (request.Header.To?.ToTag != null) return;

            switch (request.Method)
            {
                case SIPMethodsEnum.INVITE:
                    await HandleInviteAsync(request, remoteEndPoint).ConfigureAwait(false);
                    break;
                case SIPMethodsEnum.OPTIONS:
                case SIPMethodsEnum.NOTIFY:
                    await RespondAsync(request, SIPResponseStatusCodesEnum.Ok).ConfigureAwait(false);
                    break;
                case SIPMethodsEnum.BYE:
                case SIPMethodsEnum.CANCEL:
                    // Handled by the call's user agent; nothing to do for stray ones.
                    break;
                default:
                    await RespondAsync(request, SIPResponseStatusCodesEnum.MethodNotAllowed).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"SIP request {request.Method} handling failed", ex);
        }
    }

    private async Task HandleInviteAsync(SIPRequest request, SIPEndPoint remoteEndPoint)
    {
        if (!IsFromServer(remoteEndPoint))
        {
            Log.Info($"Refused INVITE from {remoteEndPoint} (not the dialer server)");
            await RespondAsync(request, SIPResponseStatusCodesEnum.Forbidden).ConfigureAwait(false);
            return;
        }

        // Supervisor "Silent Listen" (Live Status page): the backend rings the
        // listener's phone as "CMX Silent Listen" <9999>. Always answer it, even
        // while Not Ready — it's listen-only and never an agent-routed call.
        var isSilentListen = request.Header.From?.FromURI?.User == SilentListenCallerId;
        if (isSilentListen) Log.Info("Answering supervisor Silent Listen call");

        if (!isSilentListen && ShouldAnswer != null && !ShouldAnswer())
        {
            Log.Info($"Refused call from {request.Header.From?.FromURI}: agent is not taking calls");
            await RespondAsync(request, SIPResponseStatusCodesEnum.BusyHere).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (_activeCall?.IsCallActive == true)
            {
                _ = RespondAsync(request, SIPResponseStatusCodesEnum.BusyHere);
                return;
            }
        }

        var transport = _transport;
        if (transport == null) return;

        var ua = new SIPUserAgent(transport, _outboundProxy);
        // Microphone via SIPSorcery; its built-in playback is switched off (disableSink)
        // because its large buffer adds delay — CallAudioPlayer plays the caller instead.
        var audio = new WindowsAudioEndPoint(new AudioEncoder(), _audioOutIndex, _audioInIndex, false, true);
        var media = new VoIPMediaSession(audio.ToMediaEndPoints()) { AcceptRtpFromAny = false }; // only the Asterisk media address
        var player = new CallAudioPlayer(_audioOutIndex);
        media.OnRtpPacketReceived += (remote, kind, packet) =>
        {
            if (kind == SDPMediaTypesEnum.audio) player.AddRtp(packet.Header.PayloadType, packet.Payload);
        };

        ua.OnCallHungup += _ => OnHungUp(ua);

        lock (_gate)
        {
            _activeCall = ua;
            _audio = audio;
            _player = player;
            IsMuted = false;
        }

        var serverUa = ua.AcceptCall(request);
        var answered = await ua.Answer(serverUa, media).ConfigureAwait(false);
        if (answered && ua.IsCallActive)
        {
            await media.Start().ConfigureAwait(false);
            Log.Info($"Auto-answered call from {request.Header.From?.FromURI}");
            CallAnswered?.Invoke();
        }
        else
        {
            Log.Info("Auto-answer failed");
            OnHungUp(ua);
        }
    }

    private void OnHungUp(SIPUserAgent ua)
    {
        WindowsAudioEndPoint? audio = null;
        CallAudioPlayer? player = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_activeCall, ua)) return;
            _activeCall = null;
            audio = _audio;
            _audio = null;
            player = _player;
            _player = null;
            IsMuted = false;
        }

        try { audio?.CloseAudio(); } catch (Exception ex) { Log.Error("Closing audio failed", ex); }
        player?.Dispose();
        try { (ua as IDisposable)?.Dispose(); } catch { /* ignore */ }
        CallEnded?.Invoke();
    }

    private async Task RespondAsync(SIPRequest request, SIPResponseStatusCodesEnum status)
    {
        var transport = _transport;
        if (transport == null) return;
        var response = SIPResponse.GetResponse(request, status, null);
        await transport.SendResponseAsync(response).ConfigureAwait(false);
    }

    /// <summary>Local microphone mute (the other side hears silence).</summary>
    public async Task SetMutedAsync(bool muted)
    {
        var audio = _audio;
        if (audio == null) return;
        if (muted) await audio.PauseAudio().ConfigureAwait(false);
        else await audio.ResumeAudio().ConfigureAwait(false);
        IsMuted = muted;
    }

    /// <summary>RFC 2833 DTMF into the live call (IVRs, extensions).</summary>
    public async Task SendDtmfAsync(char key)
    {
        var ua = _activeCall;
        if (ua == null || !ua.IsCallActive) return;
        byte tone = key switch
        {
            '*' => 10,
            '#' => 11,
            >= '0' and <= '9' => (byte)(key - '0'),
            _ => 255,
        };
        if (tone == 255) return;
        await ua.SendDtmf(tone).ConfigureAwait(false);
    }

    /// <summary>Local hang-up — only a fallback; normally the backend ends calls via AMI.</summary>
    public void HangUp()
    {
        var ua = _activeCall;
        if (ua == null) return;
        try { ua.Hangup(); } catch (Exception ex) { Log.Error("SIP hangup failed", ex); }
        OnHungUp(ua);
    }

    /// <summary>Signs the phone out for good (sign-out, app exit). Stops auto-recovery.</summary>
    public void Stop()
    {
        _registrationParams = null;
        TearDown();
    }

    private void TearDown()
    {
        HangUp();
        try { _registration?.Stop(); } catch { /* ignore */ }
        _registration = null;

        if (_transport != null)
        {
            _transport.SIPTransportRequestReceived -= OnRequestReceived;
            try { _transport.Shutdown(); } catch { /* ignore */ }
            _transport = null;
        }

        if (IsRegistered)
        {
            IsRegistered = false;
            RegistrationChanged?.Invoke(false, null);
        }
    }

    /// <summary>
    /// A registration refresh that gets no answer usually means the office router has dropped
    /// (or its SIP "ALG" has mangled) this socket's NAT mapping — retrying on the same socket
    /// never recovers. So rebuild everything on a fresh UDP socket: after 5 s, then backing off
    /// to 60 s. Never interrupts a live call — waits for it to end first.
    /// </summary>
    private void ScheduleRecovery()
    {
        if (_registrationParams == null) return;
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var delay = TimeSpan.FromSeconds(5);
                while (true)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    var p = _registrationParams;
                    if (p == null) return;                 // signed out meanwhile
                    if (IsRegistered) return;              // recovered on its own
                    if (IsInCall) { delay = TimeSpan.FromSeconds(5); continue; }

                    Log.Info("Phone registration lost — reconnecting on a fresh socket");
                    var ok = await RegisterAsync(p.Value.Host, p.Value.Port, p.Value.Extension,
                        p.Value.Password, p.Value.Expiry, TimeSpan.FromSeconds(12), p.Value.Proxy).ConfigureAwait(false);
                    if (ok) { Log.Info("Phone re-registered"); return; }
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Phone recovery failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _recovering, 0);
                if (_registrationParams != null && !IsRegistered) ScheduleRecovery();
            }
        });
    }

    public void Dispose() => Stop();
}
