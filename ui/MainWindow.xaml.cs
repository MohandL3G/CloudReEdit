using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace CloudReEdit;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            try
            {
                RootNavigation.Navigate(typeof(CloudReEdit.Pages.AccountsPage));
            }
            catch { }
        };
    }

    public void Navigate(Type pageType)
    {
        RootNavigation.Navigate(pageType);
    }
}
