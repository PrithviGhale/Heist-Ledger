using Velopack;

namespace GTAEarningsTracker;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer hooks must run before WPF creates windows or loads user data.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
