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

    const double Eighth = 0.14; // ring tempo

    /// <summary>
    /// Home-appliance style: a clean beeper tone playing short, cheerful major melodies, and single "pip"s for
    /// buttons. All melodies are original. Levels are matched by loudness (RMS), not peak, since a square-wave
    /// melody sounds far louder than its peak suggests.
    /// </summary>
    internal static float[] Render(SoundKind kind)
    {
        Beeper b;
        switch (kind)
        {
            case SoundKind.Ring: // incoming: a little song that climbs, hangs on G, then comes home to C (repeats after a pause)
                b = new Beeper(3.3);
                b.Melody(0, Eighth,
                    (C6, 1), (E6, 1), (G6, 1), (E6, 1), (F6, 1), (D6, 1), (B5, 1), (D6, 1), (G5, 3),
                    (A5, 1), (C6, 1), (F6, 1), (A6, 1), (G6, 1), (E6, 1), (D6, 1), (B5, 1), (C6, 4));
                return b.Finish(rmsDb: -23);

            case SoundKind.Ringback: // while you wait: a soft "pi-po"
                b = new Beeper(0.5);
                b.Melody(0, 0.11, (E6, 1), (C6, 2));
                return b.Finish(rmsDb: -30);

            case SoundKind.Connected: // "start" chime: quick rising pips
                b = new Beeper(0.6);
                b.Melody(0, 0.075, (C6, 1), (E6, 1), (G6, 1), (C7, 3));
                return b.Finish(rmsDb: -25);

            case SoundKind.Ended: // "done": a short falling tune that resolves
                b = new Beeper(1.0);
                b.Melody(0, 0.1, (G6, 1), (E6, 1), (C6, 1), (D6, 1), (B5, 1), (C6, 4));
                return b.Finish(rmsDb: -25);

            case SoundKind.MuteOn: // lower button pip
                b = new Beeper(0.09);
                b.Melody(0, 0.06, (A5, 1));
                return b.Finish(rmsDb: -28);

            case SoundKind.MuteOff: // higher button pip
                b = new Beeper(0.09);
                b.Melody(0, 0.06, (E6, 1));
                return b.Finish(rmsDb: -28);

            default:
                return [];
        }
    }

    /// <summary>
    /// Appliance beeper: a band-limited square wave, softened by a gentle low-pass the way a little
    /// speaker behind plastic sounds, each note slightly decaying with a tiny gap so notes stay distinct.
    /// </summary>
    sealed class Beeper(double seconds)
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
                Note(freq, t, length - 0.012); // 12 ms of air between notes
                t += length;
            }
        }

        void Note(double freq, double start, double length)
        {
            int s0 = (int)(start * Rate);
            int n = Math.Min((int)(length * Rate), mix.Length - s0);
            double dt = freq / Rate, phase = 0;
            int attack = (int)(0.003 * Rate), release = (int)(0.008 * Rate);
            for (int i = 0; i < n; i++)
            {
                phase += dt;
                if (phase >= 1) phase -= 1;
                double s = phase < 0.5 ? 1 : -1;
                double p2 = phase - 0.5;
                if (p2 < 0) p2 += 1;
                s += Blep(phase, dt) - Blep(p2, dt);
                double env = Math.Min(1, i / (double)attack) * Math.Min(1, (n - i) / (double)release)
                           * (1 - 0.35 * i / n); // slight decay through the note
                mix[s0 + i] += s * env;
            }
        }

        /// <summary>PolyBLEP: smooths a hard edge over one sample on each side of the discontinuity.</summary>
        static double Blep(double phase, double dt)
        {
            if (phase < dt) { double x = phase / dt; return x + x - x * x - 1; }
            if (phase > 1 - dt) { double x = (phase - 1) / dt; return x * x + x + x + 1; }
            return 0;
        }

        /// <summary>
        /// Two-pole low-pass (~3.5 kHz) for the soft beeper tone, then set the loudness to <paramref name="rmsDb"/>
        /// (measured over the sounding part only), never letting peaks go above 0.5.
        /// </summary>
        public float[] Finish(double rmsDb)
        {
            double a = Math.Exp(-2 * Math.PI * 3500.0 / Rate), y1 = 0, y2 = 0;
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
            float gain = (float)Math.Min(Math.Pow(10, rmsDb / 20) / Math.Max(rms, 1e-9), 0.5 / max);
            for (int i = 0; i < output.Length; i++) output[i] *= gain;
            return output;
        }
    }
}
