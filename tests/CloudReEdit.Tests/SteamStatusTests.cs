using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class SteamStatusTests
{
    [Fact]
    public void FoundAndStopped_IsSafeToEdit()
    {
        var text = SteamStatus.Text(steamFound: true, running: false);

        Assert.Equal(SteamStatus.Stopped, text);
        Assert.Contains("safe", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FoundAndRunning_IsBlocked()
    {
        var text = SteamStatus.Text(steamFound: true, running: true);

        Assert.Equal(SteamStatus.Running, text);
        Assert.Contains("blocked", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotFound_IsUnavailableAndNeverSafe()
    {
        var text = SteamStatus.Text(steamFound: false, running: false);

        Assert.Equal(SteamStatus.NotInstalled, text);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe", text, StringComparison.OrdinalIgnoreCase);
    }
}