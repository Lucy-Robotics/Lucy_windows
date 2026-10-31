using Lucy.Core;

namespace Lucy;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Helper mode used by ManagedProcess.SendCtrlC — must stay tiny and never touch WPF.
        if (args.Length == 2 && args[0] == ManagedProcess.CtrlCArgument && int.TryParse(args[1], out var pid))
            return ManagedProcess.RunCtrlCHelper(pid);
        // Helper mode used by the launcher's Update (see FirmwareToolchain).
        if (args.Length == 2 && args[0] == FirmwareToolchain.HelperArgument)
            return FirmwareToolchain.RunHelper(args[1]);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
