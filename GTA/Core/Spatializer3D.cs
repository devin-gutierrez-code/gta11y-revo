using System;
using NAudio.Wave;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// Places a mono cue at a point in space relative to the listener. Mono in, stereo out —
    /// the same contract <c>PanningSampleProvider</c> has, so it drops into every emitter that
    /// used the panner without restructuring any of them.
    ///
    /// WHY THIS REPLACES THE PAN. The old cue ended in
    /// <c>new PanningSampleProvider(shaped) { Pan = clamp(dot(toTarget, right) * 1.4f) }</c>.
    /// That is one scalar, and it cannot express two things a blind player needs:
    ///
    ///   * FRONT FROM BACK. A target 30 degrees ahead-right and one 150 degrees behind-right
    ///     have the same dot-with-right, so they had the same pan. The nav scanner papered over
    ///     this by making BEHIND its own zone with an octave-down double tick, which works for
    ///     four fixed zones and cannot work for a continuous bearing or for a scan sweep.
    ///   * ELEVATION. Los Santos has staircases, overpasses, rooftops and underpasses, and the
    ///     old cue said nothing about any of them.
    ///
    /// HOW EACH CUE IS PRODUCED, and why
    ///
    ///   AZIMUTH — constant-power pan on sin(azimuth). Unchanged in spirit from the old pan,
    ///   but now driven by a real bearing instead of a dot product, and paired with:
    ///
    ///   ITD (interaural time difference) — the far ear is delayed by Woodworth's formula,
    ///   (r/c)(az + sin az) with a head radius of 8.75 cm. Peaks at about 0.66 ms. Level
    ///   differences alone localise poorly at the low frequencies this mod's tones live at
    ///   (330-620 Hz); below roughly 1.5 kHz, timing is what the auditory system actually
    ///   uses. The delay is INTERPOLATED because one sample at 44.1 kHz is 23 microseconds and
    ///   the just-noticeable ITD is nearer 10.
    ///
    ///   FRONT/BACK — a low-pass that closes as the source moves behind, standing in for the
    ///   pinna's own filtering, plus a small level trim. This is the cue that resolves front
    ///   from back; no amount of panning can. It is approximate — a real HRTF is measured, not
    ///   computed — but it is the right MECHANISM, and it is unambiguous in the direction that
    ///   matters: rear sources sound duller, never merely quieter.
    ///
    ///   DISTANCE — inverse-ish gain plus an air-absorption low-pass. Deliberately gentle, and
    ///   floored: distance is already carried by the iter-37 pulse RATE, which is proven and
    ///   stays the primary channel. This is a reinforcing cue, not a replacement, and a distant
    ///   obstacle must never fade to inaudible — inaudible is indistinguishable from absent,
    ///   which is the one failure this mod cannot afford.
    ///
    ///   ELEVATION — NOT rendered here, on purpose. The genuine spectral elevation cue is a
    ///   notch that moves through 7-9 kHz, and these sources are narrowband tones an octave or
    ///   two above middle C. A notch up there would be filtering silence: it would measure as
    ///   "implemented" and be perfectly inaudible. <see cref="ElevationDegrees"/> is exposed so
    ///   the caller can emit an explicit, learnable chirp instead — a symbolic cue, honestly
    ///   labelled, rather than a psychoacoustic one that does nothing.
    ///
    /// COORDINATES are listener-relative and right-handed-ish, in metres:
    /// <c>right</c> positive to the listener's right, <c>forward</c> positive ahead,
    /// <c>up</c> positive above. The caller owns the choice of listener frame (body, camera or
    /// vehicle); this class only knows the offset it was handed.
    /// </summary>
    internal sealed class Spatializer3D : ISampleProvider
    {
        private const double HEAD_RADIUS_M = 0.0875;
        private const double SPEED_OF_SOUND = 343.0;
        private const int MAX_DELAY_SAMPLES = 128;   // 2.9 ms at 44.1 kHz, far past any real ITD

        // Distance model. REF_DIST is "as close as it gets"; beyond it the gain decays on a
        // square-root curve and stops at FLOOR so nothing ever becomes unhearable.
        private const float REF_DIST_M = 3.0f;
        private const float GAIN_FLOOR = 0.30f;

        private readonly ISampleProvider source;
        private readonly WaveFormat waveFormat;

        private readonly float gainLeft;
        private readonly float gainRight;
        private readonly bool delayRight;         // which ear is the far one
        private readonly int delayWhole;
        private readonly float delayFrac;
        private readonly float distanceGain;

        private readonly float pinnaA;            // one-pole coefficient, front/back
        private readonly float airA;              // one-pole coefficient, distance
        private float pinnaZ;
        private float airZ;

        private readonly float[] delayLine = new float[MAX_DELAY_SAMPLES];
        private int delayPos;

        private float[] monoBuf = new float[1024];
        private bool sourceDrained;
        private int flushFramesLeft;

        /// <summary>Signed elevation of the source, degrees; positive is above the listener.
        /// Not rendered by this filter — see the class remarks.</summary>
        public float ElevationDegrees { get; private set; }

        /// <summary>Distance to the source in metres, as handed in.</summary>
        public float Distance { get; private set; }

        /// <summary>Signed azimuth in degrees; 0 ahead, positive to the right, +/-180 behind.</summary>
        public float AzimuthDegrees { get; private set; }

        /// <param name="source">Mono cue, already enveloped.</param>
        /// <param name="right">Listener-relative offset, metres, positive right.</param>
        /// <param name="forward">Listener-relative offset, metres, positive ahead.</param>
        /// <param name="up">Listener-relative offset, metres, positive above.</param>
        /// <param name="applyDistance">False for cues where distance carries no meaning (menu
        /// earcons, confirmations) — they keep full level and full bandwidth.</param>
        public Spatializer3D(ISampleProvider source, float right, float forward, float up,
            bool applyDistance = true)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (source.WaveFormat.Channels != 1)
                throw new ArgumentException("Spatializer3D requires a mono source", "source");

            this.source = source;
            int rate = source.WaveFormat.SampleRate;
            waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);

            double horiz = Math.Sqrt((double)right * right + (double)forward * forward);
            double dist = Math.Sqrt(horiz * horiz + (double)up * up);
            // A degenerate offset (the caller could not resolve a position) must not divide by
            // zero and must not silently render as dead ahead. Treat it as directly overhead at
            // arm's length: audible, centred, and obviously not a bearing.
            if (dist < 0.001 || double.IsNaN(dist))
            {
                dist = 1.0;
                horiz = 0.0;
            }
            Distance = (float)dist;

            // GTA.Math is in scope in this assembly's other files and shadows System.Math on a
            // bare Math.Atan2 there; this file is deliberately free of GTA types so the
            // System.Math here is unambiguous.
            double az = Math.Atan2(right, forward);          // 0 ahead, +right
            AzimuthDegrees = (float)(az * 180.0 / Math.PI);
            ElevationDegrees = (float)(Math.Asin(Math.Max(-1.0, Math.Min(1.0, up / dist)))
                                       * 180.0 / Math.PI);

            // ---- azimuth: constant power -------------------------------------------------
            // sin(az) collapses front and back onto the same pan. That is intentional: the
            // pinna filter below carries front/back, exactly as it does in a real head.
            double p = Math.Sin(az);
            // Elevated sources sit closer to the median plane; narrowing the pan with elevation
            // keeps a cue directly overhead from reading as hard-left or hard-right.
            double elevNarrow = horiz / dist;
            p *= elevNarrow;
            double panAngle = (p + 1.0) * Math.PI / 4.0;      // 0 .. pi/2
            gainLeft = (float)Math.Cos(panAngle);
            gainRight = (float)Math.Sin(panAngle);

            // ---- ITD ---------------------------------------------------------------------
            double itdSeconds = (HEAD_RADIUS_M / SPEED_OF_SOUND) * (Math.Abs(az) + Math.Sin(Math.Abs(az)));
            if (az < 0) itdSeconds = -itdSeconds;             // negative az = source to the left
            double itdSamples = Math.Abs(itdSeconds) * rate * elevNarrow;
            if (itdSamples > MAX_DELAY_SAMPLES - 2) itdSamples = MAX_DELAY_SAMPLES - 2;
            delayRight = itdSeconds < 0;                      // source left => right ear is far
            delayWhole = (int)itdSamples;
            delayFrac = (float)(itdSamples - delayWhole);

            // ---- front/back: the pinna stand-in -------------------------------------------
            double cosAz = Math.Cos(az);
            double pinnaFc = 3500.0 + 16500.0 * (0.5 * (1.0 + cosAz));
            pinnaA = OnePole(pinnaFc, rate);
            float rearTrim = (float)(1.0 - 0.20 * ((1.0 - cosAz) * 0.5));

            // ---- distance ------------------------------------------------------------------
            if (applyDistance)
            {
                float d = (float)Math.Max(dist, REF_DIST_M);
                float falloff = (float)Math.Sqrt(REF_DIST_M / d);
                distanceGain = GAIN_FLOOR + (1f - GAIN_FLOOR) * falloff;
                double airFc = 2500.0 + 15500.0 * Math.Exp(-dist / 25.0);
                airA = OnePole(airFc, rate);
            }
            else
            {
                distanceGain = 1f;
                airA = 1f;                                    // pass-through
            }
            distanceGain *= rearTrim;
        }

        /// <summary>One-pole low-pass coefficient for a cutoff in Hz. Clamped below Nyquist so
        /// a very open filter degenerates to pass-through rather than to instability.</summary>
        private static float OnePole(double cutoffHz, int sampleRate)
        {
            double nyq = sampleRate * 0.5;
            if (cutoffHz >= nyq * 0.98) return 1f;
            double a = 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / sampleRate);
            return (float)Math.Max(0.0, Math.Min(1.0, a));
        }

        public WaveFormat WaveFormat { get { return waveFormat; } }

        public int Read(float[] buffer, int offset, int count)
        {
            int framesWanted = count / 2;
            if (framesWanted <= 0) return 0;

            if (monoBuf.Length < framesWanted) monoBuf = new float[framesWanted];

            int framesRead = 0;
            if (!sourceDrained)
            {
                framesRead = source.Read(monoBuf, 0, framesWanted);
                if (framesRead < framesWanted)
                {
                    sourceDrained = true;
                    // Let the ITD delay line empty rather than clipping the tail. It is under a
                    // millisecond, but a truncated delay line ends on a discontinuity, and a
                    // click is exactly what EnvelopeSampleProvider exists to remove.
                    flushFramesLeft = delayWhole + 2;
                    for (int i = framesRead; i < framesWanted; i++) monoBuf[i] = 0f;
                }
            }
            else
            {
                for (int i = 0; i < framesWanted; i++) monoBuf[i] = 0f;
            }

            int framesToEmit = framesWanted;
            if (sourceDrained)
            {
                if (flushFramesLeft <= 0) return 0;
                int tail = framesRead + flushFramesLeft;
                if (tail < framesToEmit) framesToEmit = tail;
                flushFramesLeft -= Math.Max(0, framesToEmit - framesRead);
            }

            int w = offset;
            for (int i = 0; i < framesToEmit; i++)
            {
                float s = monoBuf[i];

                // Air absorption, then the pinna filter. Cascaded rather than merged: two
                // one-poles give a steeper, more convincing rear roll-off than either alone,
                // and each stays independently readable.
                airZ += airA * (s - airZ);
                float a = airZ;
                pinnaZ += pinnaA * (a - pinnaZ);
                float shaped = pinnaZ * distanceGain;

                // Interpolated fractional delay on the far ear.
                delayLine[delayPos] = shaped;
                int i0 = delayPos - delayWhole;
                if (i0 < 0) i0 += MAX_DELAY_SAMPLES;
                int i1 = i0 - 1;
                if (i1 < 0) i1 += MAX_DELAY_SAMPLES;
                float delayed = delayLine[i0] + (delayLine[i1] - delayLine[i0]) * delayFrac;
                delayPos++;
                if (delayPos >= MAX_DELAY_SAMPLES) delayPos = 0;

                float l = delayRight ? shaped : delayed;
                float r = delayRight ? delayed : shaped;

                buffer[w++] = l * gainLeft;
                buffer[w++] = r * gainRight;
            }

            return framesToEmit * 2;
        }
    }
}
