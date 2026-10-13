using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CloudReEdit.Services;

/// <summary>Shared Steam process control, so the shutdown loop lives in exactly
/// one place instead of being copied into every caller that needs Steam out of
/// the way.</summary>
public static class SteamController
{
    /// <summary>30 polls x 500ms = 15s, matching the shutdown wait the Accounts
    /// page used to run inline.</summary>
    public const int ShutdownPolls = 30;
    public const int ShutdownPollDelayMs = 500;

    /// <summary>Asks Steam to shut down and waits for the process to actually
    /// exit. Returns true when Steam is confirmed not running - including when it
    /// was never running to begin with, so callers can treat "true" as "safe to
    /// edit". Returns false only when Steam is demonstrably still alive.</summary>
    public static async Task<bool> CloseSteamAsync()
    {
        if (!SteamDetector.IsSteamRunning()) return true;

        var steamPath = SteamDetector.FindSteamPath();
        if (steamPath == null)
        {
            AppLog.Warn("steam", "steam is running but its install path is unknown, cannot shut it down");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(steamPath, "steam.exe"),
                Arguments = "-shutdown",
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Exception("steam", "steam shutdown request failed to launch", ex);
            return false;
        }

        for (int i = 0; i < ShutdownPolls; i++)
        {
            await Task.Delay(ShutdownPollDelayMs);

            if (!SteamDetector.IsSteamRunning())
            {
                AppLog.Info("steam", $"steam shut down after {(i + 1) * ShutdownPollDelayMs} ms");
                return true;
            }
        }

        AppLog.Warn("steam", $"steam did not exit within {ShutdownPolls * ShutdownPollDelayMs} ms");
        return false;
    }

    /// <summary>Launches Steam. Returns false when it could not be started, so a
    /// caller can report it rather than silently leaving Steam closed.</summary>
    public static bool StartSteam()
    {
        try
        {
            var steamPath = SteamDetector.FindSteamPath();
            if (steamPath == null)
            {
                AppLog.Warn("steam", "cannot launch steam, install path is unknown");
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(steamPath, "steam.exe"),
                UseShellExecute = true,
            })?.Dispose();

            AppLog.Info("steam", "steam launch requested");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("steam", "steam launch failed", ex);
            return false;
        }
    }
}
