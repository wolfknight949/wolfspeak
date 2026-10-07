using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WolfSpeak;

/// <summary>Device sample format as reported by WASAPI.</summary>
public readonly record struct DeviceFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int BlockAlign)
{
    public override string ToString() => $"{SampleRate / 1000.0:0.#} kHz {Channels} ch {(IsFloat ? "float" : $"{BitsPerSample}-bit")}";
}

/// <summary>
/// Minimal direct WASAPI streams.
///
/// Why not NAudio's WasapiOut/WasapiCapture: classic shared mode always runs a ~10 ms engine period with a
/// ~22 ms buffer, and NAudio keeps that buffer completely full. Here we use <c>IAudioClient3</c> low-latency
/// shared mode (Windows 10+): the engine period drops to the device minimum (typically 3 ms) and we keep just
/// two periods queued. Falls back to the classic path automatically if the device/driver refuses.
/// </summary>
public abstract class LowLatencyStream : IDisposable
{
    protected readonly IAudioClient3 Client;
    protected readonly DeviceFormat Format;
    protected readonly int PeriodFrames;
    protected readonly int BufferFrames;
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    volatile bool running;

    /// <summary>Engine period in ms (how often we're woken up).</summary>
    public double PeriodMs => PeriodFrames * 1000.0 / Format.SampleRate;
    public bool IsLowLatency { get; }
    public DeviceFormat DeviceFormat => Format;
    public event Action<Exception>? Failed;

    protected LowLatencyStream(string? deviceId, bool capture, string threadName, bool lowLatency)
    {
        var device = CoreAudio.GetDevice(deviceId, capture);
        var iid = typeof(IAudioClient3).GUID;
        device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out var obj);
        Client = (IAudioClient3)obj;

        Client.GetMixFormat(out var mixPtr);
        try
        {
            Format = CoreAudio.ReadFormat(mixPtr);
            const uint EventCallback = 0x00040000;
            bool done = false;
            if (lowLatency)
            {
                try
                {
                    Client.GetSharedModeEnginePeriod(mixPtr, out _, out _, out uint minPeriod, out _);
                    Client.InitializeSharedAudioStream(EventCallback, minPeriod, mixPtr, IntPtr.Zero);
                    IsLowLatency = done = true;
                }
                catch { /* driver/Windows doesn't support it → classic below */ }
            }
            if (!done)
            {
                // Classic shared mode: Windows' standard ~10 ms engine period, rock solid everywhere.
                Client.Initialize(0, EventCallback, 200_000, 0, mixPtr, IntPtr.Zero);
                IsLowLatency = false;
            }
        }
        finally { Marshal.FreeCoTaskMem(mixPtr); }

        ConfigureSession();
        Client.GetBufferSize(out uint bufferFrames);
        BufferFrames = (int)bufferFrames;
        if (IsLowLatency)
        {
            Client.GetCurrentSharedModeEnginePeriod(out var curFmt, out uint period);
            Marshal.FreeCoTaskMem(curFmt);
            PeriodFrames = (int)period;
        }
        else
        {
            Client.GetDevicePeriod(out long defaultPeriod, out _);
            PeriodFrames = (int)(defaultPeriod * Format.SampleRate / 10_000_000);
        }

        Client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = threadName };
    }

    /// <summary>
    /// Names the session "WolfSpeak" (volume mixer, Sonar) and opts out of Windows' auto-ducking:
    /// voice apps on the communication device would otherwise make Windows turn games down by 80%.
    /// </summary>
    void ConfigureSession()
    {
        try
        {
            var iid = new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"); // IID_IAudioSessionControl
            Client.GetService(ref iid, out var obj);
            var session = (IAudioSessionControl2)obj;
            var context = Guid.Empty;
            session.SetDisplayName("WolfSpeak", ref context);
            if (Environment.ProcessPath is { } exe) session.SetIconPath(exe + ",0", ref context);
            session.SetDuckingPreference(true);
            Marshal.ReleaseComObject(session);
        }
        catch { /* cosmetic / best effort */ }
    }

    public void Start()
    {
        running = true;
        Prime();
        Client.Start();
        thread.Start();
    }

    protected virtual void Prime() { }
    protected abstract void Service();

    void Loop()
    {
        Dsp.EnsureProAudioThread();
        try
        {
            while (running)
            {
                if (wake.WaitOne(200) && running) Service();
            }
        }
        catch (Exception ex)
        {
            if (running) Failed?.Invoke(ex); // e.g. AUDCLNT_E_DEVICE_INVALIDATED when unplugged
        }
    }

    public void Dispose()
    {
        if (running)
        {
            running = false;
            wake.Set();
            if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(500);
        }
        try { Client.Stop(); } catch { }
        Marshal.ReleaseComObject(Client);
        wake.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Plays a mono 48 kHz <see cref="ISampleProvider"/> on a device, converting to its native format.</summary>
public sealed class LowLatencyRender : LowLatencyStream
{
    readonly ISampleProvider source;
    readonly IAudioRenderClient render;
    readonly LinearResampler? resampler;
    float[] input = new float[4096];
    float[] pending = new float[8192];
    int pendingCount;

    // Queue = one engine period (what the engine pulls each pass) + a small safety margin.
    // The margin starts at 3 ms and grows by 2 ms whenever the device ran dry, up to one more period.
    int marginFrames;
    int wakes;
    volatile int queueFrames;

    /// <summary>Audio we keep queued in the device, in ms.</summary>
    public double QueueMs => queueFrames * 1000.0 / Format.SampleRate;
    public int Underruns { get; private set; }

    public LowLatencyRender(string? deviceId, ISampleProvider source, bool lowLatency) : base(deviceId, capture: false, "WolfSpeak render", lowLatency)
    {
        this.source = source;
        var iid = typeof(IAudioRenderClient).GUID;
        Client.GetService(ref iid, out var obj);
        render = (IAudioRenderClient)obj;
        if (Format.SampleRate != source.WaveFormat.SampleRate)
            resampler = new LinearResampler(source.WaveFormat.SampleRate, Format.SampleRate);
        // Ultra-low latency: one period + 3 ms (widens itself on glitches). Otherwise the classic safe two periods.
        marginFrames = IsLowLatency ? Format.SampleRate * 3 / 1000 : PeriodFrames;
        UpdateQueue();
    }

    void UpdateQueue() => queueFrames = Math.Min(BufferFrames, PeriodFrames + marginFrames);

    protected override void Prime() => Service();

    protected override void Service()
    {
        Client.GetCurrentPadding(out uint padding);
        if (padding == 0 && ++wakes > 20 && marginFrames < PeriodFrames)
        {
            // Nothing left when the engine woke us → we were too tight. Widen the margin.
            Underruns++;
            marginFrames = Math.Min(PeriodFrames, marginFrames + Format.SampleRate * 2 / 1000);
            UpdateQueue();
        }
        else if (wakes <= 20) wakes++;

        int frames = queueFrames - (int)padding;
        if (frames <= 0) return;

        Fill(frames);
        render.GetBuffer((uint)frames, out var ptr);
        unsafe
        {
            int ch = Format.Channels;
            if (Format.IsFloat && Format.BitsPerSample == 32)
            {
                var dst = new Span<float>((void*)ptr, frames * ch);
                for (int i = 0; i < frames; i++)
                {
                    float s = Math.Clamp(pending[i], -1f, 1f);
                    int o = i * ch;
                    dst[o] = s;
                    if (ch > 1) dst[o + 1] = s;
                    for (int c = 2; c < ch; c++) dst[o + c] = 0;
                }
            }
            else if (Format.BitsPerSample == 16)
            {
                var dst = new Span<short>((void*)ptr, frames * ch);
                for (int i = 0; i < frames; i++)
                {
                    short s = (short)(Math.Clamp(pending[i], -1f, 1f) * 32767f);
                    int o = i * ch;
                    dst[o] = s;
                    if (ch > 1) dst[o + 1] = s;
                    for (int c = 2; c < ch; c++) dst[o + c] = 0;
                }
            }
            else
            {
                var dst = new Span<int>((void*)ptr, frames * ch); // 32-bit int PCM
                for (int i = 0; i < frames; i++)
                {
                    int s = (int)(Math.Clamp(pending[i], -1f, 1f) * int.MaxValue);
                    int o = i * ch;
                    dst[o] = s;
                    if (ch > 1) dst[o + 1] = s;
                    for (int c = 2; c < ch; c++) dst[o + c] = 0;
                }
            }
        }
        render.ReleaseBuffer((uint)frames, 0);

        pendingCount -= frames;
        Array.Copy(pending, frames, pending, 0, pendingCount);
    }

    /// <summary>Makes sure <see cref="pending"/> holds at least <paramref name="frames"/> device-rate samples.</summary>
    void Fill(int frames)
    {
        if (pending.Length < frames * 2 + 64) Array.Resize(ref pending, frames * 2 + 64);
        while (pendingCount < frames)
        {
            int need = frames - pendingCount;
            if (resampler is null)
            {
                if (input.Length < need) input = new float[need * 2];
                source.Read(input, 0, need);
                Array.Copy(input, 0, pending, pendingCount, need);
                pendingCount += need;
            }
            else
            {
                int inCount = (int)Math.Ceiling(need * (double)source.WaveFormat.SampleRate / Format.SampleRate) + 1;
                if (input.Length < inCount) input = new float[inCount * 2];
                source.Read(input, 0, inCount);
                int max = resampler.MaxOutput(inCount);
                if (pending.Length < pendingCount + max) Array.Resize(ref pending, (pendingCount + max) * 2);
                pendingCount += resampler.Process(input.AsSpan(0, inCount), pending.AsSpan(pendingCount));
            }
        }
    }
}

/// <summary>Delivers raw device-format microphone data every engine period.</summary>
public sealed class LowLatencyCapture : LowLatencyStream
{
    readonly IAudioCaptureClient capture;
    readonly Action<ReadOnlySpan<byte>, DeviceFormat> onData;
    byte[] scratch = new byte[16384];

    public LowLatencyCapture(string? deviceId, Action<ReadOnlySpan<byte>, DeviceFormat> onData, bool lowLatency) : base(deviceId, capture: true, "WolfSpeak capture", lowLatency)
    {
        this.onData = onData;
        var iid = typeof(IAudioCaptureClient).GUID;
        Client.GetService(ref iid, out var obj);
        capture = (IAudioCaptureClient)obj;
    }

    protected override void Service()
    {
        while (true)
        {
            Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packet));
            if (packet == 0) return;
            Marshal.ThrowExceptionForHR(capture.GetBuffer(out var ptr, out uint frames, out uint flags, out _, out _));
            int bytes = (int)frames * Format.BlockAlign;
            if (scratch.Length < bytes) scratch = new byte[bytes * 2];
            const uint Silent = 0x2;
            if ((flags & Silent) != 0) Array.Clear(scratch, 0, bytes);
            else Marshal.Copy(ptr, scratch, 0, bytes);
            capture.ReleaseBuffer(frames);
            onData(scratch.AsSpan(0, bytes), Format);
        }
    }
}

// ---------------------------------------------------------------------------- COM interop

static class CoreAudio
{
    public const int CLSCTX_ALL = 0x17;
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static IMMDevice GetDevice(string? id, bool capture)
    {
        // Create via CLSID: NAudio also declares a ComImport class for this CLSID, and "new" can resolve to its type.
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;
        try
        {
            if (id is not null) { enumerator.GetDevice(id, out var d); return d; }
            enumerator.GetDefaultAudioEndpoint(capture ? 1 : 0, 0 /* eConsole */, out var def);
            return def;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static DeviceFormat ReadFormat(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = (ushort)Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int blockAlign = (ushort)Marshal.ReadInt16(p, 12);
        int bits = (ushort)Marshal.ReadInt16(p, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
        {
            var sub = new byte[16];
            Marshal.Copy(p + 24, sub, 0, 16);
            isFloat = new Guid(sub) == FloatSubtype;
        }
        return new DeviceFormat(rate, channels, bits, isFloat, blockAlign);
    }
}


[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient3
{
    // IAudioClient
    void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    void GetBufferSize(out uint frames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr handle);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    // IAudioClient2
    void IsOffloadCapable(int category, out int capable);
    void SetClientProperties(IntPtr properties);
    void GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
    // IAudioClient3
    void GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
    void GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriod);
    void InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
}

// GetService only hands out IAudioSessionControl; the RCW cast QueryInterfaces to the "2" version.
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    // IAudioSessionControl
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    void GetGroupingParam(out Guid grouping);
    void SetGroupingParam(ref Guid grouping, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioRenderClient
{
    void GetBuffer(uint frames, out IntPtr data);
    void ReleaseBuffer(uint frames, uint flags);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
