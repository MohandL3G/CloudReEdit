using System.Text.RegularExpressions;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class AppVersionTests
{
    [Fact]
    public void Display_ReturnsASemverLikeStringWithoutBuildMetadata()
    {
        var display = AppVersion.Display();

        Assert.False(string.IsNullOrWhiteSpace(display));
        Assert.DoesNotContain("+", display);
        Assert.Matches(new Regex(@"^\d+(\.\d+)*$"), display);
    }

    [Theory]
    [InlineData("0.2.7-pre-release+abc1234", "0.2.7-pre-release")]
    [InlineData("0.2.7-pre-release", "0.2.7-pre-release")]
    [InlineData("0.2.7+abc1234", "0.2.7")]
    [InlineData("0.2.7", "0.2.7")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void StripBuildMetadata_KeepsThePreReleaseSuffixAndDropsTheCommit(string? input, string? expected)
        => Assert.Equal(expected, AppVersion.StripBuildMetadata(input));

    [Fact]
    public void DisplayValue_DerivesFromCsprjAndMatchesTheWorkflowDefault()
    {
        var root = FindRepoRoot();
        Assert.True(root != null, "repo root (containing ui/CloudReEdit.csproj) was not found above the test assembly");

        var csproj = File.ReadAllText(Path.Combine(root!, "ui", "CloudReEdit.csproj"));
        var workflow = File.ReadAllText(Path.Combine(root!, ".github", "workflows", "build-release.yml"));

        var prefix = Match(csproj, @"<VersionPrefix>([^<]+)</VersionPrefix>");
        var suffix = Match(csproj, @"<VersionSuffix>([^<]+)</VersionSuffix>");
        var workflowDefault = Match(workflow, @"version:\s*\r?\n\s*description:[^\r?\n]*\r?\n\s*required:[^\r?\n]*\r?\n\s*default:\s*'([^']+)'");

        Assert.Matches(@"^\d+\.\d+\.\d+$", prefix);
        Assert.Matches(@"^[a-z0-9-]+$", suffix);

        // The workflow tags the release from its own input, so it must agree with
        // the version the app actually reports.
        Assert.Equal(prefix, workflowDefault);

        // Exactly one place decides the "-pre-release" tail of the displayed version.
        Assert.Equal($"{prefix}-{suffix}",
            AppVersion.StripBuildMetadata($"{prefix}-{suffix}+deadbeef"));
    }

    private static string Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        Assert.True(match.Success, $"pattern not found: {pattern}");
        return match.Groups[1].Value;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ui", "CloudReEdit.csproj")))
                return dir.FullName;
        }
        return null;
    }
}
