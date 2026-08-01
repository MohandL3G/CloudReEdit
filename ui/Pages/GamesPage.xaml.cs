using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Services;

namespace CloudReEdit.Pages;

public partial class GamesPage : Page
{
    private readonly List<GameEntry> _games = new();
    private System.Windows.Data.ListCollectionView? _view;

    public GamesPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        AccountText.Text = AppState.AccountDisplay != null
            ? $"Account: {AppState.AccountDisplay}"
            : "No account selected.";

        if (string.IsNullOrEmpty(AppState.AccountId))
        {
            GameList.ItemsSource = null;
            EmptyText.Text = "Pick an account on the Accounts page first.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var accountId = AppState.AccountId;
        var games = await Task.Run(() => AccountScanner.ListGames(accountId!));

        _games.Clear();
        _games.AddRange(games);

        _view = new System.Windows.Data.ListCollectionView(_games)
        {
            SortDescriptions =
            {
                new SortDescription("PlaytimeMinutes", ListSortDirection.Descending)
            }
        };
        GameList.ItemsSource = _view;

        UpdateFilter();
        _ = RefreshNamesAsync();
    }

    private async Task RefreshNamesAsync()
    {
        var ids = _games.Select(g => g.AppId).Distinct().ToList();
        Dictionary<uint, Services.StoreAppInfo> info;
        try
        {
            info = await Services.SteamStoreClient.Shared.GetAppInfoAsync(ids);
        }
        catch
        {
            info = new Dictionary<uint, Services.StoreAppInfo>();
        }

        foreach (var game in _games)
        {
            game.Name = info.TryGetValue(game.AppId, out var app) && !string.IsNullOrEmpty(app.Name)
                ? app.Name
                : $"App {game.AppId}";
        }

        if (_view != null)
            _view.Refresh();
    }

    private void UpdateFilter()
    {
        var query = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
        if (_view == null) return;

        if (string.IsNullOrEmpty(query))
        {
            _view.Filter = null;
        }
        else
        {
            _view.Filter = o => o is GameEntry g &&
                (g.Name.ToLowerInvariant().Contains(query) ||
                 g.AppId.ToString().Contains(query));
        }

        EmptyText.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateFilter();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(AccountsPage));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: GameEntry game })
            OpenEditor(game);
    }

    private void OpenEditor(GameEntry game)
    {
        AppState.AppId = game.AppId;
        AppState.GameName = game.Name;
        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(PlaytimeEditorPage));
    }
}
