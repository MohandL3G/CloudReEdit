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

        foreach (var dir in Directory.GetDirectories(statsRoot))
        {
            var accountId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(accountId)) continue;

            result.Add(new AccountInfo
            {
                AccountId = accountId,
                PersonaName = ReadPersonaName(steamPath, accountId),
                LocalStatsDir = dir,
            });
        }

        return result.OrderBy(a => a.AccountId, System.StringComparer.Ordinal).ToList();
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
            catch
            {
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
        catch
        {
            return "";
        }
    }
}
