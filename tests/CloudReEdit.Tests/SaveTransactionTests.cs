using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CloudReEdit.Services;
using CloudReEdit.Services.Cloud;
using Xunit;

namespace CloudReEdit.Tests;

public class SaveTransactionTests
{
    private const string AccountId = "76561198000000000";
    private const uint AppId = 123;
    private const string AppKey = "123";
    private const string SixTabs = "\t\t\t\t\t\t";

    [Fact]
    public async Task SteamRunning_NotSavedAndNoMutations()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        var report = await Run(scope, paths, fake, total: 150, steamRunning: true);

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Null(report.BackupDir);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
    }

    [Fact]
    public async Task MalformedLocalJson_NotSavedAndNoMutations()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        File.WriteAllText(paths.LocalStatsPath, "this is not json {");
        byte[] malformed = File.ReadAllBytes(paths.LocalStatsPath);

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Null(report.BackupDir);
        Assert.Equal(malformed, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
    }

    [Fact]
    public async Task MissingVdfBlock_NotSavedAndNoMutations()
    {
        using var env = Setup(out var scope, out var paths, out _, out var beforeLocal, out _);
        string blockless = BuildVdf("");
        File.WriteAllText(paths.VdfPath, blockless);

        var report = await Run(scope, paths, cloud: null, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(blockless, File.ReadAllText(paths.VdfPath));
    }

    [Fact]
    public async Task MissingVdfFile_SkipsVdfAndSavesOthers()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        File.Delete(paths.VdfPath);

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        Assert.Equal(CopyState.SkippedAbsentVdf, report.Copies.First(c => c.Target == CopyTarget.Vdf).State);
        Assert.Equal(CopyState.Verified, report.Copies.First(c => c.Target == CopyTarget.LocalJson).State);
        Assert.Equal(CopyState.Verified, report.Copies.First(c => c.Target == CopyTarget.CloudBlob).State);
        Assert.False(File.Exists(paths.VdfPath));
        Assert.Equal(150, ReadLocalModel(paths.LocalStatsPath).Total);
    }

    [Fact]
    public async Task Success_AllCopiesVerified_BackupsExactBytes()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes(
            "{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        string originalCloud644 = Encoding.UTF8.GetString(cloudBefore);

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        Assert.All(report.Copies, c => Assert.Equal(CopyState.Verified, c.State));
        Assert.NotNull(report.BackupDir);
        Assert.True(Directory.Exists(report.BackupDir));

        // VDF patched + verified.
        string vdfText = File.ReadAllText(paths.VdfPath);
        Assert.Contains("\"Playtime\"\t\t\"150\"", vdfText);

        // Local stats JSON adjusted to target total.
        var localModel = ReadLocalModel(paths.LocalStatsPath);
        Assert.Equal(150, localModel.Total);
        Assert.Contains(localModel.PerDevice,
            d => d.Name == "__migrated_localconfig" && d.Windows == 50);

        // Cloud blob has our target and preserved the unrelated app.
        Assert.NotNull(fake.Blob);
        var cloudRoot = JsonNode.Parse(Encoding.UTF8.GetString(fake.Blob!)) as JsonObject;
        Assert.NotNull(cloudRoot);
        Assert.NotNull(cloudRoot!["456"]);
        var cloudPlaytime = cloudRoot![AppKey]!["playtime"] as JsonObject;
        Assert.Equal(150, PlaytimeModel.Parse(cloudPlaytime).Total);
        Assert.False(fake.LastIfNoneMatch);
        Assert.Equal(FakeCloud.Sha(cloudBefore), fake.LastIfMatch);
        Assert.False(fake.DeleteCalled);

        // Backups: exact raw bytes.
        Assert.Equal(beforeLocal, File.ReadAllBytes(Path.Combine(report.BackupDir, "local_app.json")));
        Assert.Equal(originalCloud644, File.ReadAllText(Path.Combine(report.BackupDir, "cloud_stats.json")));
        Assert.NotNull(File.ReadAllBytes(Path.Combine(report.BackupDir, "localconfig.vdf")));
        Assert.True(File.Exists(Path.Combine(report.BackupDir, "target.json")));

        // Manifest records phases + verified copy states.
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(report.BackupDir, "manifest.json")))!;
        Assert.Equal(1, manifest["SchemaVersion"]!.GetValue<int>());
        Assert.Equal(AccountId, manifest["AccountId"]!.GetValue<string>());
        Assert.Equal(AppId, manifest["AppId"]!.GetValue<uint>());
        Assert.Equal("complete", manifest["Phase"]!.GetValue<string>());
        Assert.Equal("saved_verified", manifest["Result"]!.GetValue<string>());
        Assert.Equal(150, manifest["Target"]!["TotalMinutes"]!.GetValue<long>());
        Assert.Equal("verified", manifest["Local"]!["State"]!.GetValue<string>());
        Assert.Equal("verified", manifest["Cloud"]!["State"]!.GetValue<string>());
        Assert.Equal("verified", manifest["Vdf"]!["State"]!.GetValue<string>());
        Assert.True(manifest["Vdf"]!["ExistedBefore"]!.GetValue<bool>());
        Assert.Equal(FakeCloud.Sha(cloudBefore), manifest["Cloud"]!["ETag"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateCloudWhenAbsent_UsesIfNoneMatch()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        fake.Blob = null;

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        Assert.True(fake.LastIfNoneMatch);
        Assert.Null(fake.LastIfMatch);
        var cloudPlaytime = (JsonNode.Parse(Encoding.UTF8.GetString(fake.Blob!)) as JsonObject)![AppKey]!["playtime"] as JsonObject;
        Assert.Equal(150, PlaytimeModel.Parse(cloudPlaytime).Total);
    }

    [Fact]
    public async Task CloudConcurrent_RejectedAndUnchanged_RollsBackAndReportsConcurrency()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes("{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        fake.PutBehavior = _ => new CloudChangedConcurrentlyException("fake precondition failure");

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.CloudChangedConcurrently, report.Outcome);
        Assert.Equal(FakeCloud.Sha(cloudBefore), fake.LastIfMatch);
        Assert.Null(fake.LastPutRaw);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));

        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.Vdf]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.LocalJson]);
        Assert.Equal(CopyState.WriteFailed, copyStates[CopyTarget.CloudBlob]);
    }

    [Fact]
    public async Task CloudConcurrent_ButTargetAlreadyCommitted_DoesNotRestoreOld()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        byte[] cloudBefore = Encoding.UTF8.GetBytes("{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        byte[] targetBlob = MakeBlobWithTarget(150, 150, lastPlayed: 1700000000);
        fake.PutBehavior = _ =>
        {
            fake.Blob = targetBlob;
            return new CloudChangedConcurrentlyException("fake precondition failure");
        };

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedUnverified, report.Outcome);
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.Unverified, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.LocalJson]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.Vdf]);
    }

    [Fact]
    public async Task CloudOutOfSync_DivergentContentPreserved_EarlierCopiesRolledBack()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes("{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        byte[] divergent = Encoding.UTF8.GetBytes(
            "{\"123\":{\"playtime\":" + MakeModelJson(999, 999, 111) + "},\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.PutBehavior = _ =>
        {
            fake.Blob = divergent;
            return new CloudChangedConcurrentlyException("fake precondition failure");
        };

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.CloudDiverged, report.Outcome);
        Assert.Equal(divergent, fake.Blob);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.Divergent, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.Vdf]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.LocalJson]);
    }

    [Fact]
    public async Task CloudWriteFailed_AndReReadFails_StateUnknown_EarlierCopiesRolledBack()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        fake.PutBehavior = _ =>
        {
            fake.ThrowGet = true;
            throw new InvalidOperationException("fake network failure");
        };

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.CloudStateUnknown, report.Outcome);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.Unverified, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.Vdf]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.LocalJson]);
    }

    [Fact]
    public async Task CloudPutOk_ButReReadFails_SavedUnverifiedKeepsEarlierCopies()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        fake.PutBehavior = _ =>
        {
            fake.ThrowGet = true;
            return null;
        };

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedUnverified, report.Outcome);
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.Unverified, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.LocalJson]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.Vdf]);
    }

    [Fact]
    public async Task CloudGenericWriteFailure_UnchangedRemote_ReportsWriteFailed()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes(
            "{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        fake.PutBehavior = _ => new InvalidOperationException("fake auth failure");

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.CloudWriteFailed, report.Outcome);
        Assert.NotEqual(SaveOutcome.CloudDiverged, report.Outcome);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.WriteFailed, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.Vdf]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.LocalJson]);
    }

    [Fact]
    public async Task CloudPutVanishedAfter2xx_ReportsWriteFailedAndRollsBack()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes(
            "{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;
        fake.DropPut = true;

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.CloudWriteFailed, report.Outcome);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
        Assert.Equal(cloudBefore, fake.Blob);
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.WriteFailed, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.Vdf]);
        Assert.Equal(CopyState.RolledBack, copyStates[CopyTarget.LocalJson]);
    }

    [Fact]
    public async Task PreflightFailure_DoesNotCreateMissingStatsDir()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        File.Delete(paths.LocalStatsPath);
        string statsDir = Path.GetDirectoryName(paths.LocalStatsPath)!;
        Directory.Delete(statsDir, recursive: true);
        File.WriteAllText(paths.VdfPath, BuildVdf(""));

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Null(report.BackupDir);
        Assert.False(Directory.Exists(statsDir));
    }

    [Fact]
    public async Task BackupFailure_NoMutations()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        File.WriteAllText(paths.BackupRoot, "not a directory");

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.BackupFailed, report.Outcome);
        Assert.Null(report.BackupDir);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
    }

    [Fact]
    public async Task BackupFailure_CleansUpIncompleteBackupDir()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        var txn = new FailingBackupTxn(scope, paths, fake, 150, 1700000000, steamRunning: false);

        var report = await txn.RunAsync();

        Assert.Equal(SaveOutcome.BackupFailed, report.Outcome);
        Assert.Null(report.BackupDir);
        var leftover = Directory.GetDirectories(paths.BackupRoot);
        Assert.Empty(leftover);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
    }

    [Fact]
    public async Task ReadOnlyCloudProvider_SkipsCloudAndSavesLocal()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        var readOnly = new ReadOnlyCloud();

        var report = await Run(scope, paths, readOnly, total: 150, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        var copyStates = report.Copies.ToDictionary(c => c.Target, c => c.State);
        Assert.Equal(CopyState.SkippedReadOnlyCloud, copyStates[CopyTarget.CloudBlob]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.LocalJson]);
        Assert.Equal(CopyState.Verified, copyStates[CopyTarget.Vdf]);
    }

    [Fact]
    public async Task Save_UsesFrozenSnapshot_NotLaterMutationsOfLiveModel()
    {
        var live = new PlaytimeModel();
        var pc = new DevicePlaytime { Name = "pc", Windows = 100 };
        pc.Days[20340] = 60;
        live.PerDevice.Add(pc);

        var snapshot = live.Clone();

        // The editor model keeps changing after Save is clicked; the transaction
        // must have been handed the frozen snapshot and ignore the live model.
        live.PerDevice[0].Windows = 900;
        live.PerDevice[0].Name = "OTHER-PC";
        live.PerDevice[0].Days[20340] = 1;
        live.PerDevice.Add(new DevicePlaytime { Name = "extraneous", Windows = 50 });

        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);
        scope.Source = snapshot;

        var report = await Run(scope, paths, fake, total: 100, steamRunning: false);

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        var written = ReadLocalModel(paths.LocalStatsPath);
        Assert.Equal(100, written.Total);
        Assert.Single(written.PerDevice);
        Assert.Equal("pc", written.PerDevice[0].Name);
        Assert.Equal(60, written.PerDevice[0].Days[20340]);
    }

    [Fact]
    public async Task FreshSteamCheck_SteamStartedDuringSave_AbortsWithoutAnyMutation()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);
        byte[] cloudBefore = Encoding.UTF8.GetBytes(
            "{\"456\":{\"playtime\":" + MakeModelJson(77, 77, 999) + "}}");
        fake.Blob = cloudBefore;

        int calls = 0;
        var report = await Run(scope, paths, fake, total: 150, steamRunning: false,
            isSteamRunningNow: () => { calls++; return true; });

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Null(report.BackupDir);
        Assert.False(Directory.Exists(paths.BackupRoot));
        Assert.Contains(report.Messages,
            m => m.Contains("Steam started while saving", StringComparison.Ordinal));
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
        Assert.Equal(cloudBefore, fake.Blob);
        Assert.Equal(CopyState.NotAttempted,
            report.Copies.First(c => c.Target == CopyTarget.Vdf).State);
        Assert.Equal(CopyState.NotAttempted,
            report.Copies.First(c => c.Target == CopyTarget.LocalJson).State);
        Assert.Equal(CopyState.NotAttempted,
            report.Copies.First(c => c.Target == CopyTarget.CloudBlob).State);
    }

    [Fact]
    public async Task FreshSteamCheck_RunsExactlyOnce_AfterAPreflightThatPassed()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);

        int calls = 0;
        var report = await Run(scope, paths, fake, total: 150, steamRunning: false,
            isSteamRunningNow: () => { calls++; return false; });

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FreshSteamCheck_IsSkippedWhenPreflightAlreadyAborted()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out var beforeLocal, out var beforeVdf);

        int calls = 0;
        var report = await Run(scope, paths, fake, total: 150, steamRunning: true,
            isSteamRunningNow: () => { calls++; return true; });

        Assert.Equal(SaveOutcome.NotSaved, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Null(report.BackupDir);
        Assert.Equal(beforeLocal, File.ReadAllBytes(paths.LocalStatsPath));
        Assert.Equal(beforeVdf, File.ReadAllBytes(paths.VdfPath));
    }

    [Fact]
    public async Task FreshSteamCheck_ThrowingProbe_DoesNotBlockTheSave()
    {
        using var env = Setup(out var scope, out var paths, out var fake, out _, out _);

        var report = await Run(scope, paths, fake, total: 150, steamRunning: false,
            isSteamRunningNow: () => throw new InvalidOperationException("probe failure"));

        Assert.Equal(SaveOutcome.SavedVerified, report.Outcome);
    }

    // ---------------- helpers ----------------

    private static async Task<SaveReport> Run(SaveScope scope, SavePaths paths,
        CloudReEdit.Services.Cloud.ICloudProvider? cloud, long total, bool steamRunning,
        Func<bool>? isSteamRunningNow = null)
    {
        var txn = new SaveTransaction(scope, paths, cloud, total, 1700000000, steamRunning,
            isSteamRunningNow);
        return await txn.RunAsync();
    }

    private TempEnv Setup(out SaveScope scope, out SavePaths paths, out FakeCloud fake,
        out byte[] beforeLocal, out byte[] beforeVdf)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudReEdit_Test_" + Guid.NewGuid().ToString("N"));
        var statsDir = Path.Combine(root, "stats");
        var vdfDir = Path.Combine(root, "vdf");
        Directory.CreateDirectory(statsDir);
        Directory.CreateDirectory(vdfDir);

        paths = new SavePaths
        {
            LocalStatsPath = Path.Combine(statsDir, AppKey + ".json"),
            VdfPath = Path.Combine(vdfDir, "localconfig.vdf"),
            BackupRoot = Path.Combine(root, "backups"),
        };

        var model = new PlaytimeModel();
        model.PerDevice.Add(new DevicePlaytime { Name = "pc", Windows = 100 });
        var localRoot = new JsonObject { ["playtime"] = model.ToJson() };
        File.WriteAllText(paths.LocalStatsPath,
            localRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(paths.VdfPath, BuildVdf(AppBlock(AppKey,
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"LastPlayed\"\t\t\"1\"\n")));

        beforeLocal = File.ReadAllBytes(paths.LocalStatsPath);
        beforeVdf = File.ReadAllBytes(paths.VdfPath);

        scope = new SaveScope { AccountId = AccountId, AppId = AppId, Source = model };
        fake = new FakeCloud();
        return new TempEnv(root);
    }

    private static PlaytimeModel ReadLocalModel(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var pt = root!["playtime"] as JsonObject;
        return PlaytimeModel.Parse(pt);
    }

    private static byte[] MakeBlobWithTarget(long total, long twoWeeks, long lastPlayed)
    {
        var model = new PlaytimeModel();
        model.PerDevice.Add(new DevicePlaytime { Name = "pc", Windows = 100 });
        model.PerDevice.Add(new DevicePlaytime { Name = "__migrated_localconfig", Windows = total - 100 });
        model.LastPlayed = lastPlayed;
        model.Minutes2Weeks = twoWeeks;
        var appObj = new JsonObject { ["playtime"] = model.ToJson() };
        return Encoding.UTF8.GetBytes(new JsonObject { [AppKey] = appObj }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string MakeModelJson(long total, long twoWeeks, long lastPlayed)
    {
        var model = new PlaytimeModel();
        model.PerDevice.Add(new DevicePlaytime { Name = "pc", Windows = total });
        model.LastPlayed = lastPlayed;
        model.Minutes2Weeks = twoWeeks;
        return model.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildVdf(string appBlocks) =>
        "\"UserLocalConfigStore\"\n{\n" +
        "\t\"Software\"\n\t{\n" +
        "\t\t\"Valve\"\n\t\t{\n" +
        "\t\t\t\"Steam\"\n\t\t\t{\n" +
        "\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        appBlocks +
        "\t\t\t\t}\n" +
        "\t\t\t}\n" +
        "\t\t}\n" +
        "\t}\n" +
        "}\n";

    private static string AppBlock(string appId, string inner) =>
        $"\t\t\t\t\t\"{appId}\"\n" +
        "\t\t\t\t\t{\n" +
        inner +
        "\t\t\t\t\t}\n";

    private sealed class TempEnv : IDisposable
    {
        private readonly string _root;
        public TempEnv(string root) => _root = root;
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class ReadOnlyCloud : CloudReEdit.Services.Cloud.ICloudProvider
    {
        public string DisplayName => "read-only";
        public bool CanWrite => false;

        public Task<AccountStatsFetch> GetAccountStatsAsync(string accountId)
            => Task.FromResult(new AccountStatsFetch { Exists = false });

        public Task PutAccountStatsAsync(string accountId, byte[] raw,
            string? ifMatchETag = null, bool ifNoneMatchCreate = false)
            => throw new NotSupportedException();

        public Task DeleteAccountStatsAsync(string accountId)
            => throw new NotSupportedException();
    }

    private sealed class FailingBackupTxn : SaveTransaction
    {
        public FailingBackupTxn(SaveScope scope, SavePaths paths,
            CloudReEdit.Services.Cloud.ICloudProvider? cloud,
            long requestedTotal, long requestedLastPlayed, bool steamRunning)
            : base(scope, paths, cloud, requestedTotal, requestedLastPlayed, steamRunning)
        {
        }

        internal override void WriteBackupFile(string fileName, byte[] data)
        {
            if (fileName == "local_app.json")
                throw new IOException("injected backup write failure");
            base.WriteBackupFile(fileName, data);
        }
    }
}