using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Services;
using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Pages;

public partial class PlaytimeEditorPage : Page
{
    private EditorContext? _ctx;
    private ICloudProvider? _provider;
    private bool _useHours;
    private bool _syncingTotal;
    private DateTime _existingLocal = DateTime.Now;

    public PlaytimeEditorPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => (_provider as IDisposable)?.Dispose();
    }

    private async Task LoadAsync()
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
    }

    private void HoursSwitch_Changed(object sender, RoutedEventArgs e)
    {
        _useHours = HoursSwitch.IsChecked == true;
        UnitText.Text = _useHours ? "hours" : "minutes";
        RefreshTotals();
    }

    private void DevicesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            RefreshTotals();
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
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
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx == null || DevicesGrid.SelectedItem is not DevicePlaytime device) return;
        _ctx.Local.PerDevice.Remove(device);
        RefreshTotals();
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        LastPlayedPicker.Date = DateTime.Now;
    }

    private long GetLastPlayedUnix()
    {
        var date = LastPlayedPicker.Date ?? _existingLocal.Date;
        var local = DateTime.SpecifyKind(date.Date + _existingLocal.TimeOfDay, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUnixTimeSeconds();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;

        if (SteamDetector.IsSteamRunning())
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            ResultBar.Title = "Steam is running";
            ResultBar.Message = "Close Steam before editing playtime, otherwise Steam may overwrite your changes.";
            ResultBar.IsOpen = true;
            return;
        }

        long total = ParseTotalMinutes();
        long lastPlayed = GetLastPlayedUnix();

        SaveButton.IsEnabled = false;
        try
        {
            var report = await PlaytimeService.SaveAsync(_ctx, total, lastPlayed);
            if (report.Outcome is SaveOutcome.SavedVerified or SaveOutcome.SavedUnverified)
                await LoadAsync();
            ApplyReport(report, total);
        }
        finally
        {
            SaveButton.IsEnabled = true;
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
        => await LoadAsync();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow win)
            win.Navigate(typeof(GamesPage));
    }
}
