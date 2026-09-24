using System.IO;
using System.Text.Json;

namespace CmxDialer.Config;

/// <summary>
/// Settings read from appsettings.json next to the exe. One build can point at
/// dev or production just by changing ServerUrl.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Base URL of the Node backend, e.g. https://dialer.cmxinnovations.com (no /api).</summary>
    public string ServerUrl { get; set; } = "https://dialer-dev.cmxinnovations.com";

    /// <summary>
    /// true (default): phone signalling goes through the signed-in HTTPS connection
    /// (wss://server/ws/sip, port 443) — works from any network, no IP allow-lists.
    /// false: register directly over UDP to SipPort (legacy / troubleshooting).
    /// </summary>
    public bool UseSipTunnel { get; set; } = true;

    /// <summary>Asterisk PJSIP UDP port the phone registers to (only when UseSipTunnel is false).</summary>
    public int SipPort { get; set; } = 5060;

    /// <summary>
    /// Optional. By default the SIP host is taken from the backend's ASTERISK_WSS_URL
    /// (GET /api/dialer/webrtc-credentials). Set this if Asterisk's SIP address differs.
    /// </summary>
    public string? SipHostOverride { get; set; }

    public int SipRegisterExpirySeconds { get; set; } = 60;

    /// <summary>-1 = Windows default device.</summary>
    public int AudioOutputDeviceIndex { get; set; } = -1;
    public int AudioInputDeviceIndex { get; set; } = -1;

    public bool AlwaysOnTop { get; set; } = true;

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error("Failed to read appsettings.json, using defaults", ex);
        }
        return new AppSettings();
    }
}
