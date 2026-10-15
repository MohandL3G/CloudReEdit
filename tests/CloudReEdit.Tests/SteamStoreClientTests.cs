using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class SteamStoreClientTests
{
    private const string CdnUrl = "https://shared.steamstatic.com/store_item_assets/steam/apps/440/header.jpg";

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
    [InlineData("https://example.com/x.jpg", false)]
    [InlineData("ftp://shared.steamstatic.com/x.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidSteamCdnUrl_OnlyAllowsHttpsOnSteamHosts(string? url, bool expected)
        => Assert.Equal(expected, SteamStoreClient.IsValidSteamCdnUrl(url));

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

    // ---------------- store_cache.json ----------------

    [Fact]
    public async Task ConcurrentCacheSaves_ProduceOneValidFileWithoutTempLeftovers()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => Respond(ApiOkBody(), finalUri: req.RequestUri));
        using var client = env.NewClient(handler);

        var fetched = await client.GetAppInfoAsync(new uint[] { 440 });
        Assert.Equal("Team Fortress 2", fetched[440].Name);

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
        var parsed = System.Text.Json.JsonSerializer.Deserialize(json,
            CloudReEdit.Services.StoreCacheJsonContext.Default.StoreCache);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Entries);
        Assert.True(parsed.Entries.ContainsKey(440));
        Assert.Equal("Team Fortress 2", parsed.Entries[440].Name);
        Assert.False(string.IsNullOrEmpty(parsed.Entries[440].HeaderUrl));
    }

    // ---------------- helpers ----------------

    /// <summary>Store API body without assets: the entry carries no header image,
    /// so no background image download races the test.</summary>
    private static string ApiOkBody()
        => "{\"response\":{\"store_items\":[{\"appid\":440,\"name\":\"Team Fortress 2\"}]}}";

    private static string ApiBodyWithHeader()
        => "{\"response\":{\"store_items\":[{\"appid\":440,\"name\":\"Team Fortress 2\"," +
           "\"assets\":{\"header\":\"header.jpg\"}}]}}";

    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };

    /// <summary>Waits for the fire-and-forget header download to publish its cached
    /// file, so the temp directory is complete (and deletable) when the test ends.</summary>
    private static async Task WaitForCachedImage(string imageDir)
    {
        for (int i = 0; i < 100; i++)
        {
            if (Directory.Exists(imageDir) && Directory.GetFiles(imageDir, "*.jpg").Length > 0)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"no cached image appeared under \"{imageDir}\"");
    }

    /// <summary>Builds a response whose RequestMessage carries the URI the response
    /// actually landed on - with a stub handler that stands in for the final URI
    /// HttpClient would report after following redirects.</summary>
    private static HttpResponseMessage Respond(byte[] body, string contentType, Uri? finalUri)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (finalUri != null)
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        return resp;
    }

    private static HttpResponseMessage Respond(string body, Uri? finalUri)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (finalUri != null)
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        return resp;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private int _calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var resp = _respond(request);
            resp.RequestMessage ??= request;
            return Task.FromResult(resp);
        }
    }

    private sealed class TempEnv : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "CloudReEdit_Store_" + Guid.NewGuid().ToString("N"));

        public string CachePath => Path.Combine(Root, "store_cache.json");
        public string ImageCacheDir => Path.Combine(Root, "store_images");

        public SteamStoreClient NewClient(HttpMessageHandler handler)
        {
            Directory.CreateDirectory(Root);
            return new SteamStoreClient(handler, CachePath, ImageCacheDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
