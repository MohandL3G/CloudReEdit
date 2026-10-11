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

        _ = Task.Run(() =>
        {
            try { BackupRetention.Prune(BackupRetention.DefaultRoot()); }
            catch { }
        });
    }
}
