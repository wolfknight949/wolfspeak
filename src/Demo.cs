using System.Net;

namespace WolfSpeak;

/// <summary>Screens the demo mode can show (<c>WolfSpeak.exe --demo call</c>), used for README screenshots.</summary>
public enum DemoScene { Home, Ringing, Call, Settings }

/// <summary>
/// Demo mode: the real UI with made-up friends, a made-up call and no network, audio devices or saved settings.
/// Nothing here runs unless WolfSpeak is started with <c>--demo &lt;scene&gt;</c>.
/// </summary>
public sealed partial class VoiceEngine
{
    public bool IsDemo { get; }
    const string DemoSafetyCode = "482 915";

    public static bool TryParseDemoArgs(string[] args, out DemoScene scene)
    {
        scene = DemoScene.Home;
        int i = Array.IndexOf(args, "--demo");
        if (i < 0) return false;
        if (i + 1 < args.Length) Enum.TryParse(args[i + 1], ignoreCase: true, out scene);
        return true;
    }

    public static VoiceEngine CreateDemo(DemoScene scene)
    {
        var engine = new VoiceEngine(demo: true) { Name = "Fenrir" };
        var alex = engine.AddDemoPeer(1, "Alex Rivera", "192.168.1.23");
        engine.AddDemoPeer(2, "Sam", "192.168.1.37");
        engine.AddDemoPeer(3, "Mia Chen", "192.168.1.51");

        long now = Environment.TickCount64;
        switch (scene)
        {
            case DemoScene.Ringing:
                engine.partnerId = alex.Id;
                engine.lastRequestMs = now;
                engine.state = CallState.Ringing;
                break;
            case DemoScene.Call:
                engine.partnerId = alex.Id;
                engine.ConnectedAtMs = now - (12 * 60 + 34) * 1000; // "Connected · 12:34"
                engine.RttMs = 1.6;
                engine.PartnerFingerprint = "DEMO";
                engine.state = CallState.Connected;
                break;
        }
        return engine;
    }

    Peer AddDemoPeer(uint id, string name, string ip)
    {
        var peer = new Peer
        {
            Id = id,
            EndPoint = new IPEndPoint(IPAddress.Parse(ip), Port),
            Buffer = new JitterBuffer(),
            Name = name,
            Version = AppVersion,
            HelloSeen = true,
            LastSeen = Environment.TickCount64,
        };
        peers[id] = peer;
        Interlocked.Increment(ref peerVersion);
        return peer;
    }

    /// <summary>Called every UI frame in demo mode: a lively mic meter and a friend who is talking.</summary>
    public void DemoTick()
    {
        double t = Environment.TickCount64 / 1000.0;
        MicLevelDb = (float)(-34 + 8 * Math.Sin(t * 5.3) + 4 * Math.Sin(t * 13.1));
        if (state == CallState.Connected && Partner is { } p)
        {
            p.LastAudio = Environment.TickCount64;
            p.Level = (float)(0.55 + 0.3 * Math.Sin(t * 6.1) * Math.Sin(t * 2.3));
        }
    }
}
