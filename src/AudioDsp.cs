using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WolfSpeak;

/// <summary>2nd-order IIR filter (RBJ cookbook), direct form I.</summary>
public sealed class Biquad
{
    readonly double b0, b1, b2, a1, a2;
    double x1, x2, y1, y2;

    Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0;
        this.a1 = a1 / a0; this.a2 = a2 / a0;
    }

    /// <summary>Butterworth high-pass: removes DC offset, rumble, desk thumps and mains hum below the voice band.</summary>
    public static Biquad HighPass(double cutoffHz, double sampleRate, double q = 0.7071)
    {
        double w0 = 2 * Math.PI * cutoffHz / sampleRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Process(float x)
    {
        double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1; x1 = x;
        y2 = y1; y1 = y;
        return (float)y;
    }

    public void Reset() => x1 = x2 = y1 = y2 = 0;
}

public static class Dsp
{
    /// <summary>Transparent below 0.8, then a smooth tanh knee up to 1.0 — no harsh digital clipping when boosting.</summary>
    public static float SoftClip(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.8f) return x;
        return Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
    }

    public static void FadeIn(Span<float> s)
    {
        for (int i = 0; i < s.Length; i++) s[i] *= (float)i / s.Length;
    }

    public static void FadeOut(Span<float> s)
    {
        for (int i = 0; i < s.Length; i++) s[i] *= 1f - (float)(i + 1) / s.Length;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [ThreadStatic] static bool proAudio;

    /// <summary>Registers the calling thread with MMCSS "Pro Audio" so Windows schedules it ahead of normal work (fewer dropouts under game load).</summary>
    public static void EnsureProAudioThread()
    {
        if (proAudio) return;
        proAudio = true;
        try { uint index = 0; AvSetMmThreadCharacteristics("Pro Audio", ref index); } catch { }
    }
}

/// <summary>Pass-through that raises the render thread to MMCSS Pro Audio on first use.</summary>
public sealed class ProAudioProvider(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        Dsp.EnsureProAudioThread();
        return source.Read(buffer, offset, count);
    }
}
