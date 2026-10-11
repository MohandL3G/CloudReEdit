using System;
using System.Windows;
using CloudReEdit.Services;
using Wpf.Ui.Controls;

namespace CloudReEdit;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        VersionItem.Text = string.IsNullOrEmpty(AppVersion.Display())
            ? "CloudReEdit"
            : $"CloudReEdit {AppVersion.Display()}";

        Loaded += async (_, _) =>
        {
            try
            {
                RootNavigation.Navigate(typeof(CloudReEdit.Pages.AccountsPage));
            }
            catch (Exception ex)
            {
                AppLog.Exception("ui", "initial navigation to AccountsPage failed", ex);
            }
        };
    }

    public void Navigate(Type pageType)
    {
        RootNavigation.Navigate(pageType);
    }
}
