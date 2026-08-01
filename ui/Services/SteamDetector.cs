using System.Diagnostics;
using System.IO;
using CloudReEdit.Resources;
using Microsoft.Win32;

namespace CloudReEdit.Services;

/// <summary>
/// Detects the Steam installation path via the Windows registry or well-known paths.
/// </summary>
public static class SteamDetector
{
    private static readonly object _cacheLock = new();
    private static string? _cachedPath;

    public static string? FindSteamPath()
    {
        lock (_cacheLock)
        {
            if (_cachedPath != null)
                return _cachedPath;
        }

        var resolved = NormalizeToSteamRoot(TryRegistry())
                       ?? NormalizeToSteamRoot(TryKnownPaths());

        lock (_cacheLock)
        {
            if (_cachedPath != null)
                return _cachedPath;
            _cachedPath = resolved;
            return _cachedPath;
        }
    }

    public static bool SetSteamPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        var root = NormalizeToSteamRoot(path);
        if (root == null)
            return false;
        lock (_cacheLock) { _cachedPath = root; }
        return true;
    }

    private static string? NormalizeToSteamRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var dir = new DirectoryInfo(path);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "steam.exe")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            path = key32?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            using var keyUser = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            path = keyUser?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
        }
        catch
        {
        }

        return null;
    }

    private static string? TryKnownPaths()
    {
        string[] candidates =
        [
            @"C:\Games\Steam",
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            @"D:\Steam",
            @"D:\Games\Steam",
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }

        return null;
    }

    public static string GetConfigDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudRedirect");
    }

    public static string GetConfigFilePath()
    {
        return Path.Combine(GetConfigDir(), "config.json");
    }

    public static bool IsSteamRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("steam");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    public static CloudConfig? ReadConfig()
    {
        var configPath = GetConfigFilePath();
        if (!File.Exists(configPath)) return null;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("provider", out var providerProp))
                return null;
            var provider = providerProp.GetString();
            if (provider == null) return null;

            string? tokenPath = null;
            if (root.TryGetProperty("token_path", out var tp))
                tokenPath = tp.GetString();

            string? syncPath = null;
            if (root.TryGetProperty("sync_path", out var sp))
                syncPath = sp.GetString();

            return new CloudConfig(provider, tokenPath, syncPath);
        }
        catch
        {
            return null;
        }
    }
}

public record CloudConfig(string Provider, string? TokenPath, string? SyncPath)
{
    public string DisplayName => Provider switch
    {
        "gdrive" => S.Get("Provider_GoogleDrive"),
        "onedrive" => S.Get("Provider_OneDrive"),
        "r2" => S.Get("Provider_R2"),
        "s3" => S.Get("Provider_S3"),
        "folder" => S.Get("Provider_FolderNetworkDrive"),
        "local" => S.Get("Provider_LocalOnly"),
        _ => Provider
    };

    public bool IsFolder => Provider == "folder";
    public bool IsLocal => Provider == "local";
}
