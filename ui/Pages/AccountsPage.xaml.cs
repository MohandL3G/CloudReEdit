using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Services;

namespace CloudReEdit.Pages;

public partial class AccountsPage : Page
{
    private bool _isRefreshing;

    public AccountsPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        SetRefreshing(true);
        try
        {
            List<AccountInfo> accounts;
            try
            {
                accounts = await Task.Run(AccountScanner.ListAccounts);
            }
            catch
            {
                // Enumeration failed: nothing usable to show, keep the page alive.
                AccountCombo.ItemsSource = null;
                AccountCombo.SelectedItem = null;
                OpenGamesButton.IsEnabled = false;
                ShowState(AccountsListState.LoadErrorState());
                return;
            }

            AccountCombo.ItemsSource = accounts;

            var steamPath = SteamDetector.FindSteamPath();
            SteamPathText.Text = steamPath ?? "Steam not found.";

            UpdateProviderStatus();
            UpdateSteamStatus();

            // Preselect the previously chosen account when it still exists.
            AccountCombo.SelectedItem = null;
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
            OpenGamesButton.IsEnabled = AccountCombo.SelectedItem is AccountInfo;

            bool steamFound = steamPath != null;
            bool configExists = File.Exists(SteamDetector.GetConfigFilePath());
            bool configUsable = SteamDetector.ReadConfig() != null;
            ShowState(AccountsListState.Determine(steamFound, configExists, configUsable, accounts.Count));
        }
        catch
        {
            AccountCombo.ItemsSource = null;
            AccountCombo.SelectedItem = null;
            OpenGamesButton.IsEnabled = false;
            ShowState(AccountsListState.LoadErrorState());
        }
        finally
        {
            _isRefreshing = false;
            SetRefreshing(false);
        }
    }

    private void ShowState((AccountsListStateKind State, string Message) state)
    {
        if (state.State == AccountsListStateKind.AccountsAvailable)
        {
            StateBar.IsOpen = false;
            return;
        }

        (StateBar.Title, StateBar.Severity) = state.State switch
        {
            AccountsListStateKind.SteamNotFound =>
                ("Steam not detected", Wpf.Ui.Controls.InfoBarSeverity.Warning),
            AccountsListStateKind.CloudRedirectNotConfigured =>
                ("CloudRedirect not configured", Wpf.Ui.Controls.InfoBarSeverity.Warning),
            AccountsListStateKind.ConfigUnusable =>
                ("CloudRedirect config problem", Wpf.Ui.Controls.InfoBarSeverity.Warning),
            AccountsListStateKind.NoAccounts =>
                ("No accounts yet", Wpf.Ui.Controls.InfoBarSeverity.Informational),
            _ =>
                ("Couldn't load accounts", Wpf.Ui.Controls.InfoBarSeverity.Error),
        };
        StateBar.Message = state.Message;
        StateBar.IsOpen = true;
    }

    private void SetRefreshing(bool refreshing)
    {
        RefreshButton.IsEnabled = !refreshing;
        LoadingPanel.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProviderStatus()
    {
        var result = ProviderStatus.Classify(
            File.Exists(SteamDetector.GetConfigFilePath()),
            SteamDetector.ReadConfig());
        ProviderText.Text = ProviderStatus.Text(result.State, result.DisplayName, result.Error);
    }

    private void UpdateSteamStatus()
    {
        bool found = SteamDetector.FindSteamPath() != null;
        bool running = found && SteamDetector.IsSteamRunning();
        SteamRunningText.Text = SteamStatus.Text(found, running);
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

    private void OpenGames_Click(object sender, RoutedEventArgs e)
    {
        if (AccountCombo.SelectedItem is not AccountInfo account) return;

        AppState.AccountId = account.AccountId;
        AppState.AccountDisplay = account.DisplayName;

        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(GamesPage));
    }
}
