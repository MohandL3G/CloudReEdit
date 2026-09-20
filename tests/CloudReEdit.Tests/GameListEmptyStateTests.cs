using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class GameListEmptyStateTests
{
    [Fact]
    public void NoAccountSelected_ShowsPickAccount()
    {
        var msg = GameListEmptyState.For(false, totalGames: 5, shownGames: 5, query: "");
        Assert.Equal(GameListEmptyState.PickAccount, msg);
    }

    [Fact]
    public void AccountWithNoGames_ShowsNoGames()
    {
        var msg = GameListEmptyState.For(true, totalGames: 0, shownGames: 0, query: "");
        Assert.Equal(GameListEmptyState.NoGames, msg);
    }

    [Fact]
    public void GamesAndNoQuery_ShowsNothing()
    {
        var msg = GameListEmptyState.For(true, totalGames: 3, shownGames: 3, query: "");
        Assert.Equal("", msg);
    }

    [Fact]
    public void SearchWithZeroMatches_ShowsNoSearchMatches()
    {
        var msg = GameListEmptyState.For(true, totalGames: 3, shownGames: 0, query: "portal");
        Assert.Equal(GameListEmptyState.NoSearchMatches, msg);
    }

    [Fact]
    public void SearchWithMatches_ShowsNothing()
    {
        var msg = GameListEmptyState.For(true, totalGames: 3, shownGames: 1, query: "portal");
        Assert.Equal("", msg);
    }

    [Fact]
    public void WhitespaceOnlyQuery_IsNotTreatedAsSearch()
    {
        // The page trims the query before filtering, so whitespace-only input is
        // "no search" and must never produce the search-specific message.
        var msg = GameListEmptyState.For(true, totalGames: 3, shownGames: 0, query: "   ");
        Assert.Equal("", msg);
    }
}