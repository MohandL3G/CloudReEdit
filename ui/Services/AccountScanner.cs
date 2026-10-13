using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CloudReEdit.Services;

public sealed class AccountInfo
{
    public string AccountId { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public string LocalStatsDir { get; set; } = "";

    public string DisplayName =>
        string.IsNullOrEmpty(PersonaName) ? AccountId : $"{PersonaName} ({AccountId})";
}

public sealed class GameEntry
{
    public string AccountId { get; set; } = "";
    public uint AppId { get; set; }
    public string Name { get; set; } = "";

    // Steam store header capsule URI for the list artwork. It is validated by
    // SteamStoreClient.IsValidImageUrl before it ever gets here, so an empty value
    // means "no usable artwork" and the template collapses the image column
    // rather than leaving a placeholder artifact behind.
    public string HeaderImage { get; set; } = "";

    public long PlaytimeMinutes { get; set; }
    public long LastPlayed { get; set; }

    public double Hours => PlaytimeMinutes / 60.0;
    public string PlaytimeDisplay => $"{PlaytimeMinutes} min ({Hours:0.##} h)";

    public string LastPlayedDisplay => LastPlayed > 0
        ? $"Last played: {DateTimeOffset.FromUnixTimeSeconds(LastPlayed).LocalDateTime:g}"
        : "Never played";
}

/// <summary>Enumerates CloudRedirect accounts and their games from the local
/// <c>cloud_redirect/stats</c> directory.</summary>
public static class AccountScanner
{
    private static readonly Regex PersonaRegex = new("\"PersonaName\"\\s+\"([^\"]+)\"");

    public static List<AccountInfo> ListAccounts()
    {
        var result = new List<AccountInfo>();
        var steamPath = SteamDetector.FindSteamPath();
        if (steamPath == null) return result;

        var statsRoot = Path.Combine(steamPath, "cloud_redirect", "stats");
        if (!Directory.Exists(statsRoot)) return result;

        var userdataRoot = Path.Combine(steamPath, "userdata");
        var skipped = 0;

        foreach (var dir in Directory.GetDirectories(statsRoot))
        {
            var accountId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(accountId)) continue;

            if (!IsAccountFolder(accountId, dir, userdataRoot))
            {
                skipped++;
                continue;
            }

            result.Add(new AccountInfo
            {
                AccountId = accountId,
                PersonaName = ReadPersonaName(steamPath, accountId),
                LocalStatsDir = dir,
            });
        }

        if (skipped > 0)
            AppLog.Info("accounts", $"skipped {skipped} non-account folder(s) under cloud_redirect\\stats");

        return result.OrderBy(a => a.AccountId, System.StringComparer.Ordinal).ToList();
    }

    /// <summary>True when <paramref name="accountId"/> has the shape of a Steam
    /// account id: a non-empty unsigned integer, the same shape
    /// <see cref="ListGames"/> already relies on for its <c>&lt;appId&gt;.json</c>
    /// files. Temp, backup and leftover folders can never satisfy this.</summary>
    public static bool LooksLikeSteamAccountId(string accountId)
        => !string.IsNullOrEmpty(accountId) && uint.TryParse(accountId, out _);

    /// <summary>Decides whether a directory under <c>cloud_redirect/stats</c> is a
    /// real Steam account. Pure apart from the two paths it is handed, so it can be
    /// unit tested against temp directories with no Steam install present.
    ///
    /// <para>A folder qualifies only when its name has a plausible account-id shape
    /// <b>and</b> either</para>
    /// <para>• a matching folder exists under <c>userdata</c> — the strongest signal
    /// available, and deliberately sufficient on its own so that a real account whose
    /// stats folder is temporarily empty stays listed rather than silently
    /// disappearing; or</para>
    /// <para>• it contains at least one numeric <c>&lt;appId&gt;.json</c>, the same
    /// shape <see cref="ListGames"/> enumerates.</para>
    ///
    /// <para>Everything else is skipped silently — no placeholder rows.</para></summary>
    public static bool IsAccountFolder(string accountId, string statsDir, string userdataRoot)
    {
        if (!LooksLikeSteamAccountId(accountId)) return false;

        try
        {
            if (Directory.Exists(Path.Combine(userdataRoot, accountId))) return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("accounts", $"userdata probe failed for account {accountId}: {ex.GetType().Name}");
        }

        return HasAnyGameStats(statsDir);
    }

    private static bool HasAnyGameStats(string statsDir)
    {
        try
        {
            if (!Directory.Exists(statsDir)) return false;

            foreach (var file in Directory.EnumerateFiles(statsDir, "*.json"))
            {
                if (uint.TryParse(Path.GetFileNameWithoutExtension(file), out _))
                    return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("accounts", $"stats probe failed for \"{statsDir}\": {ex.GetType().Name}");
        }

        return false;
    }

    public static List<GameEntry> ListGames(string accountId)
    {
        var result = new List<GameEntry>();
        var dir = LocalStatsStore.StatsDir(accountId);
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (!uint.TryParse(fileName, out var appId)) continue;

            var entry = new GameEntry
            {
                AccountId = accountId,
                AppId = appId,
                Name = GameNames.InitialName(appId),
            };

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (root?.TryGetPropertyValue("playtime", out var pt) == true && pt is JsonObject pto)
                {
                    var model = PlaytimeModel.Parse(pto);
                    entry.PlaytimeMinutes = model.Total;
                    entry.LastPlayed = model.LastPlayed;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("stats", $"app stats could not be parsed: \"{file}\" ({ex.GetType().Name})");
            }

            result.Add(entry);
        }

        return result.OrderByDescending(g => g.PlaytimeMinutes)
                     .ThenBy(g => g.AppId)
                     .ToList();
    }

    private static string ReadPersonaName(string steamPath, string accountId)
    {
        try
        {
            var vdf = Path.Combine(steamPath, "userdata", accountId, "config", "localconfig.vdf");
            if (!File.Exists(vdf)) return "";

            var m = PersonaRegex.Match(File.ReadAllText(vdf));
            return m.Success ? m.Groups[1].Value : "";
        }
        catch (Exception ex)
        {
            AppLog.Debug("stats", $"persona name read failed for account {accountId}: {ex.GetType().Name}");
            return "";
        }
    }
}
