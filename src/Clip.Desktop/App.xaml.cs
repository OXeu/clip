using System.IO;
using System.Windows;

namespace Clip.Desktop;

public partial class App : Application
{
    internal static bool IsAutomatedRun => Environment.GetCommandLineArgs().Any(Program.IsAutomationArgument);
    private bool _windowRendered;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = IsAutomatedRun;
        DispatcherUnhandledException += (_, args) =>
        {
            StartupDiagnostics.ReportFailure("Clip · 发生错误", args.Exception, smoke);
            args.Handled = true;
            if (smoke || !_windowRendered) Shutdown(1);
        };
        StartupDiagnostics.Write("Creating main window");
        var window = new MainWindow(smoke ? [] : e.Args);
        MainWindow = window;
        window.ContentRendered += (_, _) => { _windowRendered = true; StartupDiagnostics.Write("Main window rendered"); };
        window.Show();
        StartupDiagnostics.Write("Main window shown");
        if (smoke)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.InitializationCompleted.WaitAsync(TimeSpan.FromSeconds(40));
                    if (e.Args.Contains("--startup-test") && !window.ToolsReady)
                        throw new InvalidOperationException("Packaged FFmpeg could not be initialized.");
                    await window.VerifyUiAsync();
                    var dialog = new ExportWindow(window.CurrentMedia!, true) { Owner = window };
                    dialog.Show();
                    dialog.UpdateLayout();
                    dialog.VerifyDisclosure();
                    dialog.Close();
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-success.txt"), "Window initialization, edit controls, and export dialog passed.");
                    StartupDiagnostics.Write("Startup and UI verification completed");
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), exception.ToString());
                    StartupDiagnostics.Write("Startup verification failed", exception);
                    Shutdown(1);
                }
            });
        }
    }
}
