using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class GameNamesTests
{
    [Fact]
    public void InitialName_FormatsAppId()
    {
        Assert.Equal("App 440", GameNames.InitialName(440));
    }

    [Theory]
    [InlineData(440u, "App 440")]
    [InlineData(570u, "App 570")]
    [InlineData(730u, "App 730")]
    public void InitialName_FormatsMultipleAppIds(uint appId, string expected)
        => Assert.Equal(expected, GameNames.InitialName(appId));

    [Fact]
    public void DisplayName_KeepsFallback_WhenStoreLookupHasNoResult()
    {
        Assert.Equal("App 440", GameNames.DisplayName(440, null));
        Assert.Equal("App 440", GameNames.DisplayName(440, ""));
    }

    [Fact]
    public void DisplayName_UsesStoreName_WhenMetadataProvidesOne()
    {
        Assert.Equal("Team Fortress 2", GameNames.DisplayName(440, "Team Fortress 2"));
    }

    [Fact]
    public void InitialName_MatchesSearchByAppKeyword()
    {
        // The page predicate lowercases the name and does a substring match
        // (g.Name.ToLowerInvariant().Contains(query)); the initial fallback name
        // must satisfy that same matching for a generic "app" search.
        var query = "app";
        Assert.Contains(query, GameNames.InitialName(440).ToLowerInvariant());
    }

    [Fact]
    public void InitialName_MatchesSearchByAppIdDigits()
    {
        // Numeric AppId queries match both the fallback name and the AppId string
        // under the page predicate's substring semantics.
        var query = "440";
        Assert.Contains(query, GameNames.InitialName(440).ToLowerInvariant());
        Assert.Contains(query, 440.ToString());
    }
}