using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CmxDialer.Infrastructure;

/// <summary>Short local tone when a call connects, so agents know the line is live.</summary>
public static class Tones
{
    public static void Connected()
    {
        Task.Run(() =>
        {
            try
            {
                var tone = new SignalGenerator(16000, 1)
                {
                    Frequency = 880,
                    Gain = 0.15,
                    Type = SignalGeneratorType.Sin,
                }.Take(TimeSpan.FromMilliseconds(180));

                using var output = new WaveOutEvent();
                output.Init(tone);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                Log.Error("Connect tone failed", ex);
            }
        });
    }
}
