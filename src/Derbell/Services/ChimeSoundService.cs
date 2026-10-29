using System.IO;
using System.Media;
using System.Text;

namespace Derbell.Services;

public static class ChimeSoundService
{
    /// <summary>
    /// Plays a rich, resonant, long "Ding-Dong" acoustic doorbell sound asynchronously on a background thread.
    /// </summary>
    public static void PlayRelaxingChime(int volumePercent = 85)
    {
        int clampedVolume = Math.Clamp(volumePercent, 0, 100);
        if (clampedVolume == 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                byte[] wavBytes = GenerateRelaxingChimeWav(clampedVolume / 100.0);
                using var ms = new MemoryStream(wavBytes);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch
            {
                // Ignore audio device errors if no audio endpoint is active
            }
        });
    }

    /// <summary>
    /// Generates a 44.1kHz 16-bit stereo PCM WAV buffer containing a rich, resonant, extended two-tone "Ding-Dong"
    /// electro-mechanical doorbell chime with authentic inharmonic modal physics and subtle entryway acoustic resonance (~2.8 seconds).
    /// </summary>
    public static byte[] GenerateRelaxingChimeWav(double volumeScale = 0.85)
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const int bitsPerSample = 16;
        const double durationSeconds = 2.85;

        int numSamples = (int)(sampleRate * durationSeconds);
        int dataChunkSize = numSamples * channels * (bitsPerSample / 8);

        double masterGain = Math.Clamp(volumeScale, 0.0, 1.0);

        // Classic Solenoid Doorbell Chime:
        // "Ding" = E5 (659.25 Hz) at 0.00s
        // "Dong" = C5 (523.25 Hz) at 0.68s (relaxed, natural chime interval)
        const double dingFreq = 659.25; // E5
        const double dongFreq = 523.25; // C5
        const double dongStartSec = 0.68;

        // Raw stereo dry buffers
        double[] dryLeft = new double[numSamples];
        double[] dryRight = new double[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;

            // "Ding" (E5) with natural metallic strike and long resonant decay
            double ding = SynthesizeChimeBar(t, dingFreq);

            // "Dong" (C5) striking at 0.68s and sustaining through the tail
            double dong = 0.0;
            if (t >= dongStartSec)
            {
                double tDong = t - dongStartSec;
                dong = SynthesizeChimeBar(tDong, dongFreq);
            }

            // Spatial positioning: Ding bar slightly left, Dong bar slightly right
            dryLeft[i] = (ding * 0.70) + (dong * 0.45);
            dryRight[i] = (ding * 0.45) + (dong * 0.70);
        }

        // Apply entryway acoustic reverberation (early reflections + comb diffusion)
        // Delays in samples: ~14ms, ~23ms, ~35ms, ~43ms
        int d1 = (int)(sampleRate * 0.014);
        int d2 = (int)(sampleRate * 0.023);
        int d3 = (int)(sampleRate * 0.035);
        int d4 = (int)(sampleRate * 0.043);

        double[] wetLeft = new double[numSamples];
        double[] wetRight = new double[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double rL = 0.0;
            double rR = 0.0;

            if (i >= d1) rL += dryLeft[i - d1] * 0.22;
            if (i >= d2) rR += dryRight[i - d2] * 0.20;
            if (i >= d3)
            {
                rL += wetLeft[i - d3] * 0.16;
                rR += dryLeft[i - d3] * 0.14;
            }
            if (i >= d4)
            {
                rR += wetRight[i - d4] * 0.16;
                rL += dryRight[i - d4] * 0.14;
            }

            wetLeft[i] = dryLeft[i] + rL;
            wetRight[i] = dryRight[i] + rR;
        }

        // Find peak across both channels for normalization
        double maxPeak = 0.001;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            // Smooth fade-out over the final 80ms to ensure a clean release
            double remaining = durationSeconds - t;
            double tailFade = remaining < 0.080 ? Math.Max(0.0, remaining / 0.080) : 1.0;

            wetLeft[i] *= tailFade;
            wetRight[i] *= tailFade;

            double peakL = Math.Abs(wetLeft[i]);
            double peakR = Math.Abs(wetRight[i]);
            if (peakL > maxPeak) maxPeak = peakL;
            if (peakR > maxPeak) maxPeak = peakR;
        }

        double normFactor = (0.95 / maxPeak) * masterGain;

        using var ms = new MemoryStream(44 + dataChunkSize);
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            // RIFF WAVE Header
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataChunkSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt subchunk
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write((short)bitsPerSample);

            // data subchunk
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataChunkSize);

            for (int i = 0; i < numSamples; i++)
            {
                short leftPcm = (short)Math.Clamp(wetLeft[i] * normFactor * short.MaxValue, short.MinValue, short.MaxValue);
                short rightPcm = (short)Math.Clamp(wetRight[i] * normFactor * short.MaxValue, short.MinValue, short.MaxValue);
                writer.Write(leftPcm);
                writer.Write(rightPcm);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Synthesizes an authentic acoustic chime bar struck by a solenoid plunger based on Euler-Bernoulli beam modal physics.
    /// Includes the fundamental with gentle acoustic beating, sub-octave resonator hum, inharmonic chime partials, and solenoid impact transient.
    /// </summary>
    private static double SynthesizeChimeBar(double t, double freq)
    {
        if (t <= 0.0) return 0.0;

        // Crisp 2.5ms attack envelope (raised cosine) preventing DC step and audio clicks
        double attack = t < 0.0025
            ? 0.5 * (1.0 - Math.Cos(Math.PI * (t / 0.0025)))
            : 1.0;

        // 1. Fundamental mode with subtle ~1.6 Hz beating (simulates microscopic physical asymmetry of acoustic chime bar)
        // Sustained long decay (decay constant 1.15 = rings for ~2.5s)
        const double beatRate = 1.6;
        double f1 = (0.62 * Math.Sin(2.0 * Math.PI * freq * t) +
                     0.38 * Math.Sin(2.0 * Math.PI * (freq + beatRate) * t))
                    * Math.Exp(-t * 1.15);

        // 2. Resonator chamber sub-octave hum (gives warmth and deep acoustic presence to the chime bar)
        double fHum = Math.Sin(2.0 * Math.PI * (freq * 0.5) * t) * 0.18 * Math.Exp(-t * 2.1);

        // 3. Inharmonic Chime Mode 2 (~2.756x fundamental - classic physical bar mode)
        double f2 = Math.Sin(2.0 * Math.PI * (freq * 2.756) * t) * 0.32 * Math.Exp(-t * 2.3);

        // 4. Inharmonic Chime Mode 3 (~5.404x fundamental - bright metallic chime overtone)
        double f3 = Math.Sin(2.0 * Math.PI * (freq * 5.404) * t) * 0.15 * Math.Exp(-t * 4.5);

        // 5. Inharmonic Chime Mode 4 (~8.93x fundamental - initial shimmer)
        double f4 = Math.Sin(2.0 * Math.PI * (freq * 8.93) * t) * 0.07 * Math.Exp(-t * 8.0);

        // 6. Solenoid plunger strike impact (felt/nylon tipped striker clink during first 15ms)
        double strikeImpact = (Math.Sin(2.0 * Math.PI * (freq * 11.2) * t) * 0.14 +
                               Math.Sin(2.0 * Math.PI * (freq * 16.4) * t) * 0.09)
                              * Math.Exp(-t * 90.0);

        return attack * (f1 + fHum + f2 + f3 + f4 + strikeImpact);
    }
}
