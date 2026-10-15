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
    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_cache.json");

    private static readonly string DefaultImageCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_images");

    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] RiffMagic = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
    private static readonly byte[] WebpMagic = { (byte)'W', (byte)'E', (byte)'B', (byte)'P' };

    private readonly string _cachePath;
    private readonly string _imageCacheDir;

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

    public SteamStoreClient() : this(null, null, null)
    {
    }

    /// <summary>Test seam: an explicit message handler (a stub never follows
    /// redirects on its own, so it can script the final response URI), plus cache
    /// locations that default to CloudRedirect's AppData folder.</summary>
    internal SteamStoreClient(HttpMessageHandler? handler, string? cachePath, string? imageCacheDir)
    {
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _cachePath = cachePath ?? DefaultCachePath;
        _imageCacheDir = imageCacheDir ?? DefaultImageCacheDir;
    }

    /// <summary>True when <paramref name="url"/> is an absolute https URL on a
    /// Steam CDN/store host.</summary>
    public static bool IsValidSteamCdnUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsAllowedResponseUri(uri);
    }

    /// <summary>True only for https URIs on a Steam CDN/store host. Every HTTP
    /// response - the store API call and image downloads alike - must land on an
    /// allowed URI after redirects, otherwise the response is discarded.</summary>
    public static bool IsAllowedResponseUri(Uri? uri)
    {
        if (uri == null || uri.Scheme != "https") return false;

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
            normalizedCache = Path.GetFullPath(DefaultImageCacheDir);
        }
        catch (Exception ex)
        {
            AppLog.Debug("store", $"cached image path validation failed: {ex.GetType().Name}");
            return false;
        }

        var prefix = normalizedCache.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedCache
            : normalizedCache + Path.DirectorySeparatorChar;

        return normalizedLocal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidImageUrl(string? url)
        => IsValidSteamCdnUrl(url) || IsValidCachedImagePath(url);

    private string GetCachedImagePath(string cdnUrl)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(cdnUrl), hash);
        var hex = Convert.ToHexString(hash[..8]);
        return Path.Combine(_imageCacheDir, $"{hex}.jpg");
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
            {
                result[id] = cached;
                AppLog.Debug("store", $"cache hit app={id}");
            }
            else
            {
                toFetch.Add(id);
                AppLog.Debug("store", $"cache miss app={id}");
            }
        }

        if (toFetch.Count > 0)
        {
            AppLog.Info("store", $"fetching {toFetch.Count} app(s) from the Steam store API");
            var fetched = await FetchFromApiAsync(toFetch).ConfigureAwait(false);
            AppLog.Info("store", $"store API returned {fetched.Count} of {toFetch.Count} requested app(s)");
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
                AppLog.Debug("store", $"image cache hit app={key}");
            }
            else
            {
                AppLog.Debug("store", $"image cache miss app={key}");
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

    /// <summary>Downloads one header image into the cache. The response is only
    /// kept when it lands on an allowed Steam CDN host after redirects and its
    /// bytes are a real JPEG, PNG or WebP image; anything else is discarded.</summary>
    internal async Task DownloadImageToCacheAsync(string cdnUrl, string localPath)
    {
        if (!IsValidSteamCdnUrl(cdnUrl)) return;

        string? tmp = null;
        try
        {
            Directory.CreateDirectory(_imageCacheDir);

            if (File.Exists(localPath)) return;

            using var resp = await _http.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn("store", $"image download failed with HTTP {(int)resp.StatusCode}: {cdnUrl}");
                return;
            }

            if (!IsAllowedResponseUri(resp.RequestMessage?.RequestUri))
            {
                AppLog.Warn("store", $"image download rejected: redirected outside the Steam CDN allowlist: {cdnUrl}");
                return;
            }

            if (resp.Content.Headers.ContentLength is long declared && declared > MaxCachedImageBytes)
            {
                AppLog.Warn("store", $"image too large ({declared} bytes): {cdnUrl}");
                return;
            }

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

            if (written == 0)
            {
                AppLog.Warn("store", $"image download produced no data: {cdnUrl}");
                return;
            }

            if (!FileLooksLikeImage(tmp))
            {
                AppLog.Warn("store", $"image download was not a JPEG, PNG or WebP image: {cdnUrl}");
                return;
            }

            File.Move(tmp, localPath, overwrite: true);
            tmp = null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("store", $"image download failed: {cdnUrl}", ex);
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); }
                catch (Exception cleanupEx)
                {
                    AppLog.Debug("store", $"image temp cleanup failed: {cleanupEx.GetType().Name}");
                }
            }
        }
    }

    /// <summary>True when <paramref name="header"/> starts with a JPEG
    /// (<c>FF D8 FF</c>), PNG (<c>89 50 4E 47 0D 0A 1A 0A</c>) or WebP
    /// (<c>RIFF....WEBP</c>) signature.</summary>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> header)
    {
        if (header.Length >= JpegMagic.Length && header[..JpegMagic.Length].SequenceEqual(JpegMagic))
            return true;
        if (header.Length >= PngMagic.Length && header[..PngMagic.Length].SequenceEqual(PngMagic))
            return true;
        return header.Length >= 12 &&
               header[..RiffMagic.Length].SequenceEqual(RiffMagic) &&
               header.Slice(8, WebpMagic.Length).SequenceEqual(WebpMagic);
    }

    private static bool FileLooksLikeImage(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read = fs.Read(header);
            return LooksLikeImage(header[..read]);
        }
        catch (Exception ex)
        {
            AppLog.Debug("store", $"image header read failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void EvictStaleImages()
    {
        int evicted = 0;
        try
        {
            if (!Directory.Exists(_imageCacheDir)) return;
            var cutoff = DateTime.UtcNow - ImageCacheTtl;

            foreach (var file in Directory.EnumerateFiles(_imageCacheDir, "*.jpg"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        evicted++;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("store", $"image eviction failed for \"{file}\": {ex.GetType().Name}");
                }
            }

            var tmpCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            foreach (var file in Directory.EnumerateFiles(_imageCacheDir, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < tmpCutoff)
                    {
                        File.Delete(file);
                        evicted++;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("store", $"image temp eviction failed for \"{file}\": {ex.GetType().Name}");
                }
            }

            if (evicted > 0)
                AppLog.Debug("store", $"evicted {evicted} stale cache file(s)");
        }
        catch (Exception ex)
        {
            AppLog.Exception("store", "image cache sweep failed", ex);
        }
    }

    /// <summary>Queries the Steam store API. A response that lands on a host
    /// outside the Steam allowlist after redirects is discarded as a whole.</summary>
    internal async Task<Dictionary<uint, StoreAppInfo>> FetchFromApiAsync(List<uint> appIds)
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

            if (!IsAllowedResponseUri(resp.RequestMessage?.RequestUri))
            {
                AppLog.Warn("store", "store API response rejected: redirected outside the Steam allowlist");
                return result;
            }

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
        catch (Exception ex)
        {
            AppLog.Exception("store", "store API response could not be parsed", ex);
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

            if (File.Exists(_cachePath))
            {
                var json = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            AppLog.Exception("store", $"store cache load failed: \"{_cachePath}\"", ex);
            _diskLoaded = true;
        }
        finally
        {
            _diskLock.Release();
        }
    }

    /// <summary>Persists the in-memory cache to <c>store_cache.json</c>. Overlapping
    /// saves are serialized through the same lock as the disk load, and the file is
    /// replaced atomically (temp file + move), so concurrent fire-and-forget saves
    /// can neither collide nor leave a half-written file behind. The JSON schema is
    /// deliberately unchanged - the file lives in CloudRedirect's folder and is
    /// shared with it.</summary>
    internal async Task SaveDiskCacheAsync()
    {
        bool taken = false;
        try
        {
            await _diskLock.WaitAsync().ConfigureAwait(false);
            taken = true;

            var cache = new StoreCache
            {
                Entries = new Dictionary<uint, StoreAppInfo>(_mem)
            };

            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(cache, StoreCacheJsonContext.Default.StoreCache);
            FileUtils.AtomicWriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Exception("store", $"store cache save failed: \"{_cachePath}\"", ex);
        }
        finally
        {
            if (taken)
            {
                try { _diskLock.Release(); }
                catch (Exception ex)
                {
                    AppLog.Debug("store", $"store cache lock release failed: {ex.GetType().Name}");
                }
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _diskLock.Dispose();
    }
}
