namespace CloudReEdit.Services;

/// <summary>Pure Steam editing-availability text for the Accounts page. A missing
/// installation is its own state: "no process running" must never be reported as
/// "safe to edit" when Steam isn't installed at all.</summary>
public static class SteamStatus
{
    public const string NotInstalled = "Steam installation not found. Playtime editing is unavailable.";
    public const string Running = "Steam is RUNNING. Editing playtime is blocked while Steam is open. Use \"Close Steam\" first.";
    public const string Stopped = "Steam is not running. Playtime edits can be applied safely.";

    public static string Text(bool steamFound, bool running)
    {
        if (!steamFound)
            return NotInstalled;
        return running ? Running : Stopped;
    }
}