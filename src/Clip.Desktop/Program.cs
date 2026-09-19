using System.Runtime.CompilerServices;

namespace Clip.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] arguments)
    {
        StartupDiagnostics.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Write("Unhandled process exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => StartupDiagnostics.Write("Unobserved task exception", e.Exception);
        try
        {
            return RunApplication(arguments);
        }
        catch (Exception exception)
        {
            // This entry point catches failures before App.InitializeComponent / OnStartup exist.
            StartupDiagnostics.ReportFailure("无法启动视频剪辑", exception, arguments.Any(IsAutomationArgument));
            return 1;
        }
    }

    internal static bool IsAutomationArgument(string argument) => argument is "--smoke-test" or "--startup-test" or "--startup-failure-test";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] arguments)
    {
        if (arguments.Contains("--startup-failure-test")) throw new InvalidOperationException("CLIP_STARTUP_FAILURE_TEST");
        StartupDiagnostics.Write("Creating WPF application");
        var application = new App();
        application.InitializeComponent();
        StartupDiagnostics.Write("Application resources loaded");
        return application.Run();
    }
}
