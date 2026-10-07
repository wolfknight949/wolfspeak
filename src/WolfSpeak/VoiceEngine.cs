using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WolfSpeak;

public sealed class Peer
{
    public required uint Id { get; init; }
    public required IPEndPoint EndPoint { get; set; }
    public required JitterBuffer Buffer { get; init; }
    public string Name { get; set; } = "?";
    public long LastSeen;
    public long LastAudio;
    public volatile float Level; // 0..1, last received packet
    public ushort LastSeq;
    public bool HasSeq;
    public int Received, Lost; // audio packets this call
    /// <summary>Their app version from Hello; null until they say hello, or if they run a pre-1.2 build.</summary>
    public volatile string? Version;
    public volatile bool HelloSeen;
    public long MismatchNoticeMs;
    public bool VersionMatches => Version == VoiceEngine.AppVersion;
}

public enum CallState { Idle, Calling, Ringing, Connected }

/// <summary>
/// Peer-to-peer LAN voice calls between two people. No server, no codec: 48 kHz mono 16-bit PCM
/// in 5 ms UDP packets (~770 kbit/s, trivial for a LAN). Peers find each other with UDP broadcast.
///
/// Packet: 'W' 'S' type(1) senderId(4) payload
///   Hello: UTF-8 name + 0 + UTF-8 app version   Audio: seq(2) + 480 x int16   Bye / CallRequest / CallAccept / CallEnd: (none)
///   CallDecline: reason(1, 0 = declined, 1 = busy, 2 = different app version)   Ping / Pong: timestamp(8)
/// </summary>
public sealed class VoiceEngine : IDisposable
{
    public const int Port = 50505;
    public const int SampleRate = 48000;
    public const int FrameSamples = SampleRate / 200; // 5 ms per packet

    /// <summary>"1.2.0". Calls only connect between identical versions, so audio/protocol changes can't glitch.</summary>
    public static readonly string AppVersion = typeof(VoiceEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    const byte TypeHello = 1, TypeAudio = 2, TypeBye = 3,
        TypeCallRequest = 4, TypeCallAccept = 5, TypeCallDecline = 6, TypeCallEnd = 7,
        TypePing = 8, TypePong = 9;
    const byte DeclineNormal = 0, DeclineBusy = 1, DeclineVersion = 2;
    const int HeaderSize = 7;
    const int TickMs = 250;
    const int PeerTimeoutMs = 5000;
    const int RingTimeoutMs = 30000;
    const int GateHangoverFrames = 60; // keep sending 300 ms after voice drops below the gate

    readonly uint myId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    readonly Socket socket;
    readonly Thread rxThread;
    readonly System.Threading.Timer tickTimer;
    readonly ConcurrentDictionary<uint, Peer> peers = new();
    readonly ConcurrentDictionary<string, IPAddress> manualTargets = new();
    readonly MixingSampleProvider voiceMixer = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
    readonly MixingSampleProvider outputMixer = new(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
    readonly VolumeSampleProvider voiceVolume;
    readonly SoundPlayer sounds = new();
    readonly JitterBuffer loopback = new();
    readonly object audioLock = new();
    readonly object callLock = new();
    volatile bool running = true;
    int peerVersion;
    int bufferSamples = 960;
    int tick;

    volatile LowLatencyCapture? capture;
    LowLatencyRender? output;

    // Call state (guarded by callLock)
    volatile CallState state;
    volatile uint partnerId;
    long callStartMs, lastRequestMs;

    // Capture-thread state
    LinearResampler? resampler;
    float[] monoScratch = new float[4096];
    float[] resampleScratch = new float[4096];
    readonly float[] frame = new float[FrameSamples];
    int frameFill;
    int hangover;
    ushort txSeq;
    readonly byte[] txPacket = new byte[HeaderSize + 3 + FrameSamples * 2];
    readonly Biquad highPass = Biquad.HighPass(80, SampleRate);
    const int PreRollFrames = 1; // 5 ms sent from just before the gate opened (more would add delay to every sentence)
    readonly float[][] preRoll = [new float[FrameSamples]];
    int preRollIndex, preRollCount;
    const byte AudioFlagEnd = 1;

    public string Name { get; set; } = Environment.UserName;
    public volatile bool Muted;
    public volatile bool PushToTalk;
    public volatile int PttKey = 0x05;
    public volatile float GateDb = -50;
    public volatile float MicGain = 1f;

    public float MicLevelDb { get; private set; } = -100;
    public bool Transmitting { get; private set; }
    public int PeerVersion => Volatile.Read(ref peerVersion);
    public IEnumerable<Peer> Peers => peers.Values;

    public CallState State => state;
    public Peer? Partner => state != CallState.Idle && peers.TryGetValue(partnerId, out var p) ? p : null;
    public long ConnectedAtMs { get; private set; }
    /// <summary>Smoothed round-trip time to the partner in ms, or -1 if unknown.</summary>
    public double RttMs { get; private set; } = -1;
    /// <summary>Recent incoming audio packet loss (0..100), smoothed.</summary>
    public double LossPercent { get; private set; }
    int lastReceived, lastLost;

    /// <summary>Device/audio errors.</summary>
    public event Action<string>? Error;
    /// <summary>Call outcomes worth telling the user ("Alex declined", "Connection lost", ...).</summary>
    public event Action<string>? Notice;

    float outputVolume = 1f;
    bool deafened;

    public float OutputVolume
    {
        get => outputVolume;
        set { outputVolume = value; voiceVolume.Volume = deafened ? 0 : value; }
    }

    public bool Deafened
    {
        get => deafened;
        set { deafened = value; voiceVolume.Volume = value ? 0 : outputVolume; }
    }

    public int BufferMs
    {
        set
        {
            bufferSamples = SampleRate / 1000 * value;
            loopback.TargetSamples = bufferSamples;
            foreach (var p in peers.Values) p.Buffer.TargetSamples = bufferSamples;
        }
    }

    /// <summary>Automatic delay buffer: as small as the connection allows, grows only on real stutter.</summary>
    public bool BufferAuto
    {
        get => bufferAuto;
        set
        {
            bufferAuto = value;
            loopback.Adaptive = value;
            loopback.TargetSamples = bufferSamples;
            foreach (var p in peers.Values) { p.Buffer.Adaptive = value; p.Buffer.TargetSamples = bufferSamples; }
        }
    }
    volatile bool bufferAuto = true;

    /// <summary>Current delay buffer in ms (the partner's in a call, otherwise the mic test's).</summary>
    public int CurrentBufferMs => (state == CallState.Connected && Partner is { } p ? p.Buffer : loopback).TargetMs;

    /// <summary>Estimated mouth-to-ear delay inside WolfSpeak (excludes headset hardware and network).</summary>
    public double EstimatedDelayMs => CaptureMs + FrameSamples * 500.0 / SampleRate + CurrentBufferMs + RenderMs + 1;

    public bool Loopback
    {
        get => loopbackEnabled;
        set { loopbackEnabled = value; if (!value) loopback.Clear(); }
    }
    volatile bool loopbackEnabled;

    public VoiceEngine()
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
            ReceiveBufferSize = 1 << 20,
        };
        // Stop Windows from failing ReceiveFrom with WSAECONNRESET after an ICMP "port unreachable".
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        // DSCP EF (46): marks packets as real-time voice for routers/switches that honour QoS.
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }
        socket.Bind(new IPEndPoint(IPAddress.Any, Port));

        voiceVolume = new VolumeSampleProvider(voiceMixer);
        loopback.TargetSamples = bufferSamples;
        loopback.Adaptive = bufferAuto;
        voiceMixer.AddMixerInput(loopback);
        outputMixer.AddMixerInput(voiceVolume);
        outputMixer.AddMixerInput(sounds);

        rxThread = new Thread(ReceiveLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "WolfSpeak RX" };
        rxThread.Start();
        tickTimer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
    }

    public void PlaySound(SoundKind kind) => sounds.Play(kind);

    // ---------------------------------------------------------------- calls

    public void Call(uint id)
    {
        lock (callLock)
        {
            if (state != CallState.Idle || !peers.TryGetValue(id, out var p)) return;
            if (!p.VersionMatches) { Notice?.Invoke(VersionMismatchText(p)); return; }
            partnerId = id;
            state = CallState.Calling;
            callStartMs = Environment.TickCount64;
            SendControl(TypeCallRequest, p);
        }
    }

    public void Accept()
    {
        lock (callLock)
        {
            if (state != CallState.Ringing) return;
            if (Partner is not { } p) { EndCall(null); return; }
            SendControl(TypeCallAccept, p);
            EnterConnected(p);
        }
    }

    /// <summary>Cancel an outgoing call, decline an incoming one, or hang up.</summary>
    public void HangUp()
    {
        lock (callLock)
        {
            var p = Partner;
            if (p is not null)
            {
                if (state == CallState.Ringing)
                    SendDecline(p, DeclineNormal);
                else if (state != CallState.Idle)
                    for (int i = 0; i < 3; i++) SendControl(TypeCallEnd, p);
            }
            EndCall(null);
        }
    }

    void EnterConnected(Peer p)
    {
        p.Buffer.Clear();
        p.HasSeq = false;
        p.Received = p.Lost = 0;
        lastReceived = lastLost = 0;
        LossPercent = 0;
        RttMs = -1;
        ConnectedAtMs = Environment.TickCount64;
        state = CallState.Connected;
    }

    void EndCall(string? notice)
    {
        state = CallState.Idle;
        partnerId = 0;
        RttMs = -1;
        if (notice is not null) Notice?.Invoke(notice);
    }

    void OnCallPacket(byte type, Peer from, ReadOnlySpan<byte> payload)
    {
        lock (callLock)
        {
            bool isPartner = state != CallState.Idle && from.Id == partnerId;
            switch (type)
            {
                case TypeCallRequest:
                    if (state == CallState.Idle && !from.HelloSeen)
                        break; // don't know their version yet; they repeat the request every 250 ms
                    if (state == CallState.Idle && !from.VersionMatches)
                    {
                        SendDecline(from, DeclineVersion);
                        long now = Environment.TickCount64;
                        if (now - from.MismatchNoticeMs > 10000)
                        {
                            from.MismatchNoticeMs = now;
                            Notice?.Invoke($"{from.Name} tried to call you. " + VersionMismatchText(from));
                        }
                    }
                    else if (state == CallState.Idle)
                    {
                        partnerId = from.Id;
                        state = CallState.Ringing;
                        lastRequestMs = Environment.TickCount64;
                    }
                    else if (isPartner && state == CallState.Ringing)
                        lastRequestMs = Environment.TickCount64;
                    else if (isPartner && state == CallState.Connected)
                        SendControl(TypeCallAccept, from); // our accept got lost
                    else if (isPartner && state == CallState.Calling)
                    {
                        SendControl(TypeCallAccept, from); // we called each other at the same time
                        EnterConnected(from);
                    }
                    else
                        SendDecline(from, DeclineBusy);
                    break;

                case TypeCallAccept:
                    if (isPartner && state == CallState.Calling) EnterConnected(from);
                    break;

                case TypeCallDecline:
                    if (isPartner && state == CallState.Calling)
                        EndCall((payload.Length > 0 ? payload[0] : DeclineNormal) switch
                        {
                            DeclineBusy => $"{from.Name} is in another call",
                            DeclineVersion => VersionMismatchText(from),
                            _ => $"{from.Name} declined",
                        });
                    else if (isPartner && state == CallState.Ringing)
                        EndCall($"Missed call from {from.Name}");
                    break;

                case TypeCallEnd:
                    if (isPartner)
                        EndCall(state == CallState.Ringing ? $"Missed call from {from.Name}" : $"{from.Name} hung up");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- audio

    /// <summary>
    /// (Re)opens the mic and headphones. A selected device that is unplugged falls back to the
    /// Windows default. Returns a short note when a fallback happened, otherwise null.
    /// </summary>
    public string? StartAudio(string? micId, string? outputId)
    {
        lock (audioLock)
        {
            StopAudio();
            string? note = null;

            using var enumerator = new MMDeviceEnumerator();
            var mic = Resolve(enumerator, micId, DataFlow.Capture, ref note, "Microphone");
            var speaker = Resolve(enumerator, outputId, DataFlow.Render, ref note, "Headphones");
            MicName = mic.FriendlyName;
            OutputName = speaker.FriendlyName;

            // Virtual devices (Sonar etc.) glitch and change pitch in low-latency mode (measured) and
            // re-buffer internally anyway, so they always get the classic mode.
            var render = new LowLatencyRender(speaker.ID, outputMixer, UltraLowLatency && !IsVirtualDevice(OutputName));
            render.Failed += ex => { AudioFailed = true; Error?.Invoke("Headphones stopped: " + ex.Message); };
            output = render;

            resampler = null;
            frameFill = 0;
            highPass.Reset();
            var cap = new LowLatencyCapture(mic.ID, OnCaptured, UltraLowLatency && !IsVirtualDevice(MicName));
            cap.Failed += ex => { AudioFailed = true; Error?.Invoke("Microphone stopped: " + ex.Message); };
            var fmt = cap.DeviceFormat;
            resampler = fmt.SampleRate == SampleRate ? null : new LinearResampler(fmt.SampleRate, SampleRate);
            capture = cap;

            render.Start();
            cap.Start();
            AudioFailed = false;
            return note;
        }
    }

    public string MicName { get; private set; } = "";
    public string OutputName { get; private set; } = "";
    /// <summary>Mic engine period in ms (time until captured audio reaches us).</summary>
    public double CaptureMs => capture?.PeriodMs ?? 10;
    /// <summary>Audio kept queued in the headphone device, in ms.</summary>
    public double RenderMs => output?.QueueMs ?? 20;
    public bool IsLowLatency => capture?.IsLowLatency == true && output?.IsLowLatency == true;
    public string AudioInfo =>
        $"Mic: {capture?.DeviceFormat}, {CaptureMs:0.#} ms period · Headphones: {output?.DeviceFormat}, {RenderMs:0.#} ms queue" +
        (IsLowLatency ? " · low-latency mode" : " · standard mode") +
        $"\nGlitches: {Glitches} (gaps filled {GapsFilled}, headphone underruns {output?.Underruns ?? 0})";

    static readonly string[] VirtualDeviceWords = ["Sonar", "Virtual", "Voicemeeter", "VB-Audio", "Steam Streaming", "Nahimic", "Wave Link"];

    /// <summary>Software audio devices that route through another app (SteelSeries Sonar, Voicemeeter, …).</summary>
    public static bool IsVirtualDevice(string name) =>
        VirtualDeviceWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Use Windows' low-latency audio mode (3 ms periods where supported). Off = classic, safest settings.</summary>
    public bool UltraLowLatency { get; set; } = true;

    /// <summary>Concealed gaps in what you hear (mid-speech buffer underruns).</summary>
    public int GapsFilled => (state == CallState.Connected && Partner is { } p ? p.Buffer : loopback).Underruns;
    public int Glitches => GapsFilled + (output?.Underruns ?? 0);

    /// <summary>
    /// "Default" means the Windows <em>communication</em> devices, like Discord/Teams use. With SteelSeries
    /// Sonar that is Sonar Chat, so WolfSpeak lands in GG's Chat channel automatically.
    /// (We opt out of Windows' auto-ducking, so games are not turned down.)
    /// </summary>
    public const Role DefaultRole = Role.Communications;

    static MMDevice Resolve(MMDeviceEnumerator en, string? id, DataFlow flow, ref string? note, string what)
    {
        if (id is not null)
        {
            try
            {
                var d = en.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
            catch { }
            note = $"{what} not found — using the Windows default";
        }
        return en.GetDefaultAudioEndpoint(flow, DefaultRole);
    }

    /// <summary>True when the mic or headphones stopped with an error (e.g. unplugged).</summary>
    public bool AudioFailed { get; private set; }

    void StopAudio()
    {
        var cap = capture;
        capture = null;
        cap?.Dispose();
        output?.Dispose();
        output = null;
        Transmitting = false;
    }

    void OnCaptured(ReadOnlySpan<byte> src, DeviceFormat fmt)
    {
        int bytesPerSample = fmt.BitsPerSample / 8;
        int channels = fmt.Channels;
        int frames = src.Length / (bytesPerSample * channels);
        bool isFloat = fmt.IsFloat;

        if (monoScratch.Length < frames) monoScratch = new float[frames * 2];

        // Downmix to mono float
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = src[((i * channels + c) * bytesPerSample)..];
                sum += isFloat ? BinaryPrimitives.ReadSingleLittleEndian(s)
                    : bytesPerSample switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                        3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    };
            }
            monoScratch[i] = sum / channels;
        }

        ReadOnlySpan<float> mono = monoScratch.AsSpan(0, frames);
        if (resampler is not null)
        {
            int max = resampler.MaxOutput(frames);
            if (resampleScratch.Length < max) resampleScratch = new float[max * 2];
            int n = resampler.Process(mono, resampleScratch);
            mono = resampleScratch.AsSpan(0, n);
        }

        // Slice into 5 ms frames
        while (!mono.IsEmpty)
        {
            int take = Math.Min(FrameSamples - frameFill, mono.Length);
            mono[..take].CopyTo(frame.AsSpan(frameFill));
            frameFill += take;
            mono = mono[take..];
            if (frameFill == FrameSamples)
            {
                ProcessFrame();
                frameFill = 0;
            }
        }
    }

    void ProcessFrame()
    {
        // Mic chain: gain → 80 Hz high-pass (DC, rumble, hum) → soft limiter
        float gain = MicGain;
        double sumSq = 0;
        for (int i = 0; i < FrameSamples; i++)
        {
            float v = Dsp.SoftClip(highPass.Process(frame[i] * gain));
            frame[i] = v;
            sumSq += v * v;
        }
        float rmsDb = (float)(10 * Math.Log10(sumSq / FrameSamples + 1e-12));
        MicLevelDb = rmsDb;

        bool wasTx = Transmitting;
        bool tx;
        if (Muted)
            tx = false;
        else if (PushToTalk)
            tx = (GetAsyncKeyState(PttKey) & 0x8000) != 0;
        else
        {
            if (rmsDb >= GateDb) hangover = GateHangoverFrames;
            tx = hangover > 0;
            if (hangover > 0) hangover--;
        }
        Transmitting = tx;

        if (tx && !wasTx)
        {
            // Start of speech. Voice gate: also send the frames just before it opened so the
            // first syllable isn't clipped. Fade in the very first sample block to avoid a click.
            int pre = PushToTalk ? 0 : preRollCount;
            for (int k = pre; k > 0; k--)
            {
                var f = preRoll[(preRollIndex - k + PreRollFrames) % PreRollFrames];
                if (k == pre) Dsp.FadeIn(f);
                SendFrame(f, end: false);
            }
            if (pre == 0) Dsp.FadeIn(frame);
            SendFrame(frame, end: false);
        }
        else if (tx)
            SendFrame(frame, end: false);
        else if (wasTx)
        {
            // End of speech: fade this frame out and mark it, so the receiver doesn't try to conceal "loss".
            Dsp.FadeOut(frame);
            SendFrame(frame, end: true);
        }

        frame.CopyTo(preRoll[preRollIndex], 0);
        preRollIndex = (preRollIndex + 1) % PreRollFrames;
        preRollCount = Math.Min(preRollCount + 1, PreRollFrames);
    }

    void SendFrame(float[] samples, bool end)
    {
        if (loopbackEnabled) loopback.Write(samples, end);
        if (state != CallState.Connected || !peers.TryGetValue(partnerId, out var partner)) return;

        WriteHeader(txPacket, TypeAudio);
        BinaryPrimitives.WriteUInt16LittleEndian(txPacket.AsSpan(HeaderSize), txSeq++);
        txPacket[HeaderSize + 2] = end ? AudioFlagEnd : (byte)0;
        var pcm = txPacket.AsSpan(HeaderSize + 3);
        for (int i = 0; i < FrameSamples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm[(i * 2)..], (short)(samples[i] * 32767f));
        Send(txPacket, partner.EndPoint);
    }

    // ---------------------------------------------------------------- network

    void ReceiveLoop()
    {
        var buf = new byte[2048];
        var samples = new float[FrameSamples];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            int n;
            try { n = socket.ReceiveFrom(buf, ref from); }
            catch (SocketException) { if (!running) break; continue; }
            catch (ObjectDisposedException) { break; }

            // A malformed packet from anyone on the LAN must never kill the receiver.
            try { HandlePacket(buf, n, (IPEndPoint)from, samples); }
            catch (Exception ex) { Log.Write("Ignored bad network packet", ex); }
        }
    }

    void HandlePacket(byte[] buf, int n, IPEndPoint ep, float[] samples)
    {
        {
            if (n < HeaderSize || buf[0] != 'W' || buf[1] != 'S') return;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(3));
            if (id == myId) return; // our own broadcast
            byte type = buf[2];
            var payload = buf.AsSpan(HeaderSize, n - HeaderSize);

            if (type == TypeBye)
            {
                if (peers.TryGetValue(id, out var gone)) LosePeer(gone, $"{gone.Name} closed WolfSpeak");
                return;
            }

            var peer = Touch(id, ep, out bool isNew);
            switch (type)
            {
                case TypeHello:
                {
                    int sep = payload.IndexOf((byte)0);
                    peer.Name = Encoding.UTF8.GetString(sep < 0 ? payload : payload[..sep]);
                    peer.Version = sep < 0 ? null : Encoding.UTF8.GetString(payload[(sep + 1)..]);
                    peer.HelloSeen = true;
                    if (isNew) Send(BuildHello(), peer.EndPoint); // answer right away so they see us too
                    break;
                }

                case TypeAudio:
                {
                    if (state != CallState.Connected || id != partnerId) break;
                    int count = (payload.Length - 3) / 2;
                    if (count <= 0 || count > FrameSamples) break;
                    ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    bool end = (payload[2] & AudioFlagEnd) != 0;
                    if (peer.HasSeq)
                    {
                        short gap = (short)(seq - peer.LastSeq);
                        if (gap <= 0) break; // late/duplicate
                        if (gap > 1 && gap < 100) Interlocked.Add(ref peer.Lost, gap - 1);
                    }
                    peer.LastSeq = seq;
                    peer.HasSeq = true;
                    Interlocked.Increment(ref peer.Received);
                    double sq = 0;
                    for (int i = 0; i < count; i++)
                    {
                        float v = BinaryPrimitives.ReadInt16LittleEndian(payload[(3 + i * 2)..]) / 32768f;
                        samples[i] = v;
                        sq += v * v;
                    }
                    peer.Level = Math.Clamp((float)(10 * Math.Log10(sq / count + 1e-12) + 60) / 50f, 0f, 1f);
                    peer.Buffer.Write(samples.AsSpan(0, count), end);
                    peer.LastAudio = Environment.TickCount64;
                    break;
                }

                case TypePing:
                    var pong = new byte[HeaderSize + payload.Length];
                    WriteHeader(pong, TypePong);
                    payload.CopyTo(pong.AsSpan(HeaderSize));
                    Send(pong, peer.EndPoint);
                    break;

                case TypePong:
                    if (payload.Length >= 8 && id == partnerId)
                    {
                        long sent = BinaryPrimitives.ReadInt64LittleEndian(payload);
                        double rtt = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                        RttMs = RttMs < 0 ? rtt : RttMs * 0.7 + rtt * 0.3;
                    }
                    break;

                case TypeCallRequest or TypeCallAccept or TypeCallDecline or TypeCallEnd:
                    OnCallPacket(type, peer, payload);
                    break;
            }
        }
    }

    Peer Touch(uint id, IPEndPoint ep, out bool isNew)
    {
        isNew = false;
        if (!peers.TryGetValue(id, out var peer))
        {
            var created = new Peer
            {
                Id = id,
                EndPoint = new IPEndPoint(ep.Address, ep.Port),
                Buffer = new JitterBuffer { TargetSamples = bufferSamples, Adaptive = bufferAuto },
                Name = ep.Address.ToString(),
            };
            peer = peers.GetOrAdd(id, created);
            if (ReferenceEquals(peer, created))
            {
                voiceMixer.AddMixerInput(peer.Buffer);
                Interlocked.Increment(ref peerVersion);
                isNew = true;
            }
        }
        else if (!peer.EndPoint.Equals(ep))
        {
            peer.EndPoint = new IPEndPoint(ep.Address, ep.Port);
        }
        peer.LastSeen = Environment.TickCount64;
        return peer;
    }

    void LosePeer(Peer peer, string notice)
    {
        lock (callLock)
        {
            if (state != CallState.Idle && peer.Id == partnerId)
                EndCall(state == CallState.Connected ? notice : $"{peer.Name} is no longer reachable");
        }
        if (peers.TryRemove(peer.Id, out _))
        {
            voiceMixer.RemoveMixerInput(peer.Buffer);
            Interlocked.Increment(ref peerVersion);
        }
    }

    void Tick()
    {
        if (!running) return;
        try
        {
            long now = Environment.TickCount64;
            tick++;

            lock (callLock)
            {
                var p = Partner;
                switch (state)
                {
                    case CallState.Calling when now - callStartMs > RingTimeoutMs:
                        if (p is not null) SendControl(TypeCallEnd, p);
                        EndCall($"{p?.Name ?? "Your friend"} didn't answer");
                        break;
                    case CallState.Calling when p is not null:
                        SendControl(TypeCallRequest, p); // UDP: keep repeating until answered
                        break;
                    case CallState.Ringing when now - lastRequestMs > 3000:
                        EndCall($"Missed call from {p?.Name ?? "your friend"}");
                        break;
                    case CallState.Connected when p is not null && tick % 2 == 0:
                        var ping = new byte[HeaderSize + 8];
                        WriteHeader(ping, TypePing);
                        BinaryPrimitives.WriteInt64LittleEndian(ping.AsSpan(HeaderSize), Stopwatch.GetTimestamp());
                        Send(ping, p.EndPoint);
                        break;
                }
            }

            if (tick % 4 != 0) return; // once per second below

            if (state == CallState.Connected && Partner is { } partner)
            {
                int received = partner.Received, lost = partner.Lost;
                int dr = received - lastReceived, dl = lost - lastLost;
                lastReceived = received;
                lastLost = lost;
                if (dr + dl > 0) LossPercent = LossPercent * 0.6 + 100.0 * dl / (dr + dl) * 0.4;
            }

            var hello = BuildHello();
            foreach (var bcast in BroadcastAddresses())
                Send(hello, new IPEndPoint(bcast, Port));
            foreach (var ip in manualTargets.Values)
                Send(hello, new IPEndPoint(ip, Port));
            foreach (var p in peers.Values)
                Send(hello, p.EndPoint);

            foreach (var p in peers.Values)
                if (now - p.LastSeen > PeerTimeoutMs)
                    LosePeer(p, $"Connection to {p.Name} lost");
        }
        catch { /* never kill the timer */ }
    }

    public void AddManualTarget(IPAddress ip)
    {
        manualTargets[ip.ToString()] = ip;
        Send(BuildHello(), new IPEndPoint(ip, Port));
    }

    public void RemoveManualTarget(IPAddress ip) => manualTargets.TryRemove(ip.ToString(), out _);

    byte[] BuildHello()
    {
        var name = Encoding.UTF8.GetBytes((Name.Length > 32 ? Name[..32] : Name).Replace("\0", ""));
        var version = Encoding.UTF8.GetBytes(AppVersion);
        var pkt = new byte[HeaderSize + name.Length + 1 + version.Length];
        WriteHeader(pkt, TypeHello);
        name.CopyTo(pkt, HeaderSize);
        version.CopyTo(pkt, HeaderSize + name.Length + 1);
        return pkt;
    }

    public static string VersionMismatchText(Peer p) =>
        $"{p.Name} has {(p.Version is null ? "an older WolfSpeak" : $"WolfSpeak {p.Version}")}, you have {AppVersion}. " +
        "Update both to the same version to call.";

    void SendControl(byte type, Peer to)
    {
        var pkt = new byte[HeaderSize];
        WriteHeader(pkt, type);
        Send(pkt, to.EndPoint);
    }

    void SendDecline(Peer to, byte reason)
    {
        var pkt = new byte[HeaderSize + 1];
        WriteHeader(pkt, TypeCallDecline);
        pkt[HeaderSize] = reason;
        Send(pkt, to.EndPoint);
    }

    void WriteHeader(byte[] pkt, byte type)
    {
        pkt[0] = (byte)'W';
        pkt[1] = (byte)'S';
        pkt[2] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(3), myId);
    }

    void Send(byte[] pkt, IPEndPoint to)
    {
        try { socket.SendTo(pkt, to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    static IEnumerable<NetworkInterface> ActiveInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    public static List<IPAddress> LocalAddresses() =>
        ActiveInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();

    static HashSet<IPAddress> BroadcastAddresses()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in ActiveInterfaces())
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
                set.Add(new IPAddress(ip));
            }
        return set;
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        if (!running) return;
        HangUp();
        running = false;
        tickTimer.Dispose();

        var bye = new byte[HeaderSize];
        WriteHeader(bye, TypeBye);
        foreach (var p in peers.Values) Send(bye, p.EndPoint);

        lock (audioLock) StopAudio();
        socket.Close();
    }
}
