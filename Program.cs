using System.Runtime.CompilerServices;

namespace APISwitch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--ag-hook", StringComparison.Ordinal))
        {
            return Services.AgHookService.RunCliHook();
        }

        RunWpfApp();
        return Environment.ExitCode;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWpfApp()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
