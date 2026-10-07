using NAudio.Wave;

namespace WolfSpeak;

public enum SoundKind { Ring, Ringback, Connected, Ended, MuteOn, MuteOff }

/// <summary>Plays short synthesized UI sounds (ring, connect, hang-up) through the output mixer.</summary>
public sealed class SoundPlayer : ISampleProvider
{
    static readonly Dictionary<SoundKind, float[]> Clips = new()
    {
        [SoundKind.Ring] = Notes(0.22, (880, 110), (0, 50), (1175, 150), (0, 140), (880, 110), (0, 50), (1175, 150)),
        [SoundKind.Ringback] = Notes(0.10, (523, 160), (0, 60), (659, 260)),
        [SoundKind.Connected] = Notes(0.18, (523, 80), (659, 80), (784, 200)),
        [SoundKind.Ended] = Notes(0.18, (784, 80), (659, 80), (440, 240)),
        [SoundKind.MuteOn] = Notes(0.14, (660, 45), (440, 70)),
        [SoundKind.MuteOff] = Notes(0.14, (440, 45), (660, 70)),
    };

    readonly Queue<float[]> queue = new();
    readonly object sync = new();
    float[]? current;
    int pos;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceEngine.SampleRate, 1);

    public void Play(SoundKind kind)
    {
        lock (sync)
        {
            if (queue.Count < 2) queue.Enqueue(Clips[kind]);
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

    /// <summary>Soft bell-ish tones: sine + a bit of 2nd harmonic, fast attack, exponential decay.</summary>
    static float[] Notes(double amp, params (double Freq, int Ms)[] notes)
    {
        int rate = VoiceEngine.SampleRate;
        var output = new List<float>();
        foreach (var (freq, ms) in notes)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                if (freq <= 0) { output.Add(0); continue; }
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-3.5 * i / n) * Math.Min(1, (n - i) / (rate * 0.004));
                double s = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(4 * Math.PI * freq * t);
                output.Add((float)(amp * env * s));
            }
        }
        return [.. output];
    }
}
