using System;
using System.IO;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

/// <summary>Exercises <see cref="AccountScanner.IsAccountFolder"/> against temp
/// directories so no Steam install is required. The rule under test: a folder under
/// <c>cloud_redirect/stats</c> counts as an account only when its name has a
/// plausible account-id shape AND either a matching <c>userdata</c> folder exists or
/// the folder holds at least one numeric &lt;appId&gt;.json.</summary>
public sealed class AccountFolderValidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cre-accounts-").FullName;

    private string UserdataRoot => Path.Combine(_root, "userdata");
    private string StatsRoot => Path.Combine(_root, "cloud_redirect", "stats");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string MakeStatsDir(string accountId)
    {
        var dir = Path.Combine(StatsRoot, accountId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string MakeUserdataDir(string accountId)
    {
        var dir = Path.Combine(UserdataRoot, accountId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name)
        => File.WriteAllText(Path.Combine(dir, name), "{}");

    // ---- looks like a Steam account id ------------------------------------------

    [Theory]
    [InlineData("76561198000000000")]
    [InlineData("0")]
    [InlineData("42")]
    public void LooksLikeSteamAccountId_AcceptsNumericIdShapes(string id)
        => Assert.True(AccountScanner.LooksLikeSteamAccountId(id));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("temp")]
    [InlineData("backup_old")]
    [InlineData("123abc")]
    [InlineData("-5")]
    [InlineData("99999999999999999999999")]
    public void LooksLikeSteamAccountId_RejectsNonAccountShapes(string id)
        => Assert.False(AccountScanner.LooksLikeSteamAccountId(id));

    // ---- accept cases -----------------------------------------------------------

    [Fact]
    public void Accepts_NumericId_WithUserdataFolder_AndEmptyStats()
    {
        // A real account whose stats folder is temporarily empty must still be
        // listed: userdata presence alone is sufficient evidence.
        var stats = MakeStatsDir("123456");
        MakeUserdataDir("123456");

        Assert.True(AccountScanner.IsAccountFolder("123456", stats, UserdataRoot));
    }

    [Fact]
    public void Accepts_NumericId_WithGameStats_AndNoUserdataFolder()
    {
        // Stats content alone is also enough.
        var stats = MakeStatsDir("7654321");
        WriteFile(stats, "440.json");

        Assert.True(AccountScanner.IsAccountFolder("7654321", stats, UserdataRoot));
    }

    [Fact]
    public void Accepts_NumericId_WithBothSignals()
    {
        var stats = MakeStatsDir("999");
        WriteFile(stats, "570.json");
        MakeUserdataDir("999");

        Assert.True(AccountScanner.IsAccountFolder("999", stats, UserdataRoot));
    }

    // ---- reject cases (the decoys from issue #2) --------------------------------

    [Fact]
    public void Rejects_NonNumericName_EvenWithUserdataAndStats()
    {
        var stats = MakeStatsDir("temp");
        WriteFile(stats, "440.json");
        MakeUserdataDir("temp");

        Assert.False(AccountScanner.IsAccountFolder("temp", stats, UserdataRoot));
    }

    [Fact]
    public void Rejects_NumericId_WithNeitherUserdataNorStats()
    {
        var stats = MakeStatsDir("111222");

        Assert.False(AccountScanner.IsAccountFolder("111222", stats, UserdataRoot));
    }

    [Fact]
    public void Rejects_NumericId_WithOnlyNonNumericJson()
    {
        // A stray notes.json / backup.json must not qualify a folder.
        var stats = MakeStatsDir("333444");
        WriteFile(stats, "notes.json");
        WriteFile(stats, "backup.json");

        Assert.False(AccountScanner.IsAccountFolder("333444", stats, UserdataRoot));
    }

    [Fact]
    public void Rejects_NumericId_WithOnlyNonJsonFiles()
    {
        var stats = MakeStatsDir("555666");
        WriteFile(stats, "440.txt");

        Assert.False(AccountScanner.IsAccountFolder("555666", stats, UserdataRoot));
    }

    [Fact]
    public void Rejects_NumericId_WhenStatsDirDoesNotExist()
    {
        var missing = Path.Combine(StatsRoot, "does-not-exist");

        Assert.False(AccountScanner.IsAccountFolder("777888", missing, UserdataRoot));
    }

    [Fact]
    public void Rejects_EmptyAccountId()
    {
        var stats = MakeStatsDir("whatever");

        Assert.False(AccountScanner.IsAccountFolder("", stats, UserdataRoot));
    }

    // ---- must not degrade on missing infrastructure -----------------------------

    [Fact]
    public void DoesNotThrow_WhenUserdataRootIsMissing()
    {
        // No userdata tree at all: falls through to the stats-content check
        // rather than throwing or rejecting a folder that does have stats.
        var stats = MakeStatsDir("121212");
        WriteFile(stats, "440.json");
        var missingUserdata = Path.Combine(_root, "no-such-userdata");

        Assert.True(AccountScanner.IsAccountFolder("121212", stats, missingUserdata));
    }

    [Fact]
    public void Rejects_WhenBothRootsAreMissingEntirely()
    {
        Assert.False(AccountScanner.IsAccountFolder(
            "131313",
            Path.Combine(_root, "no-stats"),
            Path.Combine(_root, "no-userdata")));
    }
}
