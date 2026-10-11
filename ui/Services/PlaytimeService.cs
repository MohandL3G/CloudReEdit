using System;
using System.Collections.Generic;
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
        if (VdfEditor.TryGetAppPlaytimeValues(vdfPath, appId.ToString(),
            out var vp, out _, out var vl))
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
                    var fetch = await provider.GetAccountStatsAsync(accountId).ConfigureAwait(false);
                    ctx.CloudExists = fetch.Exists;
                    if (fetch.Exists &&
                        fetch.Parsed?.TryGetPropertyValue(appId.ToString(), out var appEl) == true &&
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
                finally
                {
                    (provider as IDisposable)?.Dispose();
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

    /// <summary>Runs the full multi-copy save protocol for
    /// <paramref name="targetTotal"/> minutes at the given last-played timestamp.
    /// <paramref name="source"/> is the frozen snapshot the user saw when clicking
    /// Save — the transaction must never read the live editor model. The cloud
    /// provider is created here (during pre-flight) so a bad or missing provider
    /// aborts before any file is touched.</summary>
    public static async Task<SaveReport> SaveAsync(EditorContext ctx,
        PlaytimeModel source,
        long targetTotal, long lastPlayed)
    {
        bool steamRunning = SteamDetector.IsSteamRunning();

        ICloudProvider? provider = null;
        var cfg = SteamDetector.ReadConfig();
        if (cfg != null)
        {
            var (created, error) = CloudProviderFactory.Create(cfg);
            if (created == null)
            {
                var report = new SaveReport { Outcome = SaveOutcome.NotSaved };
                report.Messages.Add(error ?? "Cloud provider could not be created. No files were changed.");
                return report;
            }
            provider = created;
        }

        var paths = new SavePaths
        {
            LocalStatsPath = LocalStatsStore.AppStatsPath(ctx.AccountId, ctx.AppId),
            VdfPath = LocalStatsStore.VdfPath(ctx.AccountId),
            BackupRoot = BackupRetention.DefaultRoot(),
        };

        var txn = new SaveTransaction(new SaveScope
        {
            AccountId = ctx.AccountId,
            AppId = ctx.AppId,
            Source = source,
        }, paths,
            provider != null && provider.CanWrite ? provider : null,
            targetTotal, lastPlayed, steamRunning);
        try
        {
            var report = await txn.RunAsync().ConfigureAwait(false);
            PruneAfterSave(paths.BackupRoot, report.BackupDir);
            return report;
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>Drops old backup directories once the save has fully finished.
    /// Runs strictly after the transaction so retention can never influence the
    /// outcome, and swallows every error so a pruning failure cannot change the
    /// report the user sees.</summary>
    private static void PruneAfterSave(string backupRoot, string? currentBackupDir)
    {
        try { BackupRetention.Prune(backupRoot, currentBackupDir); }
        catch { }
    }
}