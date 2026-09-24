using CmxDialer.Infrastructure;
using NAudio.Wave;

namespace CmxDialer.Services;

/// <summary>
/// Low-latency playback of the caller's audio (G.711 ulaw/alaw, 8 kHz).
///
/// Replaces the playback side of SIPSorcery's WindowsAudioEndPoint, whose
/// 5-second buffer lets delay build up (a burst at call start, or slight clock
/// drift, never drains). Here the buffer is capped: if more than
/// <see cref="MaxQueuedMs"/> of audio is waiting, the backlog is dropped so
/// playback snaps back to real time.
///
/// Logs the first received packet and a per-call summary, so "can't hear the
/// caller" can be traced to either the network (0 packets) or playback.
/// </summary>
public sealed class CallAudioPlayer : IDisposable
{
    private const int MaxQueuedMs = 200;
    private static readonly WaveFormat Format = new(8000, 16, 1);

    private readonly object _gate = new();
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOut _output;
    private bool _disposed;
    private long _packets;
    private long _drops;

    public CallAudioPlayer(int outputDeviceIndex)
    {
        _buffer = new BufferedWaveProvider(Format, TimeSpan.FromSeconds(1)) // NAudio 3: size set at creation
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // play silence on underrun instead of stopping
        };
        // NAudio 3: WaveOut is the event-driven player; latency = buffer size x count (~120 ms).
        _output = new WaveOut
        {
            DeviceNumber = outputDeviceIndex,
            BufferMilliseconds = 40,
            NumberOfBuffers = 3,
        };
        try
        {
            _output.Init(_buffer);
            _output.Play();
            Log.Info($"Call audio playback started (device {outputDeviceIndex})");
        }
        catch (Exception ex)
        {
            Log.Error("Call audio playback failed to start", ex);
            throw;
        }
    }

    /// <summary>Called for every received RTP audio packet.</summary>
    public void AddRtp(int payloadType, byte[] payload)
    {
        if (payload.Length == 0) return;
        Func<byte, short>? decode = payloadType switch
        {
            0 => MuLawToLinear,
            8 => ALawToLinear,
            _ => null, // DTMF (101) and anything else: not audio
        };
        if (decode == null) return;

        var pcm = new byte[payload.Length * 2];
        for (int i = 0; i < payload.Length; i++)
        {
            short s = decode(payload[i]);
            pcm[2 * i] = (byte)s;
            pcm[2 * i + 1] = (byte)(s >> 8);
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (_packets++ == 0) Log.Info($"First caller audio packet received (payload type {payloadType})");
            if (_buffer.BufferedDuration.TotalMilliseconds > MaxQueuedMs)
            {
                _buffer.ClearBuffer();
                _drops++;
            }
            _buffer.AddSamples(pcm, 0, pcm.Length);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Log.Info($"Call audio: {_packets} caller packets received, {_drops} backlog drops");
        }
        try { _output.Stop(); } catch { /* ignore */ }
        try { _output.Dispose(); } catch (Exception ex) { Log.Error("Closing playback failed", ex); }
    }

    // ITU-T G.711 decoders (same math as the reference g711.c).
    private static short MuLawToLinear(byte u)
    {
        u = (byte)~u;
        int t = ((u & 0x0F) << 3) + 0x84;
        t <<= (u & 0x70) >> 4;
        return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }

    private static short ALawToLinear(byte a)
    {
        a ^= 0x55;
        int t = (a & 0x0F) << 4;
        int seg = (a & 0x70) >> 4;
        t = seg switch
        {
            0 => t + 8,
            1 => t + 0x108,
            _ => (t + 0x108) << (seg - 1),
        };
        return (short)((a & 0x80) != 0 ? t : -t);
    }
}