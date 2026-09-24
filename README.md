# CMX CallSuite Desktop v3 — Windows desktop client

A fixed-size (400×720) Windows dialer for CMX Callsuite. It contains the SIP phone, the aux
status, the disposition form and a Callbacks page, and it replaces MicroSIP plus the browser
tab for agents. It talks only to the **existing Node backend** (REST + `/ws/dialer`). No backend
changes are needed, and client PCs have no AMI or database access.

## Agent flow

1. **Sign in** — the same email as the web dialer, then an email code or an authenticator code.
2. **Select campaign** — enforces the same rules as the backend: one campaign unless
   `multi_campaign_enabled`, and an OUTBOUND campaign is always worked alone.
3. **Register phone** — registers the agent's `ccNNN` extension over **UDP 5060**.
4. **Dialer** — the agent starts in **Not Ready**. If a call or disposition is still in
   progress (for example, after an app restart), the current status is kept instead.

The window cannot be minimized, resized or closed during a session. The agent must sign out
first, and sign-out is blocked during a call or while a disposition is owed. When a call
arrives, the taskbar button flashes and the window is brought forward.

## Build

Requirements: Windows 10/11 and the **.NET 8 SDK**, or Visual Studio 2022 17.8+ with the
".NET desktop development" workload.

```
dotnet restore
dotnet build -c Debug
dotnet run
```

**Publish** a single self-contained exe (no .NET install needed on agent PCs):

```
dotnet publish -c Release
```

The output is `bin\Release\net8.0-windows\win-x64\publish\CmxDialer.exe`. Ship
`appsettings.json` next to it.

For rollout, sign the exe with your code-signing certificate so SmartScreen doesn't warn
agents. You can package it as MSIX, MSI or an Intune Win32 app.

## Configuration — `appsettings.json`

| Key | Meaning |
|---|---|
| `ServerUrl` | Backend base URL, without `/api`. Dev: `https://dialer-dev.cmxinnovations.com` |
| `SipPort` | Asterisk PJSIP UDP port (5060) |
| `SipHostOverride` | Optional. By default the SIP host is the hostname of `ASTERISK_WSS_URL`, taken from `GET /api/dialer/webrtc-credentials`. Set this if Asterisk's SIP address differs from that host. |
| `SipRegisterExpirySeconds` | REGISTER expiry (120) |
| `AudioOutputDeviceIndex` / `AudioInputDeviceIndex` | `-1` = Windows default headset/mic |
| `AlwaysOnTop` | Start pinned on top. Agents can toggle this with the pin icon. |

Logs are written to `%LOCALAPPDATA%\CmxDialer\logs\yyyyMMdd.log`, covering registration,
WebSocket and call events.

## Server prerequisites (dev server first)

- **HTTPS**, with nginx proxying `/api` and upgrading WebSockets on `/ws/dialer`:
  `proxy_http_version 1.1; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";`
- **PJSIP UDP transport** on 5060. Each agent's `ccNNN` endpoint must allow it — not
  webrtc-only (`transport=` / `webrtc=no`, and codecs `ulaw,alaw` allowed). The registration
  password is the backend's shared `PHONE_REGISTRATION_PASSWORD`.
- **NAT:** agents behind NAT need `rtp_symmetric=yes`, `force_rport=yes` and
  `rewrite_contact=yes` on the endpoints. These are the same settings MicroSIP already relies on.
- **Firewall:** allow UDP 5060 and the RTP range (`rtp.conf`) **only from agent / VPN networks**.
  Never open 5060 to the internet.
- Session cookie: the backend expires a session if `/ws/dialer` stays closed for 15s. The app
  reconnects after 1s, 2s, 3s and then every 4s, and re-registers after sleep/resume.

## Security notes

- The phone **auto-answers only INVITEs whose source IP is the Asterisk server**. Anything else
  gets `403`, and a second call while busy gets `486`.
- The phone never places calls itself. All calls are originated by the backend over AMI,
  exactly as with the web dialer.
- Cookies live in memory only. Signing out (or a forced logout) discards them.

## Verify on first Windows build

The code compiles against the .NET 8 compiler, but the third-party calls below were written
against the libraries' documented APIs and could not be restored or built in the authoring
environment. If `dotnet build` flags anything, these are the places to look (all are in
`Services/SipPhone.cs`, `Services/VoicemailPlayer.cs` and `Infrastructure/Tones.cs`):

- SIPSorcery 8.x: `SIPRegistrationUserAgent` events `(uri, response[, message])`,
  `SIPTransport.SIPTransportRequestReceived` `(local, remote, request) → Task`,
  `SIPUserAgent.AcceptCall` / `Answer`, `VoIPMediaSession.Start`, `SendDtmf(byte)`.
- SIPSorceryMedia.Windows: `WindowsAudioEndPoint(encoder, outIndex, inIndex)`,
  `PauseAudio` / `ResumeAudio` / `CloseAudio`.
- NAudio 2.2: `MediaFoundationReader(url)`, `SignalGenerator(...).Take(...)`.

## Test checklist (use two test agents on the dev server)

- [ ] Sign in with an email code; sign in with an authenticator code; a wrong code shows an error.
- [ ] A user with no phone assigned gets the "No phone assigned" message.
- [ ] Campaign rules: a single campaign; multi-campaign BLENDED only; OUTBOUND is exclusive.
- [ ] Registration shows a green dot next to the agent's **full name**. It fails cleanly when the firewall blocks 5060.
- [ ] The agent lands in **Not Ready**. The aux dropdown changes status; it is locked during a call and ACW.
- [ ] **Dial Next**: the phone auto-answers, the customer rings, "Connected" appears and the timer runs.
- [ ] Mute (the customer hears silence), Hold/Unhold (music), DTMF into an IVR, Hang Up.
- [ ] RATIO campaign auto-dials when Ready; "no leads" self-heals within 20s once leads are added.
- [ ] Inbound call to a BLENDED campaign: auto-answer, the window flashes and comes forward, inbound dispositions show.
- [ ] Line 2 to a number and to an agent: switch lines, transfer & leave, conference, end Line 2, blind transfer.
- [ ] Disposition: comments are required; CALLBACK needs a date and time; "Set me Not Ready" works.
- [ ] Callbacks page: filter by campaign, play a voicemail, call back, and the entry disappears after the disposition is saved.
- [ ] Manual dial of a number with a pending callback asks whether to treat it as that callback.
- [ ] The window can't be closed (Alt+F4) or signed out during a call. Admin kick → returns to sign-in.
- [ ] Sleep the laptop and wake it: the WebSocket reconnects and the phone re-registers.
- [ ] Unplug the network for under 15s → the app recovers. For over 15s → it returns to sign-in.

## Known limitations

- If the app restarts while the agent is in After Call Work and the call has already ended,
  that disposition can't be restored in this app (the backend no longer exposes the call). The
  agent sees a message asking a supervisor to clear it.
- Inbound caller first/last name isn't collected (the backend doesn't require it).
- One app instance per PC (two would fight over the same extension).
# cmx_callsuite_v3_desktop
