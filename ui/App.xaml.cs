using System.Windows;
using Wpf.Ui.Appearance;

namespace CloudReEdit;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }
}
