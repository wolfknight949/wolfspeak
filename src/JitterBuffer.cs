using System.Diagnostics;
using NAudio.Wave;

namespace WolfSpeak;

/// <summary>
/// Per-peer playout buffer.
/// <list type="bullet">
/// <item>Audio arrives in bursts (a 10 ms sound card delivers two 5 ms packets at once) and is read in
///   device-period chunks. The buffer measures both and primes with <c>burst + safety</c>, so it starts
///   as small as possible without starving.</item>
/// <item>Backlog / clock drift: every 0.5 s it looks at the <em>smallest</em> amount left after a read.
///   Only if that floor sits above the safety margin is there real excess delay, which is removed by
///   playing 0.5% faster (inaudible) for the next 0.5 s — or 2% after a big hiccup. The rate never
///   flips chunk by chunk, so there is no pitch wobble.</item>
/// <item>Auto mode: the safety margin grows 2 ms on each mid-speech underrun and shrinks 1 ms after
///   every 10 s without one.</item>
/// <item>Packet loss: the gap is concealed by repeating the last 10 ms with a fast fade, then playback
///   fades back in. A sender-marked end of speech skips concealment.</item>
/// </list>
/// Always returns the requested count so the mixer never removes it.
/// </summary>
public sealed class JitterBuffer : ISampleProvider
{
    const int Rate = VoiceEngine.SampleRate;
    const int Ms = Rate / 1000;
    const int HistoryLength = 10 * Ms;  // 10 ms of last played audio, for concealment
    const int FadeInSamples = Rate / 400; // 2.5 ms
    const int Window = Rate / 2;          // control window: 0.5 s of playback
    const int AutoSafetyStart = 3 * Ms, AutoSafetyMin = 1 * Ms, AutoSafetyMax = 60 * Ms;
    static readonly float ConcealDecay = MathF.Exp(MathF.Log(0.001f) / (Rate * 0.03f)); // −60 dB in 30 ms
    static readonly long BurstGapTicks = Stopwatch.Frequency / 500; // writes < 2 ms apart belong to one burst

    readonly float[] ring = new float[Rate]; // 1 s
    readonly float[] history = new float[HistoryLength];
    readonly object sync = new();
    float[] scratch = new float[4096];
    int readPos, count, historyPos, fadeInLeft;
    bool playing, ended;
    float concealGain;

    // sizing
    bool adaptive;
    int manualTarget = 20 * Ms;
    int autoSafety = AutoSafetyStart;
    int maxBurst = 10 * Ms, maxRead = 10 * Ms;   // observed, refreshed every few seconds
    int burstAccum, windowMaxBurst, windowMaxRead;
    long lastWriteTicks;

    // backlog control
    int windowMinLeft = int.MaxValue, windowSamples, sizeWindowSamples, samplesSinceUnderrun;
    double speedUp, extraAccum;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    public volatile bool Muted;

    /// <summary>Mid-speech underruns (each one is a concealed gap).</summary>
    public int Underruns { get; private set; }

    public bool Adaptive
    {
        get => adaptive;
        set { lock (sync) { if (value && !adaptive) autoSafety = AutoSafetyStart; adaptive = value; } }
    }

    /// <summary>Manual mode: total buffer (prime level) in samples.</summary>
    public int TargetSamples
    {
        get { lock (sync) return Prime; }
        set { lock (sync) manualTarget = Math.Clamp(value, VoiceEngine.FrameSamples, ring.Length / 4); }
    }

    public int TargetMs => TargetSamples / Ms;

    /// <summary>Queued audio in milliseconds.</summary>
    public int BufferedMs { get { lock (sync) return count / Ms; } }

    int Chunk => Math.Max(maxBurst, maxRead);
    int Safety => adaptive ? autoSafety : Math.Max(Ms, manualTarget - Chunk);
    int Prime => Safety + Chunk;

    public void Write(ReadOnlySpan<float> samples, bool endOfSpeech = false)
    {
        lock (sync)
        {
            long now = Stopwatch.GetTimestamp();
            burstAccum = now - lastWriteTicks < BurstGapTicks ? burstAccum + samples.Length : samples.Length;
            lastWriteTicks = now;
            windowMaxBurst = Math.Max(windowMaxBurst, burstAccum);
            maxBurst = Math.Max(maxBurst, burstAccum);

            int len = ring.Length;
            int writePos = (readPos + count) % len;
            int first = Math.Min(samples.Length, len - writePos);
            samples[..first].CopyTo(ring.AsSpan(writePos));
            samples[first..].CopyTo(ring);
            count += samples.Length;
            ended = endOfSpeech;

            // Safety net only (e.g. after the app was stalled); normal backlog drains via the speed-up.
            if (count > Math.Max(Prime * 4, Rate / 10))
            {
                int drop = count - Prime;
                readPos = (readPos + drop) % len;
                count -= drop;
            }
        }
    }

    public int Read(float[] buffer, int offset, int n)
    {
        lock (sync)
        {
            windowMaxRead = Math.Max(windowMaxRead, n);
            maxRead = Math.Max(maxRead, n);
            RefreshSizes(n);

            if (!playing && count >= Prime)
            {
                playing = true;
                fadeInLeft = FadeInSamples;
                speedUp = 0;
                windowMinLeft = int.MaxValue;
                windowSamples = 0;
            }

            int produced = 0;
            if (playing)
            {
                extraAccum += n * speedUp;
                int extra = (int)extraAccum;
                extraAccum -= extra;
                int want = n + extra;

                if (count >= want && extra > 0)
                {
                    if (scratch.Length < want) scratch = new float[want * 2];
                    Take(scratch.AsSpan(0, want));
                    Stretch(scratch.AsSpan(0, want), buffer.AsSpan(offset, n));
                    produced = n;
                }
                else if (count >= n)
                {
                    Take(buffer.AsSpan(offset, n));
                    produced = n;
                }
                else
                {
                    produced = count;
                    Take(buffer.AsSpan(offset, produced));
                    playing = false; // underrun → conceal, then re-prime
                    concealGain = ended ? 0f : 1f;
                    if (!ended)
                    {
                        // Starved mid-speech: the stream is jitterier than our margin → widen it.
                        Underruns++;
                        if (adaptive) autoSafety = Math.Min(AutoSafetyMax, autoSafety + 2 * Ms);
                        samplesSinceUnderrun = 0;
                    }
                }

                if (playing)
                {
                    windowMinLeft = Math.Min(windowMinLeft, count);
                    windowSamples += n;
                    if (windowSamples >= Window)
                    {
                        // The floor of what was left after reads = delay we never needed.
                        int excess = windowMinLeft - Safety;
                        speedUp = excess > 20 * Ms ? 0.02 : excess > 2 * Ms ? 0.005 : 0;
                        windowMinLeft = int.MaxValue;
                        windowSamples = 0;
                    }
                    if (adaptive && (samplesSinceUnderrun += n) > Rate * 10)
                    {
                        samplesSinceUnderrun = 0;
                        autoSafety = Math.Max(AutoSafetyMin, autoSafety - Ms);
                    }
                }

                for (int i = 0; i < produced && fadeInLeft > 0; i++, fadeInLeft--)
                    buffer[offset + i] *= 1f - (float)fadeInLeft / FadeInSamples;
                Remember(buffer.AsSpan(offset, produced));
            }

            // Conceal the gap: repeat recent audio with a fast fade-out, then silence.
            for (int i = produced; i < n; i++)
            {
                buffer[offset + i] = concealGain > 0.0005f ? history[historyPos++ % HistoryLength] * concealGain : 0f;
                concealGain *= ConcealDecay;
            }
        }

        if (Muted)
            Array.Clear(buffer, offset, n);
        return n;
    }

    /// <summary>Every ~4 s, let the burst/read size estimates follow the current stream (e.g. after a device change).</summary>
    void RefreshSizes(int n)
    {
        if ((sizeWindowSamples += n) < Rate * 4) return;
        sizeWindowSamples = 0;
        if (windowMaxBurst > 0) maxBurst = windowMaxBurst;
        if (windowMaxRead > 0) maxRead = windowMaxRead;
        windowMaxBurst = windowMaxRead = 0;
    }

    void Take(Span<float> dest)
    {
        int len = ring.Length;
        int first = Math.Min(dest.Length, len - readPos);
        ring.AsSpan(readPos, first).CopyTo(dest);
        ring.AsSpan(0, dest.Length - first).CopyTo(dest[first..]);
        readPos = (readPos + dest.Length) % len;
        count -= dest.Length;
    }

    static void Stretch(ReadOnlySpan<float> src, Span<float> dst)
    {
        double step = (double)(src.Length - 1) / Math.Max(1, dst.Length - 1);
        for (int i = 0; i < dst.Length; i++)
        {
            double p = i * step;
            int i0 = (int)p;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            dst[i] = (float)(src[i0] + (src[i1] - src[i0]) * (p - i0));
        }
    }

    void Remember(ReadOnlySpan<float> played)
    {
        if (played.Length >= HistoryLength)
            played[^HistoryLength..].CopyTo(history);
        else
        {
            Array.Copy(history, played.Length, history, 0, HistoryLength - played.Length);
            played.CopyTo(history.AsSpan(HistoryLength - played.Length));
        }
        historyPos = 0;
    }

    public void Clear()
    {
        lock (sync) { readPos = 0; count = 0; playing = false; concealGain = 0; ended = false; speedUp = 0; }
    }
}
