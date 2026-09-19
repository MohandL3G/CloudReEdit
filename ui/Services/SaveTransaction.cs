using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Services;

/// <summary>
/// The multi-copy save protocol: pre-flight (nothing mutated), byte-exact backup
/// of all three copies, then write order VDF -> local JSON -> cloud blob with
/// verification after each step. Any failed later copy rolls the earlier written
/// copies back to their exact pre-save bytes.
///
/// Cloud safety rule: the pre-save cloud snapshot is never blindly re-uploaded.
/// After a cloud failure the object is re-fetched and classified: contains our
/// target -> committed (no restore); byte-equal to the pre-save snapshot -> our
/// write did not land; anything else -> a concurrent writer won and its content
/// is preserved.
/// </summary>
internal class SaveTransaction
{
    private readonly SaveScope _scope;
    private readonly SavePaths _paths;
    private readonly ICloudProvider? _cloud;
    private readonly bool _steamRunning;
    private readonly long _requestedTotal;
    private readonly long _requestedLastPlayed;

    private readonly SaveReport _report = new();
    private PlaytimeModel _target = new();
    private SaveOutcome? _outcomeSet;

    private byte[]? _localBeforeRaw;
    private JsonObject? _localBeforeParsed;
    private byte[]? _vdfBeforeRaw;
    private bool _vdfExists;

    private bool _cloudExistedBefore;
    private byte[]? _cloudBeforeRaw;
    private string? _cloudBeforeETag;
    private JsonObject? _cloudBeforeParsed;

    private string _backupDir = "";
    private BackupManifest _manifest = new();

    public SaveTransaction(SaveScope scope, SavePaths paths, ICloudProvider? cloud,
        long requestedTotal, long requestedLastPlayed, bool steamRunning)
    {
        _scope = scope;
        _paths = paths;
        _cloud = cloud;
        _steamRunning = steamRunning;
        _requestedTotal = requestedTotal;
        _requestedLastPlayed = requestedLastPlayed;

        _report.Copies.Add(new CopyStatus { Target = CopyTarget.Vdf });
        _report.Copies.Add(new CopyStatus { Target = CopyTarget.LocalJson });
        _report.Copies.Add(new CopyStatus
        {
            Target = CopyTarget.CloudBlob,
            State = cloud != null && cloud.CanWrite
                ? CopyState.NotAttempted
                : CopyState.SkippedReadOnlyCloud,
        });
    }

    public async Task<SaveReport> RunAsync()
    {
        if (!await PreFlightAsync().ConfigureAwait(false))
        {
            _report.Outcome = SaveOutcome.NotSaved;
            return Finalize();
        }

        if (!CreateBackup())
        {
            _outcomeSet = SaveOutcome.BackupFailed;
            return Finalize();
        }
        SyncManifest("backed_up", "backed_up", updateStates: false);

        if (!VdfPhase()) return Finalize();
        if (!LocalPhase()) return Finalize();
        if (CloudCopyActive() && !await CloudPhaseAsync().ConfigureAwait(false))
            return Finalize();

        _outcomeSet ??= SaveOutcome.SavedVerified;
        SyncManifest("complete", _outcomeSet == SaveOutcome.SavedUnverified
            ? "saved_unverified"
            : "saved_verified", updateStates: true);
        return Finalize();
    }

    private SaveReport Finalize()
    {
        _report.Outcome = _outcomeSet ?? SaveOutcome.NotSaved;
        if (!string.IsNullOrEmpty(_backupDir))
        {
            _report.BackupDir = _backupDir;
            _report.Messages.Add($"Backups were written to \"{_backupDir}\".");
        }
        return _report;
    }

    // ---------------- Pre-flight (no target mutations) ----------------

    private async Task<bool> PreFlightAsync()
    {
        if (_steamRunning)
        {
            _report.Messages.Add(
                "Steam is running. Close Steam before editing playtime, otherwise Steam may overwrite your changes. No files were changed.");
            _outcomeSet = SaveOutcome.NotSaved;
            return false;
        }

        // Local stats JSON: distinguish absent from malformed.
        if (File.Exists(_paths.LocalStatsPath))
        {
            try
            {
                _localBeforeRaw = File.ReadAllBytes(_paths.LocalStatsPath);
                _localBeforeParsed = JsonNode.Parse(Encoding.UTF8.GetString(_localBeforeRaw)) as JsonObject;
            }
            catch (Exception ex)
            {
                NotSaved($"Local stats file could not be read: {ex.Message}. No files were changed.");
                return false;
            }
            if (_localBeforeParsed == null)
            {
                NotSaved("Local stats file is not valid JSON. No files were changed.");
                return false;
            }
        }

        // localconfig.vdf: file absent is a skip, not a failure; block absent is fatal.
        if (File.Exists(_paths.VdfPath))
        {
            try
            {
                _vdfBeforeRaw = File.ReadAllBytes(_paths.VdfPath);
            }
            catch (Exception ex)
            {
                NotSaved($"localconfig.vdf could not be read: {ex.Message}. No files were changed.");
                return false;
            }
            if (new FileInfo(_paths.VdfPath).IsReadOnly)
            {
                NotSaved("localconfig.vdf is read-only. No files were changed.");
                return false;
            }
            _vdfExists = true;
            if (!VdfEditor.AppBlockExists(_paths.VdfPath, _scope.AppId.ToString()))
            {
                NotSaved($"Could not find app {_scope.AppId} block in localconfig.vdf. No files were changed.");
                return false;
            }
        }

        // Cloud blob fresh GET: exact raw bytes + ETag for conditional writes.
        if (CloudCopyActive())
        {
            try
            {
                var fetch = await _cloud!.GetAccountStatsAsync(_scope.AccountId).ConfigureAwait(false);
                _cloudExistedBefore = fetch.Exists;
                _cloudBeforeRaw = fetch.Raw;
                _cloudBeforeETag = fetch.ETag;
                if (fetch.Exists)
                {
                    if (fetch.Parsed == null)
                    {
                        NotSaved("Cloud stats blob is not valid JSON. No files were changed.");
                        return false;
                    }
                    _cloudBeforeParsed = fetch.Parsed;
                }
            }
            catch (Exception ex)
            {
                NotSaved($"Cloud read failed: {ex.Message}. No files were changed.");
                return false;
            }
        }

        BuildTarget();
        return true;
    }

    /// <summary>The target model starts from what the user edited in the page
    /// (per-device cells + total + last played), never from the on-disk file.</summary>
    private void BuildTarget()
    {
        var m = new PlaytimeModel();
        foreach (var d in _scope.Source.PerDevice)
        {
            var nd = new DevicePlaytime
            {
                Name = d.Name,
                Windows = d.Windows,
                Mac = d.Mac,
                Linux = d.Linux,
            };
            nd._daysPropertyPresent = d._daysPropertyPresent;
            foreach (var (day, minutes) in d.Days) nd.Days[day] = minutes;
            m.PerDevice.Add(nd);
        }
        m.AdjustBucketsToTotal(Math.Max(0, _requestedTotal));
        m.LastPlayed = Math.Max(0, _requestedLastPlayed);
        m.Minutes2Weeks = m.CalculateCloudRedirectTwoWeekTotal();
        _target = m;
    }

    private void NotSaved(string message)
    {
        _report.Messages.Add(message);
        _outcomeSet = SaveOutcome.NotSaved;
    }

    private bool CloudCopyActive()
        => _cloud != null && _cloud.CanWrite;

    // ---------------- Backup (mandatory, before any mutation) ----------------

    private bool CreateBackup()
    {
        try
        {
            _backupDir = CreateUniqueBackupDir();

            var targetJson = new JsonObject
            {
                ["total_minutes"] = _target.Total,
                ["minutes_2weeks"] = _target.Minutes2Weeks,
                ["last_played"] = _target.LastPlayed,
            };
            WriteBackupText("target.json", targetJson.ToJsonString(Indented));

            _manifest.Local = MakeManifestCopy(_localBeforeRaw != null, _localBeforeRaw, null);
            if (_localBeforeRaw != null)
                WriteBackupFile("local_app.json", _localBeforeRaw);

            _manifest.Cloud = MakeManifestCopy(_cloudExistedBefore, _cloudBeforeRaw, _cloudBeforeETag);
            if (_cloudExistedBefore && _cloudBeforeRaw != null)
                WriteBackupFile("cloud_stats.json", _cloudBeforeRaw);

            _manifest.Vdf = MakeManifestCopy(_vdfExists, _vdfBeforeRaw, null);
            if (_vdfExists && _vdfBeforeRaw != null)
                WriteBackupFile("localconfig.vdf", _vdfBeforeRaw);

            _manifest.SchemaVersion = 1;
            _manifest.CreatedAtUtc = DateTime.UtcNow.ToString("o");
            _manifest.AccountId = _scope.AccountId;
            _manifest.AppId = _scope.AppId;
            _manifest.ProviderName = _cloud?.DisplayName;
            _manifest.Target = new ManifestTarget
            {
                TotalMinutes = _target.Total,
                TwoWeekMinutes = _target.Minutes2Weeks,
                LastPlayed = _target.LastPlayed,
            };

            WriteManifestFile();
            return true;
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"Backup failed: {ex.Message}. No files were changed.");
            if (!TryCleanupBackupDir())
                _report.Messages.Add("The incomplete backup directory could not be removed - see backups.");
            return false;
        }
    }

    private static ManifestCopy MakeManifestCopy(bool existed, byte[]? raw, string? etag)
        => new()
        {
            ExistedBefore = existed,
            Sha256 = raw == null ? null : Sha256Hex(raw),
            ETag = etag,
            State = existed ? "backed_up" : "absent",
        };

    private string CreateUniqueBackupDir()
    {
        var baseDir = _paths.BackupRoot;
        Directory.CreateDirectory(baseDir);
        string stem = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{_scope.AccountId}-{_scope.AppId}";
        string dir = Path.Combine(baseDir, stem);
        int n = 1;
        while (Directory.Exists(dir) || File.Exists(dir))
            dir = Path.Combine(baseDir, $"{stem}-{n++}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal virtual void WriteBackupFile(string fileName, byte[] data)
        => FileUtils.AtomicWriteAllBytes(Path.Combine(_backupDir, fileName), data);

    internal virtual void WriteBackupText(string fileName, string content)
        => FileUtils.AtomicWriteAllText(Path.Combine(_backupDir, fileName), content);

    private bool TryCleanupBackupDir()
    {
        if (string.IsNullOrEmpty(_backupDir)) return true;
        try
        {
            Directory.Delete(_backupDir, recursive: true);
            _backupDir = "";
            return true;
        }
        catch { return false; }
    }

    private void WriteManifestFile()
    {
        WriteBackupText("manifest.json", _manifest.ToJson());
    }

    private void SyncManifest(string phase, string result, bool updateStates)
    {
        if (string.IsNullOrEmpty(_backupDir)) return;
        _manifest.Phase = phase;
        _manifest.Result = result;
        if (updateStates)
        {
            _manifest.Local!.State = ManifestState(StatusOf(CopyTarget.LocalJson).State);
            _manifest.Cloud!.State = ManifestState(StatusOf(CopyTarget.CloudBlob).State);
            _manifest.Vdf!.State = ManifestState(StatusOf(CopyTarget.Vdf).State);
        }
        try { WriteManifestFile(); } catch { }
    }

    private static string ManifestState(CopyState s) => s switch
    {
        CopyState.SkippedAbsentVdf => "skipped",
        CopyState.SkippedReadOnlyCloud => "skipped",
        CopyState.WriteFailed => "write_failed",
        CopyState.Written => "written",
        CopyState.Unverified => "unverified",
        CopyState.Verified => "verified",
        CopyState.Divergent => "divergent",
        CopyState.RolledBack => "rolled_back",
        CopyState.RestoreFailed => "restore_failed",
        _ => "not_attempted",
    };

    // ---------------- Step 1: VDF ----------------

    private bool VdfPhase()
    {
        if (!_vdfExists)
        {
            SetState(CopyTarget.Vdf, CopyState.SkippedAbsentVdf);
            SyncManifest("writing_vdf", "skipped", updateStates: true);
            return true;
        }

        try
        {
            VdfEditor.PatchAppPlaytime(_paths.VdfPath, _scope.AppId.ToString(),
                _target.Total, _target.Minutes2Weeks, _target.LastPlayed);
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"localconfig.vdf write failed: {ex.Message}.");
            SetState(CopyTarget.Vdf, CopyState.WriteFailed);
            return HandleVdfAbort();
        }

        bool ok;
        try
        {
            string beforeText = Encoding.UTF8.GetString(_vdfBeforeRaw!);
            string afterText = File.ReadAllText(_paths.VdfPath);
            ok = VdfEditor.UnrelatedContentUnchanged(beforeText, afterText, _scope.AppId.ToString());
            bool readBackOk = VdfEditor.TryGetAppPlaytimeValues(_paths.VdfPath, _scope.AppId.ToString(),
                out var p, out var p2, out var lp);
            ok = ok && readBackOk &&
                p == _target.Total && lp == _target.LastPlayed &&
                (p2 == 0 || p2 == _target.Minutes2Weeks);
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"localconfig.vdf verification failed: {ex.Message}.");
            ok = false;
        }

        if (!ok)
        {
            SetState(CopyTarget.Vdf, CopyState.Divergent);
            return HandleVdfAbort();
        }

        SetState(CopyTarget.Vdf, CopyState.Verified);
        SyncManifest("writing_vdf", "verified", updateStates: true);
        return true;
    }

    private bool HandleVdfAbort()
    {
        if (TryRestoreVdf())
        {
            SetState(CopyTarget.Vdf, CopyState.RolledBack);
            _report.Messages.Add("localconfig.vdf was restored to its previous content.");
            ReturnWithOutcome(SaveOutcome.PartialRolledBack, "vdf_rolled_back", "rolled_back");
        }
        else
        {
            SetState(CopyTarget.Vdf, CopyState.RestoreFailed);
            _report.Messages.Add("Failed to restore localconfig.vdf - see backups.");
            ReturnWithOutcome(SaveOutcome.RollbackFailed, "vdf_rolled_back", "restore_failed");
        }
        return false;
    }

    // ---------------- Step 2: local stats JSON ----------------

    private bool LocalPhase()
    {
        var root = _localBeforeParsed ?? new JsonObject();
        root["playtime"] = _target.ToJson();
        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"Local stats serialization failed: {ex.Message}.");
            SetState(CopyTarget.LocalJson, CopyState.WriteFailed);
            return RollbackEarlierCopies(SaveOutcome.PartialRolledBack, "local_rolled_back");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.LocalStatsPath) ?? "");
            FileUtils.AtomicWriteAllBytes(_paths.LocalStatsPath, bytes);
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"Local stats write failed: {ex.Message}.");
            SetState(CopyTarget.LocalJson, CopyState.WriteFailed);
            return RollbackEarlierCopies(SaveOutcome.PartialRolledBack, "local_rolled_back");
        }

        bool ok;
        try
        {
            var writtenRoot = JsonNode.Parse(File.ReadAllText(_paths.LocalStatsPath)) as JsonObject;
            ok = ParsePlaytime(writtenRoot).SemanticallyEquals(_target);
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            _report.Messages.Add("Local stats JSON verification failed after write.");
            SetState(CopyTarget.LocalJson, CopyState.Divergent);
            return RollbackEarlierCopies(SaveOutcome.PartialRolledBack, "local_rolled_back");
        }

        SetState(CopyTarget.LocalJson, CopyState.Verified);
        SyncManifest("writing_local", "verified", updateStates: true);
        return true;
    }

    /// <summary>Restores everything we wrote before the failing copy, so the only
    /// copies left touched are the pre-save state.</summary>
    private bool RollbackEarlierCopies(SaveOutcome outcome, string manifestResult)
    {
        bool allRestored = true;

        if (StatusOf(CopyTarget.LocalJson).State is CopyState.Verified or CopyState.Written
            or CopyState.Divergent or CopyState.WriteFailed)
        {
            if (TryRestoreLocal()) SetState(CopyTarget.LocalJson, CopyState.RolledBack);
            else { allRestored = false; SetState(CopyTarget.LocalJson, CopyState.RestoreFailed); }
        }

        if (StatusOf(CopyTarget.Vdf).State is CopyState.Verified or CopyState.Written or CopyState.Divergent)
        {
            if (TryRestoreVdf()) SetState(CopyTarget.Vdf, CopyState.RolledBack);
            else { allRestored = false; SetState(CopyTarget.Vdf, CopyState.RestoreFailed); }
        }

        if (allRestored)
            _outcomeSet = outcome;
        else
        {
            _outcomeSet = SaveOutcome.RollbackFailed;
            _report.Messages.Add("One or more local copies could not be restored - see backups.");
        }
        SyncManifest("rolled_back", allRestored ? manifestResult : "restore_failed", updateStates: true);
        return false;
    }

    private bool TryRestoreVdf()
    {
        if (_vdfBeforeRaw == null) return false;
        try { FileUtils.AtomicWriteAllBytes(_paths.VdfPath, _vdfBeforeRaw); return true; }
        catch { return false; }
    }

    private bool TryRestoreLocal()
    {
        try
        {
            if (_localBeforeRaw != null)
            {
                FileUtils.AtomicWriteAllBytes(_paths.LocalStatsPath, _localBeforeRaw);
                return true;
            }
            if (File.Exists(_paths.LocalStatsPath)) File.Delete(_paths.LocalStatsPath);
            return true;
        }
        catch { return false; }
    }

    // ---------------- Step 3: cloud blob ----------------

    private async Task<bool> CloudPhaseAsync()
    {
        SyncManifest("writing_cloud", "writing", updateStates: true);

        var blob = _cloudBeforeParsed ?? new JsonObject();
        var appKey = _scope.AppId.ToString();
        var appObj = blob[appKey] as JsonObject;
        if (appObj == null)
        {
            appObj = new JsonObject();
            blob[appKey] = appObj;
        }
        appObj["playtime"] = _target.ToJson();
        byte[] outBytes;
        try
        {
            outBytes = Encoding.UTF8.GetBytes(blob.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"Cloud serialization failed: {ex.Message}.");
            SetState(CopyTarget.CloudBlob, CopyState.WriteFailed);
            return RollbackEarlierCopies(SaveOutcome.PartialRolledBack, "cloud_rolled_back");
        }

        bool created = !_cloudExistedBefore;
        try
        {
            if (created)
                await _cloud!.PutAccountStatsAsync(_scope.AccountId, outBytes, null, ifNoneMatchCreate: true).ConfigureAwait(false);
            else
                await _cloud!.PutAccountStatsAsync(_scope.AccountId, outBytes, _cloudBeforeETag, ifNoneMatchCreate: false).ConfigureAwait(false);
        }
        catch (CloudChangedConcurrentlyException)
        {
            _report.Messages.Add("Cloud write was rejected: the blob changed concurrently (preconditions not met).");
            return await HandleCloudAbortAsync(concurrent: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _report.Messages.Add($"Cloud write failed: {ex.Message}.");
            return await HandleCloudAbortAsync(concurrent: false).ConfigureAwait(false);
        }

        return await VerifyCloudAfterPutAsync(created).ConfigureAwait(false);
    }

    private async Task<bool> VerifyCloudAfterPutAsync(bool created)
    {
        var fetch = await SafeFetchCloudAsync().ConfigureAwait(false);
        if (fetch == null)
        {
            SetState(CopyTarget.CloudBlob, CopyState.Unverified);
            _outcomeSet = SaveOutcome.SavedUnverified;
            _report.Messages.Add("Cloud write returned OK but the result could not be verified.");
            SyncManifest("writing_cloud", "unverified", updateStates: true);
            return false;
        }
        if (ContainsTarget(fetch))
        {
            SetState(CopyTarget.CloudBlob, CopyState.Verified);
            SyncManifest("writing_cloud", "verified", updateStates: true);
            return true;
        }
        return await ClassifyAbortedCloudAsync(fetch, created, concurrent: false).ConfigureAwait(false);
    }

    private async Task<bool> HandleCloudAbortAsync(bool concurrent)
    {
        var fetch = await SafeFetchCloudAsync().ConfigureAwait(false);
        if (fetch == null)
        {
            SetState(CopyTarget.CloudBlob, CopyState.Unverified);
            _outcomeSet = SaveOutcome.CloudStateUnknown;
            _report.Messages.Add("Cloud state could not be determined after a failed write.");
            SyncManifest("writing_cloud", "unknown", updateStates: true);
            RollbackEarlierCopiesAfterCloudUnknown();
            return false;
        }
        return await ClassifyAbortedCloudAsync(fetch, !_cloudExistedBefore, concurrent).ConfigureAwait(false);
    }

    private void RollbackEarlierCopiesAfterCloudUnknown()
    {
        bool allRestored = true;
        var li = StatusOf(CopyTarget.LocalJson);
        if (li.State is CopyState.Verified or CopyState.Written)
        {
            if (TryRestoreLocal()) li.State = CopyState.RolledBack;
            else { allRestored = false; li.State = CopyState.RestoreFailed; }
        }
        var vi = StatusOf(CopyTarget.Vdf);
        if (vi.State is CopyState.Verified or CopyState.Written)
        {
            if (TryRestoreVdf()) vi.State = CopyState.RolledBack;
            else { allRestored = false; vi.State = CopyState.RestoreFailed; }
        }
        if (!allRestored)
        {
            _outcomeSet = SaveOutcome.RollbackFailed;
            _report.Messages.Add("One or more local copies could not be restored - see backups.");
        }
        SyncManifest("rolled_back", allRestored ? "rolled_back" : "restore_failed", updateStates: true);
    }

    private async Task<bool> ClassifyAbortedCloudAsync(AccountStatsFetch fetch, bool created, bool concurrent)
    {
        if (ContainsTarget(fetch))
        {
            SetState(CopyTarget.CloudBlob, CopyState.Unverified);
            _outcomeSet = SaveOutcome.SavedUnverified;
            _report.Messages.Add(
                "The saved value is in the cloud (a concurrent write may have raced this one). No rollback of the cloud was performed; earlier copies match the target.");
            SyncManifest("writing_cloud", "unverified", updateStates: true);
            return true;
        }

        if (CloudUnchanged(fetch, created))
        {
            SetState(CopyTarget.CloudBlob, CopyState.WriteFailed);
            _report.Messages.Add("Cloud write did not land; the previous blob (or its absence) is preserved. Rolling back earlier copies.");
            return RollbackEarlierCopies(
                concurrent ? SaveOutcome.CloudChangedConcurrently : SaveOutcome.CloudWriteFailed,
                concurrent ? "cloud_concurrent" : "cloud_write_failed");
        }

        SetState(CopyTarget.CloudBlob, CopyState.Divergent);
        _report.Messages.Add(
            "Cloud now holds a different value produced by a concurrent writer; it was preserved. Earlier copies were rolled back.");
        return RollbackEarlierCopies(SaveOutcome.CloudDiverged, "cloud_diverged");
    }

    private bool ContainsTarget(AccountStatsFetch fetch)
    {
        if (!fetch.Exists || fetch.Parsed == null) return false;
        if (!fetch.Parsed.TryGetPropertyValue(_scope.AppId.ToString(), out var appEl) ||
            appEl is not JsonObject appObj) return false;
        return ParsePlaytime(appObj).SemanticallyEquals(_target);
    }

    private bool CloudUnchanged(AccountStatsFetch fetch, bool created)
    {
        if (created) return !fetch.Exists;
        if (!fetch.Exists || fetch.Raw == null || _cloudBeforeRaw == null) return false;
        return fetch.Raw.AsSpan().SequenceEqual(_cloudBeforeRaw);
    }

    private async Task<AccountStatsFetch?> SafeFetchCloudAsync()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await _cloud!.GetAccountStatsAsync(_scope.AccountId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                    _report.Messages.Add($"Cloud state re-read failed: {ex.Message}.");
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        return null;
    }

    // ---------------- Plumbing ----------------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private void ReturnWithOutcome(SaveOutcome o, string phase, string result)
    {
        _outcomeSet = o;
        SyncManifest(phase, result, updateStates: true);
    }

    private CopyStatus StatusOf(CopyTarget t)
        => _report.Copies.First(c => c.Target == t);

    private void SetState(CopyTarget t, CopyState s)
        => StatusOf(t).State = s;

    private static PlaytimeModel ParsePlaytime(JsonObject? container)
    {
        if (container?.TryGetPropertyValue("playtime", out var pt) == true && pt is JsonObject pto)
            return PlaytimeModel.Parse(pto);
        return PlaytimeModel.Parse(null);
    }

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}