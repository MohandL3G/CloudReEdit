using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudReEdit.Services;

/// <summary>Paths + read/write for the local CloudRedirect stats JSON files and
/// the per-account localconfig.vdf.</summary>
public static class LocalStatsStore
{
    public static string? SteamPath => SteamDetector.FindSteamPath();

    public static string StatsDir(string accountId)
        => Path.Combine(SteamPath ?? "", "cloud_redirect", "stats", accountId);

    public static string AppStatsPath(string accountId, uint appId)
        => Path.Combine(StatsDir(accountId), $"{appId}.json");

    public static string VdfPath(string accountId)
        => Path.Combine(SteamPath ?? "", "userdata", accountId, "config", "localconfig.vdf");

    public static JsonObject? ReadAppStats(string accountId, uint appId)
    {
        var path = AppStatsPath(accountId, appId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            AppLog.Exception("stats", $"app stats read failed: \"{path}\"", ex);
            return null;
        }
    }

    public static void WriteAppStats(string accountId, uint appId, JsonObject stats)
    {
        var path = AppStatsPath(accountId, appId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileUtils.AtomicWriteAllText(path, stats.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
