using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CloudReEdit.Services;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreCache))]
internal partial class StoreCacheJsonContext : JsonSerializerContext { }

internal class StoreAppInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("headerUrl")]
    public string? HeaderUrl { get; set; }

    [JsonPropertyName("fetchedUtc")]
    public DateTime FetchedUtc { get; set; }
}

internal class StoreCache
{
    [JsonPropertyName("entries")]
    public Dictionary<uint, StoreAppInfo> Entries { get; set; } = new();
}

internal sealed class SteamStoreClient : IDisposable
{
    public static SteamStoreClient Shared { get; } = new();

    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan ImageCacheTtl = TimeSpan.FromDays(60);
    private const long MaxCachedImageBytes = 5 * 1024 * 1024;
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_cache.json");

    private static readonly string ImageCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_images");

    private static readonly string[] AllowedCdnHosts = new[]
    {
        ".steamstatic.com",
        ".steampowered.com",
        ".steamcdn-a.akamaihd.net"
    };

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<uint, StoreAppInfo> _mem = new();
    private volatile bool _diskLoaded;
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Task> _inFlightDownloads = new();

    public SteamStoreClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static bool IsValidSteamCdnUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https") return false;

        var host = uri.Host;
        foreach (var allowed in AllowedCdnHosts)
        {
            if (host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsValidCachedImagePath(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "file") return false;

        string normalizedLocal;
        string normalizedCache;
        try
        {
            normalizedLocal = Path.GetFullPath(uri.LocalPath);
            normalizedCache = Path.GetFullPath(ImageCacheDir);
        }
        catch
        {
            return false;
        }

        var prefix = normalizedCache.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedCache
            : normalizedCache + Path.DirectorySeparatorChar;

        return normalizedLocal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidImageUrl(string? url)
        => IsValidSteamCdnUrl(url) || IsValidCachedImagePath(url);

    private static string GetCachedImagePath(string cdnUrl)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(cdnUrl), hash);
        var hex = Convert.ToHexString(hash[..8]);
        return Path.Combine(ImageCacheDir, $"{hex}.jpg");
    }

    public async Task<Dictionary<uint, StoreAppInfo>> GetAppInfoAsync(IReadOnlyList<uint> appIds)
    {
        if (appIds.Count == 0)
            return new Dictionary<uint, StoreAppInfo>();

        await EnsureDiskCacheLoaded().ConfigureAwait(false);

        var result = new Dictionary<uint, StoreAppInfo>();
        var toFetch = new List<uint>();

        foreach (var id in appIds)
        {
            if (_mem.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.FetchedUtc < DiskCacheTtl)
                result[id] = cached;
            else
                toFetch.Add(id);
        }

        if (toFetch.Count > 0)
        {
            var fetched = await FetchFromApiAsync(toFetch).ConfigureAwait(false);
            foreach (var (id, info) in fetched)
            {
                _mem[id] = info;
                result[id] = info;
            }

            _ = SaveDiskCacheAsync();
        }

        foreach (var key in result.Keys.ToArray())
        {
            var info = result[key];
            if (!IsValidSteamCdnUrl(info.HeaderUrl)) continue;

            var localPath = GetCachedImagePath(info.HeaderUrl!);
            if (File.Exists(localPath))
            {
                var rewritten = new StoreAppInfo
                {
                    Name = info.Name,
                    HeaderUrl = new Uri(localPath).AbsoluteUri,
                    FetchedUtc = info.FetchedUtc
                };
                result[key] = rewritten;
            }
            else
            {
                _ = _inFlightDownloads.GetOrAdd(localPath, lp =>
                    DownloadImageToCacheAsync(info.HeaderUrl!, lp)
                        .ContinueWith(t =>
                        {
                            _inFlightDownloads.TryRemove(lp, out _);
                        }, TaskScheduler.Default));
            }
        }

        return result;
    }

    private async Task DownloadImageToCacheAsync(string cdnUrl, string localPath)
    {
        if (!IsValidSteamCdnUrl(cdnUrl)) return;

        string? tmp = null;
        try
        {
            Directory.CreateDirectory(ImageCacheDir);

            if (File.Exists(localPath)) return;

            using var resp = await _http.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return;

            if (resp.Content.Headers.ContentLength is long declared && declared > MaxCachedImageBytes)
                return;

            tmp = $"{localPath}.{Guid.NewGuid():N}.tmp";

            long written = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory())) > 0)
                {
                    written += n;
                    if (written > MaxCachedImageBytes)
                        return;
                    await dst.WriteAsync(buf.AsMemory(0, n));
                }
            }

            if (written == 0) return;

            File.Move(tmp, localPath, overwrite: true);
            tmp = null;
        }
        catch
        {
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    private static void EvictStaleImages()
    {
        try
        {
            if (!Directory.Exists(ImageCacheDir)) return;
            var cutoff = DateTime.UtcNow - ImageCacheTtl;

            foreach (var file in Directory.EnumerateFiles(ImageCacheDir, "*.jpg"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }

            var tmpCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            foreach (var file in Directory.EnumerateFiles(ImageCacheDir, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < tmpCutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task<Dictionary<uint, StoreAppInfo>> FetchFromApiAsync(List<uint> appIds)
    {
        var result = new Dictionary<uint, StoreAppInfo>();

        try
        {
            var ids = appIds.Select(id => new { appid = id }).ToArray();
            var requestObj = new
            {
                ids,
                context = new { language = "english", country_code = "US" },
                data_request = new { include_basic_info = true, include_assets = true }
            };

            var inputJson = JsonSerializer.Serialize(requestObj);
            var encoded = Uri.EscapeDataString(inputJson);
            var url = $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json={encoded}";

            var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return result;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("response", out var response))
                return result;
            if (!response.TryGetProperty("store_items", out var items))
                return result;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("appid", out var appIdEl))
                    continue;

                uint appId = appIdEl.GetUInt32();

                var info = new StoreAppInfo
                {
                    FetchedUtc = DateTime.UtcNow
                };

                if (item.TryGetProperty("name", out var nameEl))
                    info.Name = nameEl.GetString() ?? "";

                if (item.TryGetProperty("assets", out var assets) &&
                    assets.TryGetProperty("header", out var headerEl))
                {
                    var header = headerEl.GetString();
                    if (!string.IsNullOrEmpty(header))
                        info.HeaderUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/{header}";
                }

                result[appId] = info;
            }
        }
        catch
        {
        }

        return result;
    }

    private async Task EnsureDiskCacheLoaded()
    {
        if (_diskLoaded) return;

        await _diskLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_diskLoaded) return;

            if (File.Exists(CachePath))
            {
                var json = await File.ReadAllTextAsync(CachePath).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize(json, StoreCacheJsonContext.Default.StoreCache);
                if (cache?.Entries != null)
                {
                    foreach (var (id, info) in cache.Entries)
                        _mem.TryAdd(id, info);
                }
            }

            _ = Task.Run(EvictStaleImages);

            _diskLoaded = true;
        }
        catch
        {
            _diskLoaded = true;
        }
        finally
        {
            _diskLock.Release();
        }
    }

    private async Task SaveDiskCacheAsync()
    {
        try
        {
            var cache = new StoreCache
            {
                Entries = new Dictionary<uint, StoreAppInfo>(_mem)
            };

            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(cache, StoreCacheJsonContext.Default.StoreCache);
            await File.WriteAllTextAsync(CachePath, json).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _diskLock.Dispose();
    }
}
