using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CloudReEdit.Services;

/// <summary>Retention policy for the per-save backup directories written under
/// <c>%AppData%\CloudReEdit\backups</c>. The newest <see cref="KeepPerPair"/>
/// directories are kept per account+game pair and older ones are pruned.
///
/// Retention only decides which directories survive — <c>manifest.json</c> stays
/// the source of truth for what a backup contains. A pruning failure never throws:
/// it is collected into <see cref="PruneResult.Errors"/> so callers can report it
/// without letting retention disturb a save's outcome.</summary>
public static class BackupRetention
{
    /// <summary>Number of backup directories kept per account+game pair.</summary>
    public const int KeepPerPair = 5;

    /// <summary>Length of the <c>yyyyMMdd_HHmmss_fff</c> timestamp the save protocol
    /// prefixes every backup directory name with.</summary>
    private const int TimestampLength = 19;

    /// <summary>The app's own backup root: <c>%AppData%\CloudReEdit\backups</c>.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudReEdit", "backups");

    /// <summary>Prunes backup directories under <paramref name="backupRoot"/>, keeping
    /// the <paramref name="keep"/> newest per account+game pair. Directories whose
    /// name does not match the save protocol's naming scheme are always left alone.
    /// Never throws.</summary>
    /// <param name="backupRoot">Directory holding the per-save backup folders.</param>
    /// <param name="protectDir">Absolute path of a backup that must survive the sweep
    /// — e.g. the backup belonging to a save that is still in progress.</param>
    /// <param name="keep">How many directories to keep per pair.</param>
    public static PruneResult Prune(string backupRoot, string? protectDir = null,
        int keep = KeepPerPair)
    {
        var result = new PruneResult();
        if (string.IsNullOrEmpty(backupRoot) || keep < 0) return result;

        try
        {
            if (!Directory.Exists(backupRoot)) return result;

            string? protectedFull = null;
            if (!string.IsNullOrEmpty(protectDir))
            {
                try { protectedFull = Path.GetFullPath(protectDir); }
                catch { protectedFull = protectDir; }
            }

            var pairs = new Dictionary<string, List<BackupEntry>>(StringComparer.Ordinal);

            foreach (var dir in Directory.EnumerateDirectories(backupRoot))
            {
                result.Scanned++;

                if (!TryParseDirName(Path.GetFileName(dir), out var entry))
                    continue;

                entry.Path = dir;
                if (protectedFull != null && SamePath(dir, protectedFull))
                    entry.IsProtected = true;

                var key = entry.AccountId + "\u0000" + entry.AppId;
                if (!pairs.TryGetValue(key, out var list))
                    pairs[key] = list = new List<BackupEntry>();
                list.Add(entry);
            }

            foreach (var list in pairs.Values)
            {
                // Newest first. Ordering comes from the name's timestamp so it is
                // stable and independent of the local clock's view of file times.
                var ordered = list.OrderByDescending(e => e.Timestamp, StringComparer.Ordinal)
                                  .ThenBy(e => e.Path, StringComparer.Ordinal)
                                  .ToList();

                foreach (var entry in ordered.Skip(keep))
                {
                    if (entry.IsProtected) continue;

                    try
                    {
                        Directory.Delete(entry.Path, recursive: true);
                        result.Deleted++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{entry.Path}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{backupRoot}: {ex.Message}");
        }

        return result;
    }

    /// <summary>Counts the backup directories under <paramref name="backupRoot"/> and
    /// totals their on-disk size. Never throws; an unreadable root reports zeroes.</summary>
    public static BackupStats GetStats(string backupRoot)
    {
        var stats = new BackupStats { Root = backupRoot };
        try
        {
            if (string.IsNullOrEmpty(backupRoot) || !Directory.Exists(backupRoot))
                return stats;

            foreach (var dir in Directory.EnumerateDirectories(backupRoot))
            {
                stats.Count++;
                stats.TotalBytes += DirectorySize(dir);
            }
        }
        catch { }
        return stats;
    }

    /// <summary>Renders a byte count for display, e.g. <c>12.4 MB</c>.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Parses the directory name the save protocol writes:
    /// <c>{yyyyMMdd_HHmmss_fff}_{accountId}-{appId}</c>, with an optional
    /// <c>-{n}</c> uniqueness suffix appended when two saves collide in the same
    /// millisecond. Account ids are the numeric Steam account folder names.</summary>
    internal static bool TryParseDirName(string name, out BackupEntry entry)
    {
        entry = new BackupEntry();
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length < TimestampLength + 4) return false;

        var timestamp = name.Substring(0, TimestampLength);
        if (name[TimestampLength] != '_') return false;

        for (int i = 0; i < TimestampLength; i++)
        {
            if (i == 8 || i == 15)
            {
                if (timestamp[i] != '_') return false;
            }
            else if (!char.IsAsciiDigit(timestamp[i]))
            {
                return false;
            }
        }

        var rest = name.Substring(TimestampLength + 1);
        var parts = rest.Split('-');
        if (parts.Length < 2) return false;
        if (!uint.TryParse(parts[1], out var appId)) return false;
        if (parts[0].Length == 0) return false;

        entry.AccountId = parts[0];
        entry.AppId = appId;
        entry.Timestamp = timestamp;
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        try { a = Path.GetFullPath(a); }
        catch { }
        try { b = Path.GetFullPath(b); }
        catch { }

        return string.Equals(a.TrimEnd(Path.DirectorySeparatorChar),
                             b.TrimEnd(Path.DirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase);
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    internal sealed class BackupEntry
    {
        public string Path = "";
        public string AccountId = "";
        public uint AppId;
        public string Timestamp = "";
        public bool IsProtected;
    }
}

/// <summary>Outcome of a single retention sweep.</summary>
public sealed class PruneResult
{
    public int Scanned { get; set; }
    public int Deleted { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>Aggregate size of the backup root, for display in the UI.</summary>
public sealed class BackupStats
{
    public string Root { get; set; } = "";
    public int Count { get; set; }
    public long TotalBytes { get; set; }
}
