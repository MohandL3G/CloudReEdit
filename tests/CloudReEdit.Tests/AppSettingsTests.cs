using System;
using System.IO;
using System.Text.Json.Nodes;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public AppSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "CloudReEditSettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData("ask", SaveSteamBehaviour.Ask)]
    [InlineData("ASK", SaveSteamBehaviour.Ask)]
    [InlineData("  ask  ", SaveSteamBehaviour.Ask)]
    [InlineData("neverOpen", SaveSteamBehaviour.NeverOpen)]
    [InlineData("never_open", SaveSteamBehaviour.NeverOpen)]
    [InlineData("never", SaveSteamBehaviour.NeverOpen)]
    [InlineData("alwaysOpen", SaveSteamBehaviour.AlwaysOpen)]
    [InlineData("always_open", SaveSteamBehaviour.AlwaysOpen)]
    [InlineData("always", SaveSteamBehaviour.AlwaysOpen)]
    [InlineData("lastState", SaveSteamBehaviour.LastState)]
    [InlineData("last_state", SaveSteamBehaviour.LastState)]
    [InlineData("last", SaveSteamBehaviour.LastState)]
    public void Parse_AcceptsKnownTokens(string token, SaveSteamBehaviour expected)
        => Assert.Equal(expected, AppSettings.ParseSaveSteamBehaviour(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("reopen")]
    public void Parse_UnknownOrMissingFallsBackToAsk(string? token)
        => Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.ParseSaveSteamBehaviour(token));

    [Theory]
    [InlineData(SaveSteamBehaviour.Ask, "ask")]
    [InlineData(SaveSteamBehaviour.NeverOpen, "neverOpen")]
    [InlineData(SaveSteamBehaviour.AlwaysOpen, "alwaysOpen")]
    [InlineData(SaveSteamBehaviour.LastState, "lastState")]
    public void Token_RoundTripsThroughParse(SaveSteamBehaviour behaviour, string token)
    {
        Assert.Equal(token, AppSettings.SaveSteamBehaviourToken(behaviour));
        Assert.Equal(behaviour, AppSettings.ParseSaveSteamBehaviour(
            AppSettings.SaveSteamBehaviourToken(behaviour)));
    }

    [Fact]
    public void Token_OutOfRangeEnumFallsBackToAsk()
    {
        var token = AppSettings.SaveSteamBehaviourToken((SaveSteamBehaviour)99);

        Assert.Equal("ask", token);
        Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.ParseSaveSteamBehaviour(token));
    }

    [Fact]
    public void Read_MissingFile_ReturnsAskWithoutCreatingIt()
    {
        var result = AppSettings.Read(_path);

        Assert.Equal(SaveSteamBehaviour.Ask, result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Write_ThenRead_RoundTripsEveryBehaviour()
    {
        foreach (var behaviour in new[]
        {
            SaveSteamBehaviour.Ask,
            SaveSteamBehaviour.AlwaysOpen,
            SaveSteamBehaviour.NeverOpen,
            SaveSteamBehaviour.LastState,
        })
        {
            AppSettings.Write(_path, behaviour);
            Assert.Equal(behaviour, AppSettings.Read(_path));
        }
    }

    [Fact]
    public void Write_CreatesTheMissingParentDirectory()
    {
        var nested = Path.Combine(_root, "a", "b", "settings.json");

        AppSettings.Write(nested, SaveSteamBehaviour.LastState);

        Assert.True(File.Exists(nested));
        Assert.Equal(SaveSteamBehaviour.LastState, AppSettings.Read(nested));
    }

    [Fact]
    public void Write_UsesTheCanonicalKeyAndToken()
    {
        AppSettings.Write(_path, SaveSteamBehaviour.AlwaysOpen);

        var node = JsonNode.Parse(File.ReadAllText(_path));
        Assert.Equal("alwaysOpen", node?[AppSettings.SaveSteamBehaviourKey]?.GetValue<string>());
    }

    [Fact]
    public void Read_MalformedJson_FallsBackToAskWithoutThrowing()
    {
        File.WriteAllText(_path, "{ not json at all");

        Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.Read(_path));
    }

    [Fact]
    public void Read_WrongValueType_FallsBackToAskWithoutThrowing()
    {
        File.WriteAllText(_path, "{\"saveSteamBehaviour\": 42}");

        Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.Read(_path));
    }

    [Fact]
    public void Read_UnrecognisedValue_FallsBackToAsk()
    {
        File.WriteAllText(_path, "{\"saveSteamBehaviour\": \"sometimes\"}");

        Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.Read(_path));
    }

    [Fact]
    public void Read_MissingKey_FallsBackToAsk()
    {
        File.WriteAllText(_path, "{}");

        Assert.Equal(SaveSteamBehaviour.Ask, AppSettings.Read(_path));
    }

    [Fact]
    public void Read_UnrelatedKeysAreIgnored()
    {
        File.WriteAllText(_path,
            "{\"somethingElse\": \"x\", \"saveSteamBehaviour\": \"lastState\"}");

        Assert.Equal(SaveSteamBehaviour.LastState, AppSettings.Read(_path));
    }

    [Fact]
    public void FilePath_SitsInTheCloudReEditConfigFolder()
        => Assert.EndsWith(
            Path.Combine("CloudReEdit", "settings.json"),
            AppSettings.FilePath,
            StringComparison.Ordinal);
}
