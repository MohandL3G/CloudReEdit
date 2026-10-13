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
            EmptyText.Text = GameListEmptyState.PickAccount;
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
        catch (Exception ex)
        {
            AppLog.Exception("store", $"app info lookup failed for {ids.Count} id(s)", ex);
            info = new Dictionary<uint, Services.StoreAppInfo>();
        }

        foreach (var game in _games)
        {
            var found = info.TryGetValue(game.AppId, out var app);
            game.Name = GameNames.DisplayName(game.AppId, found ? app!.Name : null);

            // Bind only a URL that already passed SteamStoreClient's validation - a
            // Steam CDN host, or a file:// path inside the store_images cache. Anything
            // else leaves the row imageless rather than risking a load from an
            // unexpected location. This runs before the Refresh() below, which re-reads
            // the bindings, so an image that only lands on a later call still appears.
            var header = found ? app!.HeaderUrl : null;
            game.HeaderImage = Services.SteamStoreClient.IsValidImageUrl(header) ? header! : "";
        }

        if (_view != null)
        {
            _view.Refresh();
            UpdateEmptyState();
        }
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

        UpdateEmptyState();
    }

    // The banner follows the FILTERED view, so a search that matches nothing is
    // its own state (as long as a search is active), distinct from an account
    // that simply has no games.
    private void UpdateEmptyState()
    {
        var message = GameListEmptyState.For(
            accountSelected: !string.IsNullOrEmpty(AppState.AccountId),
            totalGames: _games.Count,
            shownGames: _view?.Count ?? 0,
            query: SearchBox.Text ?? "");
        EmptyText.Text = message;
        EmptyText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
