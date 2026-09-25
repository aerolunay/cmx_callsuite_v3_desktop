using System.IO;
using System.Net.Http;
using CmxDialer.Infrastructure;
using NAudio.Wave;

namespace CmxDialer.Services;

/// <summary>
/// Plays a voicemail from its presigned S3 URL (GET /dialer/voicemail/:id/playback-url).
/// Voicemails are Asterisk WAV recordings (8 kHz, 16-bit PCM), so the file is downloaded
/// and read with WaveFileReader — no Media Foundation dependency (NAudio 3 moved it out).
/// </summary>
public sealed class VoicemailPlayer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _gate = new();
    private WaveOut? _output;
    private WaveStream? _reader;

    public async Task PlayAsync(string url, Action onStopped)
    {
        Stop();

        var data = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        var reader = new WaveFileReader(new MemoryStream(data));
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm &&
            reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            reader.Dispose();
            throw new InvalidOperationException($"Unsupported voicemail format ({reader.WaveFormat.Encoding}).");
        }

        var output = new WaveOut();
        output.Init(reader);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Error("Voicemail playback stopped with error", e.Exception);
            Cleanup(output, reader);
            onStopped();
        };

        lock (_gate)
        {
            _output = output;
            _reader = reader;
        }
        output.Play();
    }

    public void Stop()
    {
        WaveOut? output;
        lock (_gate) output = _output;
        try { output?.Stop(); } catch { /* ignore */ }
    }

    private void Cleanup(WaveOut output, WaveStream reader)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_output, output)) { _output = null; _reader = null; }
        }
        try { output.Dispose(); } catch { /* ignore */ }
        try { reader.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose() => Stop();
}