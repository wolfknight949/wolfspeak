using System.IO;
using System.Text.Json;

namespace WolfSpeak;

public sealed class Settings
{
    public string Name { get; set; } = Environment.UserName;
    public string? MicId { get; set; }
    public string? OutputId { get; set; }
    public bool PushToTalk { get; set; }
    public int PttKey { get; set; } = 0x05; // Mouse 4
    public int GateDb { get; set; } = -50;
    public int VolumePct { get; set; } = 100;
    public int MicGainPct { get; set; } = 100;
    public int BufferMs { get; set; } = 20;
    public bool BufferAuto { get; set; } = true;
    public bool UltraLowLatency { get; set; } = true;
    public bool HideVirtualHint { get; set; }
    public List<string> ManualIps { get; set; } = [];
    /// <summary>Friend name → identity key fingerprint seen on the last call (warns if it changes).</summary>
    public Dictionary<string, string> KnownKeys { get; set; } = [];
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WolfSpeak", "settings.json");

    /// <summary>Demo mode: fresh defaults, never read or written (your real settings stay untouched).</summary>
    public static bool DemoMode { get; set; }

    public static Settings Load()
    {
        if (DemoMode) return new() { Name = "Fenrir" };
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings: start fresh */ }
        return new();
    }

    public void Save()
    {
        if (DemoMode) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal */ }
    }
}
