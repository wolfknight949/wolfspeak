using Velopack;

namespace WolfSpeak;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (and exits for those).
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Set(false))
            .SetArgs(args)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
