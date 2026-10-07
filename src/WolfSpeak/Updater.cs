using Velopack;
using Velopack.Sources;

namespace WolfSpeak;

/// <summary>
/// Auto-update from GitHub Releases. Only active when installed via Setup.exe; a dev build or a loose
/// exe never updates itself.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/wolfknight949/wolfspeak";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    static readonly TimeSpan UrgentCheckEvery = TimeSpan.FromMinutes(5); // a friend already runs a newer version

    readonly UpdateManager? manager;
    DateTime lastCheck = DateTime.MinValue;
    bool busy;

    /// <summary>A downloaded release waiting to be applied.</summary>
    public VelopackAsset? Ready { get; private set; }

    public Updater()
    {
        try
        {
            var m = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (m.IsInstalled) manager = m;
        }
        catch (Exception ex) { Log.Write("Updater unavailable", ex); }
    }

    /// <summary>Checks GitHub (rate-limited) and downloads a newer release. True once one is ready to apply.</summary>
    public async Task<bool> CheckAsync(bool urgent)
    {
        if (Ready is not null) return true;
        if (manager is null || busy) return false;
        if (DateTime.UtcNow - lastCheck < (urgent ? UrgentCheckEvery : CheckEvery)) return false;

        busy = true;
        lastCheck = DateTime.UtcNow;
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return false;
            await manager.DownloadUpdatesAsync(info);
            Ready = info.TargetFullRelease;
            Log.Write($"Update {Ready.Version} downloaded");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed", ex);
            return false;
        }
        finally { busy = false; }
    }

    /// <summary>Hands off to the Velopack updater, which installs once this process has exited and relaunches.</summary>
    public void ApplyAfterExit(string[] restartArgs) =>
        manager!.WaitExitThenApplyUpdates(Ready!, silent: true, restart: true, restartArgs);

    public static bool IsNewerThanUs(string? version) =>
        Version.TryParse(version, out var v) && Version.TryParse(VoiceEngine.AppVersion, out var ours) && v > ours;
}
