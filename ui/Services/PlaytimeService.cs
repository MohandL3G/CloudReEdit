using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Services;

/// <summary>Snapshot of all three copies for one app, loaded for editing.</summary>
public sealed class EditorContext
{
    public string AccountId { get; set; } = "";
    public uint AppId { get; set; }

    public PlaytimeModel Local { get; set; } = new();
    public PlaytimeModel? Cloud { get; set; }
    public bool CloudExists { get; set; }
    public bool CloudCanWrite { get; set; }
    public string? CloudProviderName { get; set; }
    public string? CloudError { get; set; }

    public long VdfPlaytime { get; set; }
    public long VdfLastPlayed { get; set; }

    public List<string> Warnings { get; } = new();
}

public sealed class SaveResult
{
    public bool Success { get; set; }
    public List<string> Written { get; } = new();
    public string? Error { get; set; }
}

public static class PlaytimeService
{
    public static async Task<EditorContext> LoadAsync(string accountId, uint appId)
    {
        var ctx = new EditorContext { AccountId = accountId, AppId = appId };

        var local = LocalStatsStore.ReadAppStats(accountId, appId);
        ctx.Local = PlaytimeModel.Parse(
            local?.TryGetPropertyValue("playtime", out var pt) == true
                ? pt as JsonObject
                : null);

        var vdfPath = LocalStatsStore.VdfPath(accountId);
        if (VdfEditor.TryGetAppValues(vdfPath, appId.ToString(), out var vp, out var vl))
        {
            ctx.VdfPlaytime = vp;
            ctx.VdfLastPlayed = vl;
        }

        var cfg = SteamDetector.ReadConfig();
        if (cfg != null)
        {
            ctx.CloudProviderName = cfg.DisplayName;
            var (provider, error) = CloudProviderFactory.Create(cfg);
            if (provider == null)
            {
                ctx.CloudError = error;
            }
            else
            {
                ctx.CloudCanWrite = provider.CanWrite;
                try
                {
                    var blob = await provider.GetAccountStatsAsync(accountId).ConfigureAwait(false);
                    ctx.CloudExists = blob != null;
                    if (blob?.TryGetPropertyValue(appId.ToString(), out var appEl) == true &&
                        appEl is JsonObject appObj &&
                        appObj.TryGetPropertyValue("playtime", out var cloudPt) &&
                        cloudPt is JsonObject cloudPto)
                    {
                        ctx.Cloud = PlaytimeModel.Parse(cloudPto);
                    }
                }
                catch (Exception ex)
                {
                    ctx.CloudError = ex.Message;
                }
            }
        }

        BuildWarnings(ctx);
        return ctx;
    }

    private static void BuildWarnings(EditorContext ctx)
    {
        if (ctx.VdfPlaytime > 0 && ctx.VdfPlaytime != ctx.Local.Total)
            ctx.Warnings.Add(
                $"localconfig.vdf shows {ctx.VdfPlaytime} min, local JSON shows {ctx.Local.Total} min. The DLL's reconcile will use the VDF value.");
        if (ctx.Cloud != null && ctx.Cloud.Total != ctx.Local.Total)
            ctx.Warnings.Add(
                $"Cloud shows {ctx.Cloud.Total} min, local JSON shows {ctx.Local.Total} min. Saving rewrites both consistently.");
    }

    /// <summary>Backs up all three copies, then writes them consistently at
    /// <paramref name="targetTotal"/> minutes.</summary>
    public static async Task<SaveResult> SaveAsync(EditorContext ctx,
        ICloudProvider? provider, long targetTotal, long lastPlayed)
    {
        var result = new SaveResult();
        try
        {
            var model = ctx.Local;
            model.AdjustBucketsToTotal(targetTotal);
            model.LastPlayed = lastPlayed;
            model.Minutes2Weeks = model.Total;

            var backupDir = CreateBackupDir();

            // 1. Local app stats JSON
            var local = LocalStatsStore.ReadAppStats(ctx.AccountId, ctx.AppId) ?? new JsonObject();
            Backup(local, Path.Combine(backupDir, "local_app.json"));
            local["playtime"] = model.ToJson();
            LocalStatsStore.WriteAppStats(ctx.AccountId, ctx.AppId, local);
            result.Written.Add("Local stats JSON");

            // 2. Cloud account blob
            if (provider != null && provider.CanWrite)
            {
                var blob = await provider.GetAccountStatsAsync(ctx.AccountId).ConfigureAwait(false);
                if (blob == null) blob = new JsonObject();
                Backup(blob, Path.Combine(backupDir, "cloud_stats.json"));
                var appKey = ctx.AppId.ToString();
                var appObj = blob[appKey] as JsonObject;
                if (appObj == null)
                {
                    appObj = new JsonObject();
                    blob[appKey] = appObj;
                }
                appObj["playtime"] = model.ToJson();
                await provider.PutAccountStatsAsync(ctx.AccountId, blob).ConfigureAwait(false);
                result.Written.Add($"Cloud ({provider.DisplayName})");
            }

            // 3. localconfig.vdf
            var vdfPath = LocalStatsStore.VdfPath(ctx.AccountId);
            if (File.Exists(vdfPath))
            {
                BackupFile(vdfPath, Path.Combine(backupDir, "localconfig.vdf"));
                if (!VdfEditor.PatchAppPlaytime(vdfPath, ctx.AppId.ToString(), model.Total, lastPlayed))
                    throw new InvalidOperationException(
                        $"Could not find app {ctx.AppId} block in {vdfPath}. No files were changed.");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static string CreateBackupDir()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudReEdit", "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void BackupFile(string source, string dest)
    {
        try { FileUtils.AtomicCopy(source, dest); } catch { }
    }

    private static void Backup(JsonNode node, string dest)
    {
        try
        {
            FileUtils.AtomicWriteAllText(dest,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
