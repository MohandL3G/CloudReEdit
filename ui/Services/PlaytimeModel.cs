using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace CloudReEdit.Services;

/// <summary>One per-device sub-total bucket. Synthetic buckets (name starts with
/// <c>__</c>, e.g. <c>__migrated_localconfig</c>) are the DLL's reconcile balance
/// and are the first to be reduced when lowering playtime.</summary>
public sealed class DevicePlaytime : INotifyPropertyChanged
{
    private string _name = "";
    private long _windows;
    private long _mac;
    private long _linux;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public long Windows
    {
        get => _windows;
        set { _windows = value; OnPropertyChanged(); OnPropertyChanged(nameof(Sum)); }
    }

    public long Mac
    {
        get => _mac;
        set { _mac = value; OnPropertyChanged(); OnPropertyChanged(nameof(Sum)); }
    }

    public long Linux
    {
        get => _linux;
        set { _linux = value; OnPropertyChanged(); OnPropertyChanged(nameof(Sum)); }
    }

    public long Sum => _windows + _mac + _linux;
    public bool IsSynthetic => Name.StartsWith("__", System.StringComparison.Ordinal);

    /// <summary>CloudRedirect daily playtime history for this device: unix day
    /// index (timestamp / 86400) -&gt; minutes. Compatibility data preserved
    /// verbatim; never edited in the UI. Synthetic buckets have none.</summary>
    public Dictionary<long, long> Days { get; } = new();

    /// <summary>True when this device carries a <c>days</c> property at all,
    /// even when it is empty. Mirrors CloudRedirect, where the presence of the
    /// object alone establishes day tracking.</summary>
    public bool DaysTracked => _daysPropertyPresent || Days.Count > 0;

    internal bool _daysPropertyPresent;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Playtime object as CloudRedirect stores it. The <c>per_device</c> buckets are
/// the source of truth: the parser recomputes minutes_forever and the per-OS
/// totals as the exact sum of the buckets, so this model derives them and never
/// lets them drift from the buckets.
/// </summary>
public sealed class PlaytimeModel
{
    public long Minutes2Weeks { get; set; }
    public long LastPlayed { get; set; }
    public ObservableCollection<DevicePlaytime> PerDevice { get; } = new();

    public long WindowsSum => PerDevice.Sum(d => d.Windows);
    public long MacSum => PerDevice.Sum(d => d.Mac);
    public long LinuxSum => PerDevice.Sum(d => d.Linux);
    public long Total => WindowsSum + MacSum + LinuxSum;

    // Mirrors CloudRedirect day tracking (stats_store.cpp:1144-1228): a day
    // index is (unix timestamp / 86400) and the two-week value is the sum of the
    // day buckets that fall inside the rolling 14-day window.
    internal const long SecondsPerDay = 24L * 60 * 60;
    internal const int TwoWeekDays = 14;
    internal const long TwoWeekWindowSeconds = TwoWeekDays * SecondsPerDay;

    /// <summary>True when at least one device carries a <c>days</c> property,
    /// even one that is currently empty.</summary>
    public bool HasDayTracking => PerDevice.Any(d => d.DaysTracked);

    /// <summary>CloudRedirect-compatible two-week total. When day tracking exists
    /// it is the sum of per-device day buckets inside the current 14-day window
    /// (expired and future days excluded), capped at the lifetime total. Otherwise
    /// it is the legacy behavior: the full lifetime total.</summary>
    /// <param name="nowUnix">Fixed unix timestamp for deterministic tests;
    /// defaults to the current time.</param>
    public long CalculateCloudRedirectTwoWeekTotal(long? nowUnix = null)
    {
        if (!HasDayTracking) return Total;

        long now = nowUnix ?? System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long cutoff = now - TwoWeekWindowSeconds;
        long maxDay = now / SecondsPerDay + 1;

        long sum = 0;
        foreach (var device in PerDevice)
        {
            foreach (var (day, minutes) in device.Days)
            {
                long dayEnd = day * SecondsPerDay + SecondsPerDay;
                if (dayEnd <= cutoff || day > maxDay) continue;
                sum += minutes;
            }
        }
        return System.Math.Min(sum, Total);
    }

    public static PlaytimeModel Parse(JsonObject? pt)
    {
        var m = new PlaytimeModel();
        if (pt == null) return m;

        if (pt.TryGetPropertyValue("minutes_2weeks", out var v))
            m.Minutes2Weeks = GetLong(v);
        if (pt.TryGetPropertyValue("last_played", out var lp))
            m.LastPlayed = GetLong(lp);

        if (pt.TryGetPropertyValue("per_device", out var pd) && pd is JsonObject pdo)
        {
            foreach (var (name, val) in pdo)
            {
                if (val is not JsonObject dv) continue;
                var device = new DevicePlaytime
                {
                    Name = name,
                    Windows = GetLong(dv["windows"]),
                    Mac = GetLong(dv["mac"]),
                    Linux = GetLong(dv["linux"]),
                };
                if (dv.TryGetPropertyValue("days", out var days) && days is JsonObject daysObj)
                {
                    device._daysPropertyPresent = true;
                    foreach (var (dayKey, minutes) in daysObj)
                    {
                        if (minutes == null) continue;
                        if (long.TryParse(dayKey, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out var day))
                            device.Days[day] = GetLong(minutes);
                    }
                }
                m.PerDevice.Add(device);
            }
        }

        return m;
    }

    public JsonObject ToJson()
    {
        var perDev = new JsonObject();
        foreach (var d in PerDevice)
        {
            var device = new JsonObject
            {
                ["windows"] = System.Math.Max(0, d.Windows),
                ["mac"] = System.Math.Max(0, d.Mac),
                ["linux"] = System.Math.Max(0, d.Linux),
            };
            if (d.DaysTracked)
            {
                var days = new JsonObject();
                foreach (var (day, minutes) in d.Days)
                    days[day.ToString(CultureInfo.InvariantCulture)] = minutes;
                device["days"] = days;
            }
            perDev[d.Name] = device;
        }

        return new JsonObject
        {
            ["minutes_forever"] = Total,
            ["minutes_2weeks"] = Minutes2Weeks,
            ["last_played"] = LastPlayed,
            ["windows"] = WindowsSum,
            ["mac"] = MacSum,
            ["linux"] = LinuxSum,
            ["per_device"] = perDev,
        };
    }

    /// <summary>Deep equality on every field a save depends on: lifetime total
    /// (derived from buckets), per-device cells, day-tracking state, the verbatim
    /// day buckets, two-week total and last_played. Used to verify a written copy
    /// matches the intended state.</summary>
    public bool SemanticallyEquals(PlaytimeModel? other)
    {
        if (other == null) return false;
        if (Total != other.Total) return false;
        if (Minutes2Weeks != other.Minutes2Weeks) return false;
        if (LastPlayed != other.LastPlayed) return false;
        if (HasDayTracking != other.HasDayTracking) return false;
        if (PerDevice.Count != other.PerDevice.Count) return false;

        foreach (var d in PerDevice)
        {
            var otherDev = other.PerDevice.FirstOrDefault(o => o.Name == d.Name);
            if (otherDev == null) return false;
            if (d.Windows != otherDev.Windows || d.Mac != otherDev.Mac || d.Linux != otherDev.Linux)
                return false;
            if (d.DaysTracked != otherDev.DaysTracked) return false;
            if (d.Days.Count != otherDev.Days.Count) return false;
            foreach (var (day, minutes) in d.Days)
            {
                if (!otherDev.Days.TryGetValue(day, out var otherMinutes) || otherMinutes != minutes)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Deep copy of the whole model, including the verbatim per-device
    /// day buckets and day-tracking presence. Used to freeze the editor state at
    /// the moment Save is clicked so the save transaction can never see edits
    /// made afterwards.</summary>
    public PlaytimeModel Clone()
    {
        var m = new PlaytimeModel
        {
            Minutes2Weeks = Minutes2Weeks,
            LastPlayed = LastPlayed,
        };
        foreach (var d in PerDevice)
        {
            var nd = new DevicePlaytime
            {
                Name = d.Name,
                Windows = d.Windows,
                Mac = d.Mac,
                Linux = d.Linux,
            };
            nd._daysPropertyPresent = d._daysPropertyPresent;
            foreach (var (day, minutes) in d.Days)
                nd.Days[day] = minutes;
            m.PerDevice.Add(nd);
        }
        return m;
    }

    private static long GetLong(JsonNode? node)
    {
        if (node == null) return 0;
        try { return node.GetValue<long>(); }
        catch { return 0; }
    }

    /// <summary>
    /// Adjusts the buckets so their sum equals <paramref name="target"/> minutes.
    /// Growing adds to <c>__migrated_localconfig</c>; shrinking first drains
    /// synthetic buckets, then rescales real device buckets proportionally.
    /// </summary>
    public void AdjustBucketsToTotal(long target)
    {
        target = System.Math.Max(0, target);
        long current = Total;
        if (current == target) return;

        long diff = target - current;

        if (diff > 0)
        {
            var bal = PerDevice.FirstOrDefault(d => d.Name == "__migrated_localconfig");
            if (bal == null)
            {
                bal = new DevicePlaytime { Name = "__migrated_localconfig" };
                PerDevice.Add(bal);
            }
            bal.Windows += diff;
            return;
        }

        long need = -diff;

        foreach (var synth in PerDevice.Where(d => d.IsSynthetic).ToList())
        {
            if (need == 0) break;
            need -= TakeFromDevice(synth, need);
            if (synth.Sum == 0) PerDevice.Remove(synth);
        }

        if (need == 0) return;

        long realSum = PerDevice.Sum(d => d.Sum);
        if (realSum == 0)
        {
            if (target > 0)
            {
                PerDevice.Add(new DevicePlaytime
                {
                    Name = "__migrated_localconfig",
                    Windows = target,
                });
            }
            return;
        }

        RescaleToTotal(target);
    }

    /// <summary>Removes up to <paramref name="take"/> minutes, windows-first.</summary>
    private static long TakeFromDevice(DevicePlaytime d, long take)
    {
        long left = take;
        long w = System.Math.Min(d.Windows, left); d.Windows -= w; left -= w;
        long m = System.Math.Min(d.Mac, left); d.Mac -= m; left -= m;
        long l = System.Math.Min(d.Linux, left); d.Linux -= l; left -= l;
        return take - left;
    }

    /// <summary>Scales every (device, platform) cell proportionally so the total
    /// equals <paramref name="target"/>, using largest-remainder rounding.</summary>
    private void RescaleToTotal(long target)
    {
        var cells = new List<(DevicePlaytime Dev, int Plat, long Cur)>();
        foreach (var d in PerDevice)
        {
            cells.Add((d, 0, d.Windows));
            cells.Add((d, 1, d.Mac));
            cells.Add((d, 2, d.Linux));
        }
        cells = cells.Where(c => c.Cur > 0).ToList();
        long curSum = cells.Sum(c => c.Cur);
        if (curSum == 0) return;

        double factor = (double)target / curSum;
        long assigned = 0;
        var fracs = new List<(int Index, double Frac, long Value)>();
        for (int i = 0; i < cells.Count; i++)
        {
            double exact = cells[i].Cur * factor;
            long v = (long)exact;
            fracs.Add((i, exact - v, v));
            assigned += v;
        }

        long rem = target - assigned;
        foreach (var item in fracs.OrderByDescending(f => f.Frac).Take((int)System.Math.Clamp(rem, 0, cells.Count)))
        {
            fracs[item.Index] = (item.Index, item.Frac, item.Value + 1);
        }

        for (int i = 0; i < cells.Count; i++)
        {
            long v = fracs[i].Value;
            var (dev, plat, _) = cells[i];
            switch (plat)
            {
                case 0: dev.Windows = v; break;
                case 1: dev.Mac = v; break;
                default: dev.Linux = v; break;
            }
        }
    }
}
