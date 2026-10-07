using System.IO;

namespace WolfSpeak;

/// <summary>Tiny append-only error log at %APPDATA%\WolfSpeak\wolfspeak.log (kept under ~1 MB).</summary>
public static class Log
{
    static readonly object sync = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WolfSpeak", "wolfspeak.log");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{(ex is null ? "" : $"\n{ex}")}\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
