namespace CloudReEdit.Services;

/// <summary>Pure decision logic for the Games page empty-state banner. The page
/// routes account/game/search counts and the current query through this so the
/// message always matches the FILTERED (visible) result set — e.g. a search with
/// zero matches is its own state, distinct from an account that has no games.</summary>
public static class GameListEmptyState
{
    public const string PickAccount = "Pick an account on the Accounts page first.";
    public const string NoGames = "No games found in the local stats folder.";
    public const string NoSearchMatches = "No games match your search.";

    /// <summary>Returns the banner message, or an empty string when no empty-state
    /// banner is needed. A whitespace-only query is treated as "no search", which
    /// mirrors the search box's own trim-before-match semantics.</summary>
    public static string For(bool accountSelected, int totalGames, int shownGames, string query)
    {
        if (!accountSelected)
            return PickAccount;

        if (totalGames == 0)
            return NoGames;

        if (shownGames == 0 && !string.IsNullOrWhiteSpace(query))
            return NoSearchMatches;

        return "";
    }
}