using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Services;

namespace CloudReEdit.Pages;

public partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var accounts = await Task.Run(AccountScanner.ListAccounts);

        AccountCombo.ItemsSource = accounts;

        var steamPath = SteamDetector.FindSteamPath();
        SteamPathText.Text = steamPath ?? "Steam not found.";

        var cfg = SteamDetector.ReadConfig();
        ProviderText.Text = cfg != null
            ? $"{cfg.DisplayName} ({cfg.Provider})"
            : "No CloudRedirect config found in %AppData%\\CloudRedirect\\config.json";

        UpdateSteamStatus();

        // Preselect the previously chosen account.
        if (!string.IsNullOrEmpty(AppState.AccountId))
        {
            foreach (var item in AccountCombo.Items)
            {
                if (item is AccountInfo info && info.AccountId == AppState.AccountId)
                {
                    AccountCombo.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private void UpdateSteamStatus()
    {
        bool running = SteamDetector.IsSteamRunning();
        SteamRunningText.Text = running
            ? "Steam is RUNNING. Editing playtime is blocked while Steam is open. Use \"Close Steam\" first."
            : "Steam is not running. Playtime edits can be applied safely.";
        CloseSteamButton.IsEnabled = running;
    }

    private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenGamesButton.IsEnabled = AccountCombo.SelectedItem is AccountInfo;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void CloseSteam_Click(object sender, RoutedEventArgs e)
    {
        var steamPath = SteamDetector.FindSteamPath();
        if (steamPath == null) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(steamPath, "steam.exe"),
            Arguments = "-shutdown",
            UseShellExecute = true
        })?.Dispose();

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (!SteamDetector.IsSteamRunning()) break;
        }

        UpdateSteamStatus();
    }

    private void RestartSteam_Click(object sender, RoutedEventArgs e)
    {
        var steamPath = SteamDetector.FindSteamPath();
        if (steamPath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(steamPath, "steam.exe"),
                UseShellExecute = true
            })?.Dispose();
        }
        catch { }

        UpdateSteamStatus();
    }

    private void OpenGames_Click(object sender, RoutedEventArgs e)
    {
        if (AccountCombo.SelectedItem is not AccountInfo account) return;

        AppState.AccountId = account.AccountId;
        AppState.AccountDisplay = account.DisplayName;

        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(GamesPage));
    }
}
