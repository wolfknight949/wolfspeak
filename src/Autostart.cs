using Microsoft.Win32;

namespace WolfSpeak;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed). Starts hidden in the tray.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WolfSpeak";
    public const string TrayArgument = "--tray";

    static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the registered path current if the exe was moved.</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
