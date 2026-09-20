using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class AccountsListStateTests
{
    [Fact]
    public void SteamMissing_ShowsSteamNotFound()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: false, configFileExists: false, configUsable: false, accountCount: 0);
        Assert.Equal(AccountsListStateKind.SteamNotFound, state);
        Assert.Equal(AccountsListState.SteamNotFoundMessage, message);
        Assert.Contains("editing is unavailable", message);
    }

    [Fact]
    public void SteamMissing_OverridesAccountsAndConfig()
    {
        var (state, _) = AccountsListState.Determine(
            steamFound: false, configFileExists: true, configUsable: true, accountCount: 5);
        Assert.Equal(AccountsListStateKind.SteamNotFound, state);
    }

    [Fact]
    public void SteamPresent_ConfigMissing_NoAccounts_ShowsNotConfigured()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: true, configFileExists: false, configUsable: false, accountCount: 0);
        Assert.Equal(AccountsListStateKind.CloudRedirectNotConfigured, state);
        Assert.Equal(AccountsListState.NotConfiguredMessage, message);
        Assert.StartsWith("CloudRedirect is not configured.", message);
    }

    [Fact]
    public void SteamPresent_ConfigUnusable_NoAccounts_ShowsConfigUnusable()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: true, configFileExists: true, configUsable: false, accountCount: 0);
        Assert.Equal(AccountsListStateKind.ConfigUnusable, state);
        Assert.Equal(AccountsListState.ConfigUnusableMessage, message);
        Assert.StartsWith("CloudRedirect configuration exists but could not be used.", message);
    }

    [Fact]
    public void SteamPresent_UsableConfig_NoAccounts_ShowsNoAccounts()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: true, configFileExists: true, configUsable: true, accountCount: 0);
        Assert.Equal(AccountsListStateKind.NoAccounts, state);
        Assert.Equal(AccountsListState.NoAccountsMessage, message);
        Assert.StartsWith("No CloudRedirect accounts were found.", message);
    }

    [Fact]
    public void SteamPresent_UsableConfig_OneAccount_ShowsNothing()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: true, configFileExists: true, configUsable: true, accountCount: 1);
        Assert.Equal(AccountsListStateKind.AccountsAvailable, state);
        Assert.Equal("", message);
    }

    [Fact]
    public void SteamPresent_UsableConfig_ManyAccounts_ShowsNothing()
    {
        var (state, message) = AccountsListState.Determine(
            steamFound: true, configFileExists: true, configUsable: true, accountCount: 7);
        Assert.Equal(AccountsListStateKind.AccountsAvailable, state);
        Assert.Equal("", message);
    }

    [Fact]
    public void AccountsExist_ConfigMissingOrUnusable_StillShowsNothing()
    {
        // Config problems stay in the provider status; once account data exists the
        // primary state is availability, never a stacked mega-message.
        var (missingState, missingMessage) = AccountsListState.Determine(
            steamFound: true, configFileExists: false, configUsable: false, accountCount: 2);
        Assert.Equal(AccountsListStateKind.AccountsAvailable, missingState);
        Assert.Equal("", missingMessage);

        var (brokenState, brokenMessage) = AccountsListState.Determine(
            steamFound: true, configFileExists: true, configUsable: false, accountCount: 3);
        Assert.Equal(AccountsListStateKind.AccountsAvailable, brokenState);
        Assert.Equal("", brokenMessage);
    }

    [Fact]
    public void LoadError_MapsToGenericActionableMessage()
    {
        var (state, message) = AccountsListState.LoadErrorState();
        Assert.Equal(AccountsListStateKind.LoadError, state);
        Assert.Equal(AccountsListState.LoadErrorMessage, message);
        Assert.StartsWith("CloudReEdit could not load account information.", message);
    }

    [Fact]
    public void LoadError_ExposesNoPathsOrInternalDetail()
    {
        var (_, message) = AccountsListState.LoadErrorState();
        Assert.DoesNotContain("%AppData%", message);
        Assert.DoesNotContain("config.json", message);
        Assert.DoesNotContain("CloudRedirect", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", message);
    }
}