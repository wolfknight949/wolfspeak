namespace WolfSpeak;

/// <summary>Streaming linear-interpolation resampler (good enough for voice, zero added latency).</summary>
public sealed class LinearResampler(int inRate, int outRate)
{
    readonly double step = (double)inRate / outRate;
    double pos;
    float prev;

    public int MaxOutput(int inputLength) => (int)(inputLength / step) + 2;

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty) return 0;

        int n = 0;
        while (true)
        {
            int i0 = (int)Math.Floor(pos);
            if (i0 + 1 >= input.Length) break;
            double frac = pos - i0;
            float a = i0 < 0 ? prev : input[i0];
            float b = input[i0 + 1];
            output[n++] = (float)(a + (b - a) * frac);
            pos += step;
        }

        pos -= input.Length;
        prev = input[^1];
        return n;
    }
}
