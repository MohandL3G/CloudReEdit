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
    private const int MaxRedirectHops = 5;

    /// <summary>Default location of the shared store cache
    /// (<c>%AppData%\CloudRedirect\store_cache.json</c>).
    ///
    /// Deliberately a computed property, not a <c>static readonly</c> field:
    /// <see cref="Shared"/> is built while this type's static fields are still
    /// initializing (static field initializers run in textual order and
    /// <c>Shared</c> is declared first), so a field read from the constructor
    /// would capture its default value - null - and every lookup would then
    /// fail on a null path. Evaluating on each read makes the value correct no
    /// matter when it is asked for. Regression-pinned by
    /// <c>SharedSingleton_ResolvesItsCachePathsDuringConstruction</c>.</summary>
    private static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_cache.json");

    /// <summary>Default image cache directory next to
    /// <see cref="DefaultCachePath"/>; evaluated on each read for the same
    /// static-initialization reason.</summary>
    private static string DefaultImageCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudRedirect", "store_images");

    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] RiffMagic = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
    private static readonly byte[] WebpMagic = { (byte)'W', (byte)'E', (byte)'B', (byte)'P' };

    private readonly string _cachePath;
    private readonly string _imageCacheDir;

    /// <summary>Hosts an image download may be sent to (and may land on after
    /// redirects): Steam CDN/store hosts by suffix.</summary>
    private static readonly string[] AllowedCdnHosts = new[]
    {
        ".steamstatic.com",
        ".steampowered.com",
        ".steamcdn-a.akamaihd.net"
    };

    /// <summary>The single host the store API call may be sent to (and may land
    /// on after redirects). Deliberately exact: the API allowlist is per-purpose
    /// and much narrower than the CDN allowlist above.</summary>
    private const string AllowedApiHost = "api.steampowered.com";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<uint, StoreAppInfo> _mem = new();
    private volatile bool _diskLoaded;
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Task> _inFlightDownloads = new();

    /// <summary>Creates the shared client with CloudRedirect's default cache
    /// locations.</summary>
    public SteamStoreClient() : this(null, null, null)
    {
    }

    /// <summary>Test seam: an explicit message handler (a stub never follows
    /// redirects on its own, so it can script the final response URI), plus cache
    /// locations that default to CloudRedirect's AppData folder.
    ///
    /// Redirects are followed manually by
    /// <see cref="GetFollowingRedirectsAsync"/> so every hop can be checked
    /// against the per-purpose allowlist before the request is sent, which needs
    /// auto-redirect off on the real handler.</summary>
    internal SteamStoreClient(HttpMessageHandler? handler, string? cachePath, string? imageCacheDir)
    {
        _http = handler == null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15)
            }
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

    /// <summary>True only for https URIs on a Steam CDN/store host - the
    /// allowlist every image download must stay inside, on the initial request
    /// and on every redirect hop.</summary>
    public static bool IsAllowedResponseUri(Uri? uri)
        => IsAllowedUri(uri, allowedHost: null);

    /// <summary>True only for https URIs on the Steam store API host - the much
    /// narrower allowlist the store API call must stay inside, on the initial
    /// request and on every redirect hop. A CDN host is deliberately NOT enough
    /// here: the API and the CDN are separate purposes with separate lists.</summary>
    public static bool IsAllowedApiUri(Uri? uri)
        => IsAllowedUri(uri, AllowedApiHost);

    private static bool IsAllowedUri(Uri? uri, string? allowedHost)
    {
        if (uri == null || uri.Scheme != "https") return false;

        var host = uri.Host;
        if (allowedHost != null)
            return host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Returns store info for the requested apps: fresh cache entries
    /// first, then one Steam API fetch for the misses. Failed or partial lookups
    /// are returned for display but never persisted (see <see cref="IsComplete"/>),
    /// so they are refetched on the next call instead of being served until their
    /// TTL expires. The per-entry image pass is guarded so one unusable entry can
    /// never discard the names of every other game on the page.</summary>
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
                if (IsComplete(info))
                {
                    _mem[id] = info;
                }
                else
                {
                    AppLog.Debug("store",
                        $"not caching incomplete entry app={id} (name='{info.Name}', headerUrl={(string.IsNullOrEmpty(info.HeaderUrl) ? "missing" : info.HeaderUrl)}) - it will be fetched again next time");
                }
                result[id] = info;
            }

            _ = SaveDiskCacheAsync();
        }

        foreach (var key in result.Keys.ToArray())
        {
            var info = result[key];
            try
            {
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
            catch (Exception ex)
            {
                AppLog.Exception("store", $"image cache check failed app={key}", ex);
            }
        }

        return result;
    }

    /// <summary>True for the only entries worth caching: a fetched name plus a
    /// header URL that is allowed on the Steam CDN. Everything else is a failed
    /// or partial lookup and is refetched instead of served from the cache.</summary>
    private static bool IsComplete(StoreAppInfo info)
        => !string.IsNullOrEmpty(info.Name) && IsValidSteamCdnUrl(info.HeaderUrl);

    /// <summary>Downloads one header image into the cache. The response is only
    /// kept when it stays on an allowed Steam CDN host - on the initial URL and
    /// on every redirect hop - and its bytes are a real JPEG, PNG or WebP image;
    /// anything else is discarded and logged with the reason.</summary>
    internal async Task DownloadImageToCacheAsync(string cdnUrl, string localPath)
    {
        if (!IsValidSteamCdnUrl(cdnUrl))
        {
            AppLog.Warn("store", $"image download rejected: \"{cdnUrl}\" is not an allowed Steam CDN URL");
            return;
        }

        string? tmp = null;
        try
        {
            Directory.CreateDirectory(_imageCacheDir);

            if (File.Exists(localPath)) return;

            using var resp = await GetFollowingRedirectsAsync(
                cdnUrl, IsAllowedResponseUri, "image download",
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (resp == null) return;

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn("store", $"image download failed with HTTP {(int)resp.StatusCode}: {cdnUrl}");
                return;
            }

            if (!IsAllowedResponseUri(resp.RequestMessage?.RequestUri))
            {
                AppLog.Warn("store", $"image download rejected: response landed on \"{resp.RequestMessage?.RequestUri?.Host}\" outside the Steam CDN allowlist: {cdnUrl}");
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

    /// <summary>Sends a GET and follows redirects itself, checking the initial
    /// URL and every hop against <paramref name="isAllowedUri"/> before the
    /// request is sent, so a request never leaves the machine for a host outside
    /// the per-purpose allowlist. Returns the final response, or null when a hop
    /// was rejected or the hop limit was hit - the reason is already in
    /// <see cref="AppLog"/>.</summary>
    private async Task<HttpResponseMessage?> GetFollowingRedirectsAsync(
        string url, Func<Uri, bool> isAllowedUri, string purpose, HttpCompletionOption completionOption)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current))
        {
            AppLog.Warn("store", $"{purpose} rejected: \"{url}\" is not an absolute URL");
            return null;
        }

        for (int hop = 0; hop <= MaxRedirectHops; hop++)
        {
            if (!isAllowedUri(current))
            {
                AppLog.Warn("store", $"{purpose} rejected: host \"{current.Host}\" is not allowed");
                return null;
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(current, completionOption).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Exception("store", $"{purpose} failed for host \"{current.Host}\"", ex);
                return null;
            }

            if ((int)resp.StatusCode < 300 || (int)resp.StatusCode >= 400)
                return resp;

            var location = resp.Headers.Location;
            if (location == null)
            {
                AppLog.Warn("store",
                    $"{purpose} failed: HTTP {(int)resp.StatusCode} redirect from \"{current.Host}\" without a Location header");
                resp.Dispose();
                return null;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            resp.Dispose();

            if (!isAllowedUri(next))
            {
                AppLog.Warn("store",
                    $"{purpose} redirect rejected: \"{current.Host}\" -> \"{next.Host}\" leaves the Steam allowlist");
                return null;
            }

            current = next;
        }

        AppLog.Warn("store", $"{purpose} failed: more than {MaxRedirectHops} redirects");
        return null;
    }

    /// <summary>Queries the Steam store API. The call may only be sent to - and
    /// land on - the Steam store API host, on the initial request and on every
    /// redirect hop; a response that fails any of those checks, any HTTP status
    /// check, or the JSON parse is discarded as a whole and logged with the
    /// reason.</summary>
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
            var url = $"https://{AllowedApiHost}/IStoreBrowseService/GetItems/v1?input_json={encoded}";

            using var resp = await GetFollowingRedirectsAsync(
                url, IsAllowedApiUri, "store API request",
                HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (resp == null)
                return result;

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn("store",
                    $"store API returned HTTP {(int)resp.StatusCode} for {appIds.Count} requested app(s)");
                return result;
            }

            if (!IsAllowedApiUri(resp.RequestMessage?.RequestUri))
            {
                AppLog.Warn("store",
                    $"store API response rejected: landed on \"{resp.RequestMessage?.RequestUri?.Host}\" instead of the Steam store API host");
                return result;
            }

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("response", out var response))
                {
                    AppLog.Warn("store", "store API response had no \"response\" section");
                    return result;
                }
                if (!response.TryGetProperty("store_items", out var items))
                {
                    AppLog.Warn("store", "store API response had no \"store_items\" section");
                    return result;
                }

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
            catch (JsonException ex)
            {
                AppLog.Exception("store", "store API response could not be parsed", ex);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("store", "store API response could not be read", ex);
        }

        return result;
    }

    /// <summary>Loads <c>store_cache.json</c> once. Entries that are missing a
    /// name or a usable header URL are dropped instead of loaded, so a poisoned
    /// cache file self-heals: those apps are refetched on the next lookup and the
    /// rewritten file no longer contains them.</summary>
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
                    {
                        if (!IsComplete(info))
                        {
                            AppLog.Warn("store",
                                $"dropping incomplete store cache entry app={id} (name='{info.Name}', headerUrl={(string.IsNullOrEmpty(info.HeaderUrl) ? "missing" : info.HeaderUrl)}) - it will be fetched again");
                            continue;
                        }
                        _mem.TryAdd(id, info);
                    }
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
