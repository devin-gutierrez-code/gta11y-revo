using System;
using NAudio.Wave;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// Applies a short linear attack/release gain ramp to a fixed-length sample
    /// stream, removing the click transient that a raw SignalGenerator.Take(...)
    /// produces at both ends. The nav-assist beeps are heard thousands of times
    /// per session, so the ~5 ms edges matter.
    ///
    /// NAudio's FadeInOutSampleProvider is NOT usable here: it requires a manual
    /// BeginFadeOut call and cannot auto-fade the tail of a fixed-duration take.
    /// Mono in, mono out (channel count preserved), so it composes with
    /// PanningSampleProvider which requires a mono source.
    /// </summary>
    internal sealed class EnvelopeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly long attackEndSample;
        private readonly long releaseStartSample;
        private readonly long totalSamples;
        private long position;

        public EnvelopeSampleProvider(ISampleProvider source, double durationSeconds,
            double attackMs = 5.0, double releaseMs = 8.0)
        {
            this.source = source;
            int perSecond = source.WaveFormat.SampleRate * source.WaveFormat.Channels;
            totalSamples = (long)(durationSeconds * perSecond);
            attackEndSample = Math.Max(1, (long)(attackMs / 1000.0 * perSecond));
            releaseStartSample = Math.Max(attackEndSample,
                totalSamples - (long)(releaseMs / 1000.0 * perSecond));
        }

        public WaveFormat WaveFormat { get { return source.WaveFormat; } }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            for (int i = 0; i < read; i++)
            {
                long p = position + i;
                float gain = 1f;
                if (p < attackEndSample)
                    gain = (float)p / attackEndSample;
                else if (p >= releaseStartSample && totalSamples > releaseStartSample)
                    gain = Math.Max(0f,
                        (float)(totalSamples - p) / (totalSamples - releaseStartSample));
                buffer[offset + i] *= gain;
            }
            position += read;
            return read;
        }
    }
}
