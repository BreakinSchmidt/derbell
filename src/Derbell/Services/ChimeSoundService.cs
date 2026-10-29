using System.IO;
using System.Media;
using System.Text;

namespace Derbell.Services;

public static class ChimeSoundService
{
    /// <summary>
    /// Plays a classic, clear "Ding-Dong" two-tone doorbell sound asynchronously on a background thread.
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
                // Ignore audio device errors if no speaker is connected
            }
        });
    }

    /// <summary>
    /// Generates a 44.1kHz 16-bit stereo PCM WAV buffer containing a classic two-tone "Ding-Dong" doorbell chime (E5 -> C5).
    /// </summary>
    public static byte[] GenerateRelaxingChimeWav(double volumeScale = 0.85)
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const int bitsPerSample = 16;
        const double durationSeconds = 0.95;

        int numSamples = (int)(sampleRate * durationSeconds);
        int dataChunkSize = numSamples * channels * (bitsPerSample / 8);

        double masterGain = Math.Clamp(volumeScale, 0.0, 1.0);

        // Classic Doorbell: "Ding" = E5 (659.25 Hz) at 0.0s, "Dong" = C5 (523.25 Hz) at 0.30s
        const double dingFreq = 659.25; // E5
        const double dongFreq = 523.25; // C5
        const double dongStartSec = 0.30;

        // First pass: synthesize raw signal and find peak for full-scale normalization
        double[] rawSamples = new double[numSamples];
        double maxPeak = 0.001;

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;

            // "Ding" (E5) with tubular bell overtones and crisp strike transient
            double ding = SynthesizeBellTone(t, dingFreq, decayRate: 4.8);

            // "Dong" (C5) entering at 0.30s
            double dong = 0.0;
            if (t >= dongStartSec)
            {
                double t2 = t - dongStartSec;
                dong = SynthesizeBellTone(t2, dongFreq, decayRate: 3.8);
            }

            // Smooth fade-out over the last 30ms
            double tailFade = 1.0;
            double remaining = durationSeconds - t;
            if (remaining < 0.030)
            {
                tailFade = Math.Max(0.0, remaining / 0.030);
            }

            double val = (ding + dong) * tailFade;
            rawSamples[i] = val;

            double abs = Math.Abs(val);
            if (abs > maxPeak)
            {
                maxPeak = abs;
            }
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
                double normalized = rawSamples[i] * normFactor;
                short pcm = (short)Math.Clamp(normalized * short.MaxValue, short.MinValue, short.MaxValue);
                writer.Write(pcm); // Left
                writer.Write(pcm); // Right
            }
        }

        return ms.ToArray();
    }

    private static double SynthesizeBellTone(double t, double freq, double decayRate)
    {
        if (t <= 0.0) return 0.0;

        // Crisp 3ms strike attack
        double attack = t < 0.003
            ? 0.5 * (1.0 - Math.Cos(Math.PI * (t / 0.003)))
            : 1.0;

        // Fundamental + chime harmonics (2x, 3x, and 4.2x metallic bell strike partial)
        double f1 = Math.Sin(2.0 * Math.PI * freq * t) * Math.Exp(-t * decayRate);
        double f2 = Math.Sin(2.0 * Math.PI * (freq * 2.0) * t) * 0.45 * Math.Exp(-t * (decayRate * 1.6));
        double f3 = Math.Sin(2.0 * Math.PI * (freq * 3.0) * t) * 0.25 * Math.Exp(-t * (decayRate * 2.4));
        double fStrike = Math.Sin(2.0 * Math.PI * (freq * 4.2) * t) * 0.20 * Math.Exp(-t * 22.0);

        return attack * (f1 + f2 + f3 + fStrike);
    }
}
