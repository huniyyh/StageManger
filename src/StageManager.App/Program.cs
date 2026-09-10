using Velopack;

namespace StageManager.App;

/// <summary>
/// Entry point. Velopack runs before anything else so the install, update and uninstall hooks it invokes
/// (creating shortcuts, first run, cleanup) can do their work and exit without touching the rest of the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
