using System;
using System.Threading.Tasks;
using System.Windows;
using CloudReEdit.Services;
using Wpf.Ui.Appearance;

namespace CloudReEdit;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        AppLog.Init();
        AppLog.Info("app", $"starting CloudReEdit {AppVersion.Display()}");

        _ = Task.Run(SweepBackups);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("app", "stopping CloudReEdit");
        AppLog.Shutdown();
        base.OnExit(e);
    }

    private static void SweepBackups()
    {
        try
        {
            var root = BackupRetention.DefaultRoot();
            var result = BackupRetention.Prune(root);
            AppLog.Info("backup",
                $"startup sweep of \"{root}\": scanned {result.Scanned}, deleted {result.Deleted}, " +
                $"{result.Errors.Count} failed");

            foreach (var error in result.Errors)
                AppLog.Warn("backup", $"prune failed: {error}");
        }
        catch (Exception ex)
        {
            AppLog.Exception("backup", "startup sweep failed", ex);
        }
    }
}
