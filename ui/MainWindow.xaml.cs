using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

    private async void RestartSteamItem_Click(object sender, RoutedEventArgs e)
    {
        var steamPath = Services.SteamDetector.FindSteamPath();
        if (steamPath == null) return;

        var steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe)) return;

        var procs = Process.GetProcessesByName("steam");
        bool wasRunning = procs.Length > 0;
        foreach (var p in procs) p.Dispose();

        if (wasRunning)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = "-shutdown",
                UseShellExecute = true
            })?.Dispose();

            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                var check = Process.GetProcessesByName("steam");
                bool still = check.Length > 0;
                foreach (var p in check) p.Dispose();
                if (!still) break;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = true
            })?.Dispose();
        }
        catch { }
    }
}
