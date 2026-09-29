# CMX CallSuite Desktop v3

Windows desktop softphone + dialer for CMX CallSuite agents. Signs in to the CallSuite web
backend, registers the agent's phone, auto-answers the calls the server routes to it, and
handles dispositions, callbacks, voicemail, hold/mute/keypad, Line 2 transfers and
conferences. It replaces the web dialer and MicroSIP for agents.

## How it connects

1. **Sign in** — email + one-time code (or authenticator) against `ServerUrl` over HTTPS.
2. **Live connection** — `wss://<server>/ws/dialer` (statuses, call events).
3. **Phone** — SIP runs through a **tunnel inside the signed-in HTTPS connection**
   (`wss://<server>/ws/sip`, port 443). The server's relay (`backend/sipRelay.js` in the
   CallSuite repo) only accepts it for a valid login and only lets each user register their
   own extension. Works from any office or home network: no IP allow-lists, no router
   "SIP ALG" problems. Registration lasts 1 hour, the relay gives each extension a fixed
   address, and the app re-registers immediately after any reconnect; a watchdog rebuilds
   the phone if Asterisk's 10-second keep-alive checks stop arriving.
4. **Call audio** — G.711 (μ-law/A-law) RTP directly over UDP 10000–20000 to the server,
   with a low-latency player (`Services/CallAudioPlayer.cs`).

Calls are auto-answered only from the CallSuite server, only while the agent is Ready (or
already on a call), plus supervisor Silent Listen calls at any status.

All times shown or sent (callbacks, call log) are **US Eastern**, whatever the PC's zone.

## Build and run

Requires the .NET 10 SDK on Windows.

```bash
dotnet build CmxDialer.csproj -c Debug
dotnet run --project CmxDialer.csproj
```

Release (single self-contained exe):

```bash
dotnet publish CmxDialer.csproj -c Release
```

Output: `bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\CmxDialer.exe`.
Code-sign it before giving it to agents. The installer should add a Windows Firewall
**Allow** rule for the exe on **all** network profiles (Private *and* Public) — without the
Public profile, agents on networks Windows treats as public get one-way audio.

## Settings (`appsettings.json`, next to the exe)

| Setting | Meaning |
|---|---|
| `ServerUrl` | CallSuite site, e.g. `https://callsuite.cmxinnovations.com` |
| `UseSipTunnel` | `true` (default): phone over the 443 tunnel. `false`: direct UDP (troubleshooting only) |
| `SipPort`, `SipHostOverride`, `SipRegisterExpirySeconds` | Direct-UDP mode only |
| `AudioOutputDeviceIndex`, `AudioInputDeviceIndex` | `-1` = Windows default devices |
| `AlwaysOnTop` | Keep the window above other apps (also the pin button) |

## Logs

`%LOCALAPPDATA%\CmxDialer\logs\YYYYMMDD.log` — sign-in, registration (`SIP registered`,
`SIP tunnel connected`), each call (`Auto-answered`, `Call audio: N caller packets
received`), refusals and recoveries.

## Server requirements

- The agent's phone in Admin → Phones must be type **DESKTOP** (UDP, G.711, no WebRTC,
  `media_address` = server IP).
- Firewall (AWS security group): **TCP 443** and **UDP 10000–20000** from agents.
- See `deploy/` in the CallSuite repo for the Apache `/ws/sip` route and the relay process.
