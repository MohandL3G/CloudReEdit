namespace CloudReEdit.Services;

public enum AccountsListStateKind
{
    SteamNotFound,
    CloudRedirectNotConfigured,
    ConfigUnusable,
    NoAccounts,
    AccountsAvailable,
    LoadError,
}

/// <summary>Pure decision logic for the Accounts page's primary state banner. It
/// fills the "what do I do next?" gap without replacing the existing provider or
/// Steam status texts: once accounts exist, no banner is shown (availability wins
/// and config problems stay in the provider status); when nothing is scannable the
/// state explains which prerequisite is missing and what to do.</summary>
public static class AccountsListState
{
    public const string SteamNotFoundMessage =
        "Steam was not detected, so playtime editing is unavailable. Install or start Steam, then click Refresh.";
    public const string NotConfiguredMessage =
        "CloudRedirect is not configured. Set up CloudRedirect and let it sync once before editing playtime.";
    public const string ConfigUnusableMessage =
        "CloudRedirect configuration exists but could not be used. Fix the configuration, then click Refresh.";
    public const string NoAccountsMessage =
        "No CloudRedirect accounts were found. Let CloudRedirect sync account stats, then click Refresh.";
    public const string LoadErrorMessage =
        "CloudReEdit could not load account information. Click Refresh to try again.";

    /// <summary>Primary page state from the facts the page already collected.
    /// Steam detection is a hard prerequisite (account enumeration depends on it);
    /// with Steam present, existing accounts win over config messages so the
    /// provider status keeps its job and the banner never stacks every system state
    /// into one message. When nothing is listed, the missing/usable config
    /// distinction explains whether CloudRedirect exists at all.</summary>
    public static (AccountsListStateKind State, string Message) Determine(
        bool steamFound, bool configFileExists, bool configUsable, int accountCount)
    {
        if (!steamFound)
            return (AccountsListStateKind.SteamNotFound, SteamNotFoundMessage);

        if (accountCount > 0)
            return (AccountsListStateKind.AccountsAvailable, "");

        if (!configFileExists)
            return (AccountsListStateKind.CloudRedirectNotConfigured, NotConfiguredMessage);

        if (!configUsable)
            return (AccountsListStateKind.ConfigUnusable, ConfigUnusableMessage);

        return (AccountsListStateKind.NoAccounts, NoAccountsMessage);
    }

    /// <summary>State for an unexpected refresh failure. Generic and actionable:
    /// no raw exception text, no config paths, no credential detail.</summary>
    public static (AccountsListStateKind State, string Message) LoadErrorState()
        => (AccountsListStateKind.LoadError, LoadErrorMessage);
}