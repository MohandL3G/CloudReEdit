using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CloudReEdit.Services;
using Xunit;
using static CloudReEdit.Tests.SteamStoreFixtures;

namespace CloudReEdit.Tests;

public class SteamStoreClientTests
{
    // ---------------- URL allowlist ----------------

    [Theory]
    [InlineData("https://shared.steamstatic.com/store_item_assets/steam/apps/1/header.jpg", true)]
    [InlineData("https://cdn.cloudflare.steamstatic.com/x.jpg", true)]
    [InlineData("https://cdn.akamai.steamstatic.com/steam/apps/1/header.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/steam/apps/1/header.jpg", false)]
    [InlineData("https://shared.steampowered.com/x.jpg", true)]
    [InlineData("https://api.steampowered.com/IStoreBrowseService/GetItems/v1", true)]
    [InlineData("http://shared.steamstatic.com/x.jpg", false)]
    [InlineData("https://evilsteamstatic.com/x.jpg", false)]
    [InlineData("https://steamstatic.com.attacker.example/x.jpg", false)]
    [InlineData("https://attacker.example/.steamstatic.com/x.jpg", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    [InlineData("ftp://shared.steamstatic.com/x.jpg", false)]
    public void IsValidSteamCdnUrl_ChecksSchemeAndHost(string url, bool expected)
    {
        Assert.Equal(expected, SteamStoreClient.IsValidSteamCdnUrl(url));
    }

    [Fact]
    public void IsAllowedResponseUri_ChecksSchemeAndHostOfTheFinalUri()
    {
        Assert.True(SteamStoreClient.IsAllowedResponseUri(
            new Uri("https://api.steampowered.com/IStoreBrowseService/GetItems/v1")));
        Assert.True(SteamStoreClient.IsAllowedResponseUri(new Uri(CdnUrl)));
        Assert.False(SteamStoreClient.IsAllowedResponseUri(new Uri("https://attacker.example/steal")));
        Assert.False(SteamStoreClient.IsAllowedResponseUri(new Uri("http://shared.steamstatic.com/x.jpg")));
        Assert.False(SteamStoreClient.IsAllowedResponseUri(null));
    }

    // ---------------- per-purpose API allowlist ----------------

    [Theory]
    [InlineData("https://api.steampowered.com/IStoreBrowseService/GetItems/v1", true)]
    [InlineData("https://api.steampowered.com/?input_json=%7B%7D", true)]
    [InlineData("https://shared.steamstatic.com/store_item_assets/steam/apps/1/header.jpg", false)]
    [InlineData("https://shared.steampowered.com/x.jpg", false)]
    [InlineData("https://store.steampowered.com/app/440", false)]
    [InlineData("https://partner.steampowered.com/x", false)]
    [InlineData("https://api.steampowered.com.evil.example/x", false)]
    [InlineData("https://steam-api.evil.example/x", false)]
    [InlineData("http://api.steampowered.com/x", false)]
    [InlineData(null, false)]
    public void IsAllowedApiUri_AllowsOnlyTheSteamStoreApiHost(string? url, bool expected)
    {
        Assert.Equal(expected, SteamStoreClient.IsAllowedApiUri(url == null ? null : new Uri(url)));
    }

    // ---------------- static initialization (regression for the 0.2.7-pre-release
    // Games-page bug: Shared was built before the static path fields, so the
    // singleton ran with null paths) ----------------

    [Fact]
    public void SharedSingleton_ResolvesItsCachePathsDuringConstruction()
    {
        var type = typeof(SteamStoreClient);
        var cachePathField = type.GetField("_cachePath", BindingFlags.NonPublic | BindingFlags.Instance);
        var imageDirField = type.GetField("_imageCacheDir", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cachePathField);
        Assert.NotNull(imageDirField);

        var shared = SteamStoreClient.Shared;
        var cachePath = (string?)cachePathField!.GetValue(shared);
        var imageDir = (string?)imageDirField!.GetValue(shared);

        Assert.False(string.IsNullOrEmpty(cachePath));
        Assert.EndsWith("store_cache.json", cachePath!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(imageDir));
        Assert.EndsWith("store_images", imageDir!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- image magic bytes ----------------

    [Fact]
    public void LooksLikeImage_AcceptsJpegPngAndWebp()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        byte[] webp = Encoding.ASCII.GetBytes("RIFF").Concat(new byte[] { 0x10, 0x00, 0x00, 0x00 })
            .Concat(Encoding.ASCII.GetBytes("WEBPVP8 ")).ToArray();

        Assert.True(SteamStoreClient.LooksLikeImage(jpeg));
        Assert.True(SteamStoreClient.LooksLikeImage(png));
        Assert.True(SteamStoreClient.LooksLikeImage(webp));
    }

    [Fact]
    public void LooksLikeImage_RejectsHtmlAndTruncatedInput()
    {
        Assert.False(SteamStoreClient.LooksLikeImage(Encoding.UTF8.GetBytes("<html>not an image</html>")));
        Assert.False(SteamStoreClient.LooksLikeImage(Encoding.UTF8.GetBytes("RIFF")));
        Assert.False(SteamStoreClient.LooksLikeImage("RIFF....WEB"u8));
        Assert.False(SteamStoreClient.LooksLikeImage(ReadOnlySpan<byte>.Empty));
    }

    // ---------------- store API responses ----------------

    [Fact]
    public async Task StoreApi_ResponseLandingOnAllowedHost_IsUsed()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req =>
            Respond(ApiOkBody(), finalUri: req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.True(result.ContainsKey(440));
        Assert.Equal("Team Fortress 2", result[440].Name);
    }

    [Fact]
    public async Task StoreApi_ResponseRedirectedOffTheAllowlist_IsDiscarded()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ =>
            Respond(ApiOkBody(), finalUri: new Uri("https://attacker.example/steal")));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
    }

    [Fact]
    public async Task StoreApi_RedirectWithinTheApiHost_IsFollowed()
    {
        using var env = new TempEnv();
        int call = 0;
        var handler = new StubHandler(req =>
        {
            call++;
            return call == 1
                ? Redirect(new Uri("https://api.steampowered.com/IStoreBrowseService/GetItems/v1"))
                : Respond(ApiOkBody(), finalUri: req.RequestUri);
        });
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Equal(2, handler.Calls);
        Assert.Equal("Team Fortress 2", result[440].Name);
    }

    [Fact]
    public async Task StoreApi_RedirectToACdnHost_IsRejectedBeforeItIsRequested()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ =>
            Redirect(new Uri("https://shared.steamstatic.com/store_item_assets/steal")));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StoreApi_RedirectChainWithinTheApiHost_IsFollowed()
    {
        using var env = new TempEnv();
        int call = 0;
        var handler = new StubHandler(req =>
        {
            call++;
            return call switch
            {
                1 => Redirect(new Uri("https://api.steampowered.com/step1")),
                2 => Redirect(new Uri("https://api.steampowered.com/step2")),
                _ => Respond(ApiOkBody(), finalUri: req.RequestUri),
            };
        });
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Equal(3, handler.Calls);
        Assert.Equal("Team Fortress 2", result[440].Name);
    }

    [Fact]
    public async Task StoreApi_RedirectChainThatLeavesTheAllowlist_IsRejectedMidChain()
    {
        using var env = new TempEnv();
        int call = 0;
        var handler = new StubHandler(_ =>
        {
            call++;
            return call == 1
                ? Redirect(new Uri("https://api.steampowered.com/step1"))
                : Redirect(new Uri("https://attacker.example/steal"));
        });
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
        Assert.Equal(2, handler.Calls);
    }

    // ---------------- image downloads ----------------

    [Fact]
    public async Task ImageDownload_RealPng_IsCached()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        var target = Path.Combine(env.ImageCacheDir, "header.jpg");
        await client.DownloadImageToCacheAsync(CdnUrl, target);

        Assert.True(File.Exists(target));
        Assert.Equal(PngBytes(), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ImageDownload_NonImageBytes_AreDiscarded()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req =>
            Respond(Encoding.UTF8.GetBytes("<html>captive portal</html>"), "image/jpeg", req.RequestUri));
        using var client = env.NewClient(handler);

        var target = Path.Combine(env.ImageCacheDir, "header.jpg");
        await client.DownloadImageToCacheAsync(CdnUrl, target);

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(env.ImageCacheDir, "*.tmp"));
    }

    [Fact]
    public async Task ImageDownload_RedirectedOffTheAllowlist_IsDiscarded()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ =>
            Respond(PngBytes(), "image/png", new Uri("https://attacker.example/steal")));
        using var client = env.NewClient(handler);

        var target = Path.Combine(env.ImageCacheDir, "header.jpg");
        await client.DownloadImageToCacheAsync(CdnUrl, target);

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ImageDownload_DisallowedStartUrl_NeverHitsTheNetwork()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        await client.DownloadImageToCacheAsync("http://shared.steamstatic.com/x.jpg",
            Path.Combine(env.ImageCacheDir, "header.jpg"));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ImageDownload_RedirectBetweenCdnHosts_IsFollowed()
    {
        using var env = new TempEnv();
        int call = 0;
        var handler = new StubHandler(req =>
        {
            call++;
            return call == 1
                ? Redirect(new Uri("https://cdn.cloudflare.steamstatic.com/steam/apps/440/header.jpg"))
                : Respond(PngBytes(), "image/png", req.RequestUri);
        });
        using var client = env.NewClient(handler);

        var target = Path.Combine(env.ImageCacheDir, "header.jpg");
        await client.DownloadImageToCacheAsync(CdnUrl, target);

        Assert.Equal(2, handler.Calls);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ImageDownload_RedirectOffTheAllowlist_NeverRequestsTheTarget()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ => Redirect(new Uri("https://attacker.example/steal")));
        using var client = env.NewClient(handler);

        var target = Path.Combine(env.ImageCacheDir, "header.jpg");
        await client.DownloadImageToCacheAsync(CdnUrl, target);

        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(target));
    }

    // ---------------- store_cache.json ----------------

    [Fact]
    public async Task ConcurrentCacheSaves_ProduceOneValidFileWithoutTempLeftovers()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.Contains("/IStoreBrowseService/")
            ? Respond(ApiBodyWithHeader(), finalUri: req.RequestUri)
            : Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        var fetched = await client.GetAppInfoAsync(new uint[] { 440 });
        Assert.Equal("Team Fortress 2", fetched[440].Name);

        await WaitForCachedImage(env.ImageCacheDir);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.SaveDiskCacheAsync()));

        var json = await File.ReadAllTextAsync(env.CachePath);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries));
        Assert.Equal(JsonValueKind.Object, entries.ValueKind);
        Assert.True(entries.TryGetProperty("440", out var entry));
        Assert.Equal("Team Fortress 2", entry.GetProperty("name").GetString());
        Assert.False(File.Exists(env.CachePath + ".tmp"));
    }

    [Fact]
    public async Task CacheSaves_KeepTheSharedSchemaRoundTrippable()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.Contains("/IStoreBrowseService/")
            ? Respond(ApiBodyWithHeader(), finalUri: req.RequestUri)
            : Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        var fetched = await client.GetAppInfoAsync(new uint[] { 440 });
        Assert.StartsWith("https://shared.steamstatic.com/", fetched[440].HeaderUrl,
            StringComparison.Ordinal);

        await WaitForCachedImage(env.ImageCacheDir);
        await client.SaveDiskCacheAsync();

        var json = await File.ReadAllTextAsync(env.CachePath);
        Assert.Contains("\"headerUrl\"", json, StringComparison.Ordinal);
        var parsed = JsonSerializer.Deserialize(json, StoreCacheJsonContext.Default.StoreCache);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Entries);
        Assert.True(parsed.Entries.ContainsKey(440));
        Assert.Equal("Team Fortress 2", parsed.Entries[440].Name);
        Assert.False(string.IsNullOrEmpty(parsed.Entries[440].HeaderUrl));
    }

    [Fact]
    public async Task CacheFileIn026Format_LoadsFreshCompleteEntriesWithoutAnApiCall()
    {
        using var env = new TempEnv();
        Directory.CreateDirectory(env.Root);

        var fresh = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);
        File.WriteAllText(env.CachePath,
            "{\"entries\":{\"440\":{\"name\":\"Team Fortress 2\",\"headerUrl\":\"" + CdnUrl +
            "\",\"fetchedUtc\":\"" + fresh + "\"}}}");

        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.Contains("/IStoreBrowseService/")
            ? Respond(ApiOkBody(), finalUri: req.RequestUri)
            : Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Equal("Team Fortress 2", result[440].Name);
        Assert.Equal(0, handler.ApiCalls);
        await WaitForCachedImage(env.ImageCacheDir);
    }

    [Fact]
    public async Task IncompleteCacheEntries_AreRefetchedAndDroppedFromTheCacheFile()
    {
        using var env = new TempEnv();
        Directory.CreateDirectory(env.Root);

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);
        File.WriteAllText(env.CachePath,
            "{\"entries\":{" +
            "\"0\":{\"name\":\"\",\"fetchedUtc\":\"2026-06-12T15:10:32.2876917Z\"}," +
            "\"570\":{\"name\":\"Stale Name\",\"fetchedUtc\":\"" + now + "\"}}}");

        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.Contains("/IStoreBrowseService/")
            ? Respond(RefetchedBody(), finalUri: req.RequestUri)
            : Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 0, 570 });

        Assert.Equal(1, handler.ApiCalls);
        Assert.Equal("Left 4 Dead 2", result[570].Name);
        Assert.Equal("", result[0].Name);

        await WaitForCachedImage(env.ImageCacheDir);
        await client.SaveDiskCacheAsync();

        var parsed = JsonSerializer.Deserialize(await File.ReadAllTextAsync(env.CachePath),
            StoreCacheJsonContext.Default.StoreCache);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Entries);
        Assert.Equal(new uint[] { 440 }, parsed.Entries.Keys.ToArray());
        Assert.Equal("Team Fortress 2", parsed.Entries[440].Name);
    }

    [Fact]
    public async Task FetchedEntryWithoutHeader_IsShownButNeverPersisted()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => Respond(ApiOkBody(), finalUri: req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });
        Assert.Equal("Team Fortress 2", result[440].Name);

        await client.SaveDiskCacheAsync();

        var parsed = JsonSerializer.Deserialize(await File.ReadAllTextAsync(env.CachePath),
            StoreCacheJsonContext.Default.StoreCache);

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Entries);
    }
}
