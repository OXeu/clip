using System.IO;
using System.Windows;

namespace Clip.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        DispatcherUnhandledException += (_, args) =>
        {
            if (smoke) { args.Handled = true; Shutdown(1); return; }
            MessageBox.Show(args.Exception.Message, "Clip · 发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(smoke ? [] : e.Args);
        MainWindow = window;
        window.Show();
        if (smoke)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    window.VerifyUi();
                    var dialog = new ExportWindow(new Core.MediaInfo("test.mp4", 10, 1920, 1080, 30, 0, 1, "h264"), true) { Owner = window };
                    dialog.Show();
                    dialog.UpdateLayout();
                    dialog.Close();
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), exception.ToString());
                    Shutdown(1);
                }
            }));
        }
    }
}
