using NAudio.Wave;

namespace WolfSpeak;

public enum SoundKind { Ring, Ringback, Connected, Ended, MuteOn, MuteOff }

/// <summary>Plays short synthesized UI sounds (ring, connect, hang-up) through the output mixer.</summary>
public sealed class SoundPlayer : ISampleProvider
{
    // Rendered on a background thread at startup, not in the UI's way.
    static readonly Dictionary<SoundKind, Lazy<float[]>> Clips =
        Enum.GetValues<SoundKind>().ToDictionary(k => k, k => new Lazy<float[]>(() => Render(k)));

    readonly Queue<float[]> queue = new();
    readonly object sync = new();
    float[]? current;
    int pos;

    public SoundPlayer() => Task.Run(() => { foreach (var clip in Clips.Values) _ = clip.Value; });

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceEngine.SampleRate, 1);

    /// <summary>How long a sound plays, so the ring can repeat right after its melody (plus a pause).</summary>
    public static int LengthMs(SoundKind kind) => Clips[kind].Value.Length * 1000 / VoiceEngine.SampleRate;

    public void Play(SoundKind kind)
    {
        lock (sync)
        {
            if (queue.Count < 2) queue.Enqueue(Clips[kind].Value);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (sync)
        {
            int i = 0;
            while (i < count)
            {
                if (current is null || pos >= current.Length)
                {
                    if (!queue.TryDequeue(out current)) { current = null; break; }
                    pos = 0;
                }
                int k = Math.Min(count - i, current.Length - pos);
                Array.Copy(current, pos, buffer, offset + i, k);
                pos += k;
                i += k;
            }
            if (i < count) Array.Clear(buffer, offset + i, count - i);
        }
        return count;
    }

    // ------------------------------------------------------------------ sound design

    // Note frequencies (Hz)
    const double G5 = 783.99, A5 = 880.00, B5 = 987.77,
        C6 = 1046.50, D6 = 1174.66, E6 = 1318.51, F6 = 1396.91, G6 = 1567.98, A6 = 1760.00, C7 = 2093.00;

    const double Eighth = 0.28; // relaxed ring tempo

    /// <summary>Original gentle chime melodies, balanced by audible RMS loudness.</summary>
    internal static float[] Render(SoundKind kind)
    {
        Chime b;
        switch (kind)
        {
            case SoundKind.Ring: // incoming: a little song that climbs, hangs on G, then comes home to C (repeats after a pause)
                b = new Chime(4.5);
                b.Melody(0, Eighth,
                    (C6, 1), (E6, 1), (G6, 2), (E6, 1), (D6, 1), (C6, 2),
                    (A5, 1), (E6, 1), (D6, 1), (C6, 3));
                b.Harmony(0, 2.0, C6 / 4, E6 / 4, G6 / 4);
                b.Harmony(2.1, 2.2, A5 / 2, C6 / 4, E6 / 4);
                return b.Finish(rmsDb: -30);

            case SoundKind.Ringback: // while you wait: a soft "pi-po"
                b = new Chime(1.15);
                b.Melody(0, 0.16, (E6, 1), (C6, 2));
                return b.Finish(rmsDb: -35);

            case SoundKind.Connected: // "start" chime: gentle rising chimes
                b = new Chime(1.3);
                b.Melody(0, 0.16, (C6, 1), (E6, 1), (G6, 2));
                b.Harmony(0.12, 1.0, C6 / 4, E6 / 4, G6 / 4);
                return b.Finish(rmsDb: -32);

            case SoundKind.Ended: // "done": a short falling tune that resolves
                b = new Chime(1.5);
                b.Melody(0, 0.22, (E6, 1), (G5, 1), (C6, 2));
                b.Harmony(0.18, 1.15, C6 / 4, E6 / 4, G6 / 4);
                return b.Finish(rmsDb: -32);

            case SoundKind.MuteOn: // lower soft chime
                b = new Chime(0.65);
                b.Melody(0, 0.10, (A5, 1));
                return b.Finish(rmsDb: -35);

            case SoundKind.MuteOff: // higher soft chime
                b = new Chime(0.65);
                b.Melody(0, 0.10, (E6, 1));
                return b.Finish(rmsDb: -35);

            default:
                return [];
        }
    }

    /// <summary>Warm sine chimes with smooth attacks and overlapping, decaying tails.</summary>
    sealed class Chime(double seconds)
    {
        static readonly int Rate = VoiceEngine.SampleRate;
        readonly double[] mix = new double[(int)(seconds * Rate)];

        /// <summary>Plays notes back to back; each note's length is <paramref name="unit"/> × beats.</summary>
        public void Melody(double start, double unit, params (double Freq, int Beats)[] notes)
        {
            double t = start;
            foreach (var (freq, beats) in notes)
            {
                double length = unit * beats;
                Note(freq / 2, t, length + 0.55); // Let notes blend into a soft tail.
                t += length;
            }
        }

        public void Harmony(double start, double length, params double[] frequencies)
        {
            foreach (double freq in frequencies) Note(freq, start, length, 0.16, 0.14);
        }

        void Note(double freq, double start, double length, double gain = 1, double attackSeconds = 0.065)
        {
            int s0 = (int)(start * Rate);
            int n = Math.Min((int)(length * Rate), mix.Length - s0);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double phase = 2 * Math.PI * freq * t;
                // A warm lower octave and quiet detuned voices give the sine a fuller body.
                double tone = 0.72 * Math.Sin(phase) + 0.22 * Math.Sin(phase / 2)
                            + 0.08 * Math.Sin(phase * 1.002) + 0.08 * Math.Sin(phase * 0.998)
                            + 0.025 * Math.Sin(2 * phase) * Math.Exp(-5 * t);
                double attack = Math.Min(1, t / attackSeconds);
                double release = Math.Min(1, (n - 1 - i) / (0.32 * Rate));
                double env = (0.5 - 0.5 * Math.Cos(Math.PI * attack))
                           * (0.5 - 0.5 * Math.Cos(Math.PI * release)) * Math.Exp(-2.6 * t);
                mix[s0 + i] += tone * env * gain;
            }
        }
        /// <summary>
        /// Two-pole low-pass (~1.5 kHz), then match audible RMS and cap peaks at 0.18.
        /// </summary>
        public float[] Finish(double rmsDb)
        {
            double a = Math.Exp(-2 * Math.PI * 1500.0 / Rate), y1 = 0, y2 = 0;
            var output = new float[mix.Length];
            double max = 1e-9, sum = 0;
            int sounding = 0;
            for (int i = 0; i < mix.Length; i++)
            {
                y1 = (1 - a) * mix[i] + a * y1;
                y2 = (1 - a) * y1 + a * y2;
                output[i] = (float)y2;
                max = Math.Max(max, Math.Abs(y2));
                if (Math.Abs(y2) > 1e-4) { sum += y2 * y2; sounding++; }
            }
            double rms = Math.Sqrt(sum / Math.Max(1, sounding));
            float gain = (float)Math.Min(Math.Pow(10, rmsDb / 20) / Math.Max(rms, 1e-9), 0.18 / max);
            for (int i = 0; i < output.Length; i++) output[i] *= gain;
            return output;
        }
    }
}
