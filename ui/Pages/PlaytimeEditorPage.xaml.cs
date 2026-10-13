using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Resources;
using CloudReEdit.Services;
using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Pages;

public partial class PlaytimeEditorPage : Page
{
    private EditorContext? _ctx;
    private ICloudProvider? _provider;
    private bool _useHours;
    private bool _syncingTotal;
    private bool _isSaving;
    private DateTime _existingLocal = DateTime.Now;
    private EditorSnapshot? _baseline;
    private bool _dirty;
    private bool _isLoading;
    private bool _navPromptActive;
    private bool _bypassNav;
    private bool _closeApproved;
    private bool _navHookAttached;
    private bool _closeHookAttached;
    private bool _lastPlayedAttached;
    private readonly DependencyPropertyDescriptor _lastPlayedChanged;

    public PlaytimeEditorPage()
    {
        InitializeComponent();

        _lastPlayedChanged = DependencyPropertyDescriptor.FromProperty(
            Wpf.Ui.Controls.CalendarDatePicker.DateProperty,
            typeof(Wpf.Ui.Controls.CalendarDatePicker));

        Loaded += async (_, _) =>
        {
            AttachHooks();
            RefreshBackupsInfo();
            RefreshLogsInfo();
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            DetachHooks();
            (_provider as IDisposable)?.Dispose();
        };
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _dirty = false;
        _closeApproved = false;
        try
        {
            ResultBar.IsOpen = false;
            WarningBar.IsOpen = false;

            if (string.IsNullOrEmpty(AppState.AccountId) || AppState.AppId == null)
            {
                GameTitleText.Text = "No game selected.";
                GameSubText.Text = "Pick a game on the Games page first.";
                SaveButton.IsEnabled = false;
                return;
            }

            var accountId = AppState.AccountId;
            var appId = AppState.AppId.Value;

            _ctx = await PlaytimeService.LoadAsync(accountId, appId);
            (_provider as IDisposable)?.Dispose();
            _provider = BuildProvider();

            GameTitleText.Text = AppState.GameName ?? $"App {appId}";
            GameSubText.Text = $"Account {accountId} - App {appId}";

            // Cloud status
            string cloudInfo;
            if (_ctx.CloudProviderName != null)
            {
                cloudInfo = _provider != null && _provider.CanWrite
                    ? $"Cloud: {_ctx.CloudProviderName} (writes supported)"
                    : $"Cloud: {_ctx.CloudProviderName} (read-only in CloudReEdit)";
            }
            else
            {
                cloudInfo = "No CloudRedirect provider configured - saving local + VDF copies only.";
            }
            _ctx.Warnings.Add(cloudInfo);
            if (!string.IsNullOrEmpty(_ctx.CloudError))
                _ctx.Warnings.Add($"Cloud error: {_ctx.CloudError}");

            if (_ctx.Warnings.Count > 0)
            {
                WarningBar.Title = "Information";
                WarningBar.Message = string.Join(Environment.NewLine, _ctx.Warnings);
                WarningBar.IsOpen = true;
            }

            // Total + devices
            DevicesGrid.ItemsSource = _ctx.Local.PerDevice;
            _syncingTotal = true;
            TotalBox.Text = _ctx.Local.Total.ToString();
            _syncingTotal = false;
            RefreshTotals();

            // Last played
            _existingLocal = _ctx.Local.LastPlayed > 0
                ? DateTimeOffset.FromUnixTimeSeconds(_ctx.Local.LastPlayed).LocalDateTime
                : DateTime.Now;
            LastPlayedPicker.Date = _existingLocal.Date;

            SaveButton.IsEnabled = true;

            // Baseline for unsaved-changes tracking: exactly the values a save
            // would write for the untouched editor, so the page starts clean and
            // only becomes dirty when the user actually changes something.
            _baseline = new EditorSnapshot(_ctx.Local, ParseTotalMinutes(), GetLastPlayedUnix());
            _dirty = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private ICloudProvider? BuildProvider()
    {
        var cfg = SteamDetector.ReadConfig();
        if (cfg == null) return null;
        var (provider, _) = CloudProviderFactory.Create(cfg);
        return provider;
    }

    private void RefreshTotals()
    {
        if (_ctx == null) return;
        long total = _ctx.Local.Total;
        DeviceSumText.Text =
            $"Sum of devices: {total} min ({total / 60.0:0.##} h)";

        _syncingTotal = true;
        TotalBox.Text = _useHours
            ? (total / 60.0).ToString("0.###", CultureInfo.InvariantCulture)
            : total.ToString();
        _syncingTotal = false;
    }

    private long ParseTotalMinutes()
    {
        if (_ctx == null) return 0;
        var text = TotalBox.Text?.Trim() ?? "0";
        if (_useHours)
        {
            return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var hours)
                ? (long)Math.Round(hours * 60)
                : _ctx.Local.Total;
        }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins)
            ? Math.Max(0, mins)
            : _ctx.Local.Total;
    }

    private void TotalBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // When the user types, show the balance they'd get on save.
        if (_syncingTotal || _ctx == null) return;
        long total = ParseTotalMinutes();
        DeviceSumText.Text =
            $"Sum of devices: {_ctx.Local.Total} min - target {total} min (balance {(total - _ctx.Local.Total):+#;-#;0})";
        UpdateDirty();
    }

    private void HoursSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        _useHours = HoursSwitch.IsChecked == true;
        UnitText.Text = _useHours ? "hours" : "minutes";
        RefreshTotals();
        UpdateDirty();
    }

    private void DevicesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_isSaving) return;
        if (e.EditAction == DataGridEditAction.Commit)
            RefreshTotals();
        UpdateDirty();
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx == null || _isSaving) return;
        var name = NewDeviceBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            WarningBar.Title = "Device name required";
            WarningBar.Message = "Type a device name before adding.";
            WarningBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            WarningBar.IsOpen = true;
            return;
        }

        if (_ctx.Local.PerDevice.Any(d => d.Name == name))
        {
            WarningBar.Title = "Duplicate device";
            WarningBar.Message = $"A device named '{name}' already exists.";
            WarningBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            WarningBar.IsOpen = true;
            return;
        }

        _ctx.Local.PerDevice.Add(new DevicePlaytime { Name = name });
        NewDeviceBox.Text = "";
        RefreshTotals();
        UpdateDirty();
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx == null || _isSaving || DevicesGrid.SelectedItem is not DevicePlaytime device) return;
        _ctx.Local.PerDevice.Remove(device);
        RefreshTotals();
        UpdateDirty();
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        LastPlayedPicker.Date = DateTime.Now;
    }

    private long GetLastPlayedUnix()
    {
        var date = LastPlayedPicker.Date ?? _existingLocal.Date;
        var local = DateTime.SpecifyKind(date.Date + _existingLocal.TimeOfDay, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeSeconds();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
        => await SaveFlowAsync();

    private async Task<bool> SaveFlowAsync()
    {
        if (_ctx == null || _isSaving) return false;

        // Steam has to be out of the way before the pre-flight reads a single
        // byte, and whether it comes back up afterwards is a saved preference.
        var steam = await PrepareSteamAsync();
        if (steam.Abort) return false;

        // Lock the screen while saving. The captured snapshot is the exact state
        // the user sees right now: any pending grid cell edit is committed first
        // so the device cells, the total and the snapshot all agree.
        CommitPendingCellEdit();
        long total = ParseTotalMinutes();
        long lastPlayed = GetLastPlayedUnix();
        var snapshot = _ctx.Local.Clone();

        // Declared outside the try so the finally can tell whether Steam should
        // go back up, whichever path the save ends up taking.
        bool committed = false;
        SetSavingState(true);
        try
        {
            var report = await PlaytimeService.SaveAsync(_ctx, snapshot, total, lastPlayed);
            // A committed save re-loads the canonical state, which also resets the
            // dirty baseline. A failed save never reloads and leaves the user's
            // edits (and therefore the dirty state) in place.
            committed = report.Outcome is SaveOutcome.SavedVerified or SaveOutcome.SavedUnverified;
            if (committed)
                await LoadAsync();
            ApplyReport(report, total);
            return committed;
        }
        catch (Exception ex)
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = "Save failed";
            ResultBar.Message = $"An unexpected error occurred while saving. Your edits remain on screen.\n{ex.Message}";
            ResultBar.IsOpen = true;
            return false;
        }
        finally
        {
            FinishSteam(steam, committed);
            SetSavingState(false);
        }
    }

    /// <summary>Whether a save should go ahead once the Steam question is settled.</summary>
    /// <param name="Abort">Stop before anything is read or written. True for both
    /// a user cancel and a Steam that refused to close.</param>
    /// <param name="Closed">We actually took Steam down, so it is on us to say
    /// where it went if it does not come back.</param>
    /// <param name="ReopenAfter">Put Steam back up once the save succeeds.</param>
    private sealed record SteamPrep(bool Abort, bool Closed, bool ReopenAfter);

    /// <summary>Resolves what happens to Steam around this save: asks when the
    /// preference is <see cref="SaveSteamBehaviour.Ask"/>, closes Steam, and
    /// reports whether to put it back afterwards. Runs before the pre-flight,
    /// which requires Steam closed, so a cancellation here writes nothing at all.</summary>
    private async Task<SteamPrep> PrepareSteamAsync()
    {
        var behaviour = AppSettings.LoadSaveSteamBehaviour();

        if (!SteamDetector.IsSteamRunning())
        {
            // Nothing to close. "Always open" still means open when we are done,
            // even though Steam played no part in getting the save done.
            return new SteamPrep(
                Abort: false,
                Closed: false,
                ReopenAfter: behaviour == SaveSteamBehaviour.AlwaysOpen);
        }

        bool reopen;
        if (behaviour == SaveSteamBehaviour.Ask)
        {
            var choice = await Dialog.Choice3WithAlwaysAsync(
                "Steam must be closed",
                "Steam is running, so it has to be closed before saving - otherwise Steam may overwrite your changes. What should happen once the save is finished?",
                S.Get("Dialog_CloseReopen"),
                S.Get("Dialog_CloseOnly"),
                S.Get("Dialog_Cancel"),
                S.Get("Dialog_AlwaysDoThis"));

            if (choice.Result == Wpf.Ui.Controls.MessageBoxResult.None)
            {
                AppLog.Info("steam", "playtime save cancelled from the steam dialog, nothing was written");
                return new SteamPrep(Abort: true, Closed: false, ReopenAfter: false);
            }

            reopen = choice.Result == Wpf.Ui.Controls.MessageBoxResult.Primary;

            if (choice.Always)
            {
                var chosen = reopen ? SaveSteamBehaviour.AlwaysOpen : SaveSteamBehaviour.NeverOpen;
                AppSettings.SetSaveSteamBehaviour(chosen);
                AppLog.Info("steam",
                    $"user asked to always {AppSettings.SaveSteamBehaviourToken(chosen)}, preference saved");
            }
        }
        else
        {
            reopen = behaviour switch
            {
                // Steam is up right now, so its most recent state was "running".
                SaveSteamBehaviour.LastState => true,
                SaveSteamBehaviour.AlwaysOpen => true,
                _ => false,
            };
        }

        if (!await SteamController.CloseSteamAsync())
        {
            AppLog.Warn("steam", "steam did not close in time, aborting the save before any read or write");
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = "Steam is still running";
            ResultBar.Message = "Steam did not close in time, so nothing was saved. Close Steam yourself and try again.";
            ResultBar.IsOpen = true;
            return new SteamPrep(Abort: true, Closed: false, ReopenAfter: false);
        }

        return new SteamPrep(Abort: false, Closed: true, ReopenAfter: reopen);
    }

    /// <summary>Restarts Steam after a save we took it down for. A failed save
    /// deliberately leaves it closed rather than reopening over files that may be
    /// half-written - so the user is told instead of left guessing.</summary>
    private void FinishSteam(SteamPrep prep, bool committed)
    {
        if (prep.ReopenAfter && committed)
        {
            SteamController.StartSteam();
            return;
        }

        if (prep.Closed && !committed && ResultBar.IsOpen)
            ResultBar.Message = $"{ResultBar.Message}\nSteam was closed for the save and has been left closed.";
    }

    // Commits the value being typed into a data-grid cell so the model, the
    // on-screen total and the save snapshot all reflect what the user sees.
    private void CommitPendingCellEdit()
    {
        DevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void SetSavingState(bool saving)
    {
        _isSaving = saving;
        SaveButtonText.Text = saving ? "Saving…" : "Save playtime";
        SavingRing.IsIndeterminate = saving;
        SavingRing.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        SetEditorEnabled(!saving);
    }

    private void SetEditorEnabled(bool enabled)
    {
        TotalBox.IsEnabled = enabled;
        HoursSwitch.IsEnabled = enabled;
        DevicesGrid.IsEnabled = enabled;
        NewDeviceBox.IsEnabled = enabled;
        AddDeviceButton.IsEnabled = enabled;
        RemoveDeviceButton.IsEnabled = enabled;
        LastPlayedPicker.IsEnabled = enabled;
        NowButton.IsEnabled = enabled;
        ReloadButton.IsEnabled = enabled;
        BackButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private void RefreshBackupsInfo()
    {
        var stats = BackupRetention.GetStats(BackupRetention.DefaultRoot());
        if (stats.Count == 0)
        {
            BackupsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        string noun = stats.Count == 1 ? "folder" : "folders";
        BackupsText.Text =
            $"Backups: {stats.Root} — {stats.Count} {noun}, {BackupRetention.FormatSize(stats.TotalBytes)}";
        BackupsPanel.Visibility = Visibility.Visible;
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        var root = BackupRetention.DefaultRoot();
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            AppLog.Exception("ui", $"could not create backups folder \"{root}\"", ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = root,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Exception("ui", $"could not open backups folder \"{root}\"", ex);
        }
    }

    private void RefreshLogsInfo()
    {
        var path = AppLog.CurrentFilePath;

        if (string.IsNullOrEmpty(path))
        {
            LogsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        long size = 0;
        try
        {
            if (File.Exists(path))
                size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            AppLog.Debug("ui", $"log size read failed for \"{path}\": {ex.GetType().Name}");
        }

        LogsText.Text = $"Log: {path} ({BackupRetention.FormatSize(size)})";
        LogsPanel.Visibility = Visibility.Visible;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var directory = AppLog.LogDirectory;
        if (string.IsNullOrEmpty(directory)) return;

        try { Directory.CreateDirectory(directory); }
        catch (Exception ex)
        {
            AppLog.Exception("ui", $"could not create logs folder \"{directory}\"", ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Exception("ui", $"could not open logs folder \"{directory}\"", ex);
        }
    }

    private void ApplyReport(SaveReport report, long total)
    {
        string backupNote = string.IsNullOrEmpty(report.BackupDir)
            ? ""
            : $"\nBackups: {report.BackupDir}";
        string details = report.Messages.Count > 0
            ? string.Join(" ", report.Messages)
            : "";
        if (!string.IsNullOrEmpty(details)) details += "\n";
        details += backupNote;

        var verified = report.Copies
            .Where(c => c.State == CopyState.Verified)
            .Select(c => c.Target switch
            {
                CopyTarget.Vdf => "localconfig.vdf",
                CopyTarget.LocalJson => "local stats JSON",
                _ => $"cloud ({_ctx?.CloudProviderName ?? "cloud"})",
            })
            .ToList();

        string written = verified.Count > 0
            ? $"{string.Join(" + ", verified)} written."
            : "Nothing was written.";

        (ResultBar.Severity, ResultBar.Title, ResultBar.Message) = report.Outcome switch
        {
            SaveOutcome.SavedVerified => (Wpf.Ui.Controls.InfoBarSeverity.Success,
                $"Saved {total} min ({total / 60.0:0.##} h)",
                written),
            SaveOutcome.SavedUnverified => (Wpf.Ui.Controls.InfoBarSeverity.Warning,
                "Saved (cloud unverified)",
                string.Join("; ", report.Copies.Select(CopySummary)) + ". " + details),
            SaveOutcome.NotSaved => (Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Save aborted",
                string.IsNullOrEmpty(details) ? "No files were changed." : details),
            SaveOutcome.BackupFailed => (Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Backup failed",
                "No files were changed. " + details),
            SaveOutcome.PartialRolledBack => (Wpf.Ui.Controls.InfoBarSeverity.Warning,
                "Changes rolled back",
                details),
            SaveOutcome.RollbackFailed => (Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Rollback incomplete",
                details),
            SaveOutcome.CloudChangedConcurrently => (Wpf.Ui.Controls.InfoBarSeverity.Warning,
                "Cloud changed concurrently",
                details),
            SaveOutcome.CloudStateUnknown => (Wpf.Ui.Controls.InfoBarSeverity.Warning,
                "Cloud state unknown",
                details),
            SaveOutcome.CloudDiverged => (Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Cloud diverged",
                details),
            SaveOutcome.CloudWriteFailed => (Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Cloud write failed",
                details),
            _ => (Wpf.Ui.Controls.InfoBarSeverity.Error, "Save failed", details),
        };
        ResultBar.IsOpen = true;
        RefreshBackupsInfo();
        RefreshLogsInfo();
    }

    private string CopySummary(CopyStatus c)
    {
        string name = c.Target switch
        {
            CopyTarget.Vdf => "localconfig.vdf",
            CopyTarget.LocalJson => "local stats JSON",
            _ => $"cloud ({_ctx?.CloudProviderName ?? "cloud"})",
        };
        string verb = c.State switch
        {
            CopyState.Verified => "verified",
            CopyState.SkippedAbsentVdf => "absent (skipped)",
            CopyState.SkippedReadOnlyCloud => "read-only (skipped)",
            CopyState.WriteFailed => "write failed",
            CopyState.Unverified => "value written, result unverified",
            CopyState.Divergent => "diverged (concurrent change preserved)",
            CopyState.RolledBack => "rolled back",
            CopyState.RestoreFailed => "restore failed",
            CopyState.NotAttempted => "not attempted",
            CopyState.Written => "written",
            _ => c.State.ToString().ToLowerInvariant(),
        };
        return $"{name}: {verb}";
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;

        if (!HasUnsavedChanges)
        {
            await LoadAsync();
            return;
        }

        switch (await AskUnsavedAsync())
        {
            case UnsavedChoice.Save:
                await SaveFlowAsync();
                break;
            case UnsavedChoice.Discard:
                await LoadAsync();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(GamesPage));
    }

    public bool HasUnsavedChanges => _dirty;

    private void UpdateDirty()
    {
        if (_syncingTotal || _isLoading || _ctx == null || _baseline == null)
            return;
        _dirty = _baseline.DiffersFrom(_ctx.Local, ParseTotalMinutes(), GetLastPlayedUnix());
    }

    private void LastPlayed_Changed(object? sender, EventArgs e)
    {
        if (_isSaving) return;
        UpdateDirty();
    }

    private enum UnsavedChoice { Save, Discard, Cancel }

    private async Task<UnsavedChoice> AskUnsavedAsync()
    {
        var result = await Dialog.Choice3Async(
            "Unsaved changes",
            "You have unsaved playtime changes. What would you like to do?",
            S.Get("Dialog_Save"),
            S.Get("Dialog_Discard"),
            S.Get("Dialog_Cancel"));
        return result switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => UnsavedChoice.Save,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    private void AttachHooks()
    {
        if (Window.GetWindow(this) is not MainWindow win) return;
        if (!_lastPlayedAttached)
        {
            _lastPlayedChanged.AddValueChanged(LastPlayedPicker, LastPlayed_Changed);
            _lastPlayedAttached = true;
        }
        if (!_navHookAttached)
        {
            win.RootNavigation.Navigating += RootNavigation_Navigating;
            _navHookAttached = true;
        }
        if (!_closeHookAttached)
        {
            win.Closing += MainWindow_Closing;
            _closeHookAttached = true;
        }
    }

    private void DetachHooks()
    {
        if (Window.GetWindow(this) is MainWindow win)
        {
            if (_navHookAttached)
            {
                win.RootNavigation.Navigating -= RootNavigation_Navigating;
                _navHookAttached = false;
            }
            if (_closeHookAttached)
            {
                win.Closing -= MainWindow_Closing;
                _closeHookAttached = false;
            }
        }
        if (_lastPlayedAttached)
        {
            _lastPlayedChanged.RemoveValueChanged(LastPlayedPicker, LastPlayed_Changed);
            _lastPlayedAttached = false;
        }
    }

    private async void RootNavigation_Navigating(
        Wpf.Ui.Controls.NavigationView sender, Wpf.Ui.Controls.NavigatingCancelEventArgs e)
    {
        if (_bypassNav || _ctx == null || _isLoading || _baseline == null) return;
        if (e.Page is PlaytimeEditorPage) return;
        if (_isSaving || _navPromptActive)
        {
            e.Cancel = true;
            return;
        }
        if (!HasUnsavedChanges) return;

        e.Cancel = true;
        var target = e.Page.GetType();
        _navPromptActive = true;
        try
        {
            switch (await AskUnsavedAsync())
            {
                case UnsavedChoice.Save when await SaveFlowAsync():
                    NavigateAway(sender, target);
                    break;
                case UnsavedChoice.Save:
                    break;
                case UnsavedChoice.Discard:
                    NavigateAway(sender, target);
                    break;
            }
        }
        finally
        {
            _navPromptActive = false;
        }
    }

    private void NavigateAway(Wpf.Ui.Controls.NavigationView sender, Type target)
    {
        _bypassNav = true;
        try
        {
            sender.Navigate(target);
        }
        finally
        {
            _bypassNav = false;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        if (_ctx == null || _isLoading || _baseline == null) return;
        if (_navPromptActive)
        {
            e.Cancel = true;
            return;
        }
        if (_isSaving)
        {
            e.Cancel = true;
            await Dialog.ShowInfoAsync("Save in progress",
                "Please wait for the save to finish before closing.");
            return;
        }
        if (!HasUnsavedChanges) return;

        e.Cancel = true;
        switch (await AskUnsavedAsync())
        {
            case UnsavedChoice.Save when await SaveFlowAsync():
                RequestClose(sender);
                break;
            case UnsavedChoice.Save:
                break;
            case UnsavedChoice.Discard:
                RequestClose(sender);
                break;
        }
    }

    private void RequestClose(object? sender)
    {
        if (sender is not Window win) return;
        _closeApproved = true;
        win.Close();
    }
}
