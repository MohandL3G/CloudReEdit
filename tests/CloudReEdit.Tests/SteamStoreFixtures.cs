using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CloudReEdit.Tests;

/// <summary>Shared fixtures for <c>SteamStoreClient</c> tests: a stub message
/// handler that scripts responses (including redirect hops) and a temp cache
/// environment, so no test ever touches the real CloudRedirect AppData folder.</summary>
internal static class SteamStoreFixtures
{
    public const string CdnUrl =
        "https://shared.steamstatic.com/store_item_assets/steam/apps/440/header.jpg";

    /// <summary>Store API body without assets: the entry carries no header image,
    /// so no background image download races the test.</summary>
    public static string ApiOkBody()
        => "{\"response\":{\"store_items\":[{\"appid\":440,\"name\":\"Team Fortress 2\"}]}}";

    public static string ApiBodyWithHeader()
        => "{\"response\":{\"store_items\":[{\"appid\":440,\"name\":\"Team Fortress 2\"," +
           "\"assets\":{\"header\":\"header.jpg\"}}]}}";

    /// <summary>Store API body covering all three shapes a poisoned cache can be
    /// refetched into: a nameless item, an item without assets, and a complete one.</summary>
    public static string RefetchedBody()
        => "{\"response\":{\"store_items\":[" +
           "{\"appid\":0}," +
           "{\"appid\":570,\"name\":\"Left 4 Dead 2\"}," +
           "{\"appid\":440,\"name\":\"Team Fortress 2\",\"assets\":{\"header\":\"header.jpg\"}}" +
           "]}}";

    public static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };

    /// <summary>Builds a response whose RequestMessage carries the URI the response
    /// actually landed on - with a stub handler that stands in for the final URI
    /// HttpClient would report after following redirects.</summary>
    public static HttpResponseMessage Respond(byte[] body, string contentType, Uri? finalUri)
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

    public static HttpResponseMessage Respond(string body, Uri? finalUri)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (finalUri != null)
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        return resp;
    }

    public static HttpResponseMessage Respond(HttpStatusCode statusCode, Uri? finalUri)
    {
        var resp = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain"),
        };
        if (finalUri != null)
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        return resp;
    }

    public static HttpResponseMessage Redirect(Uri location)
        => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = location } };

    /// <summary>Waits for the fire-and-forget header download to publish its cached
    /// file, so the temp directory is complete (and deletable) when the test ends.</summary>
    public static async Task WaitForCachedImage(string imageDir)
    {
        for (int i = 0; i < 100; i++)
        {
            if (Directory.Exists(imageDir) && Directory.GetFiles(imageDir, "*.jpg").Length > 0)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"no cached image appeared under \"{imageDir}\"");
    }
}

/// <summary>Scriptable <see cref="HttpMessageHandler"/>: never follows redirects on
/// its own (the client does that, one validated hop at a time), counts total and
/// store-API requests separately.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private int _calls;
    private int _apiCalls;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    public int Calls => _calls;
    public int ApiCalls => _apiCalls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (request.RequestUri!.AbsolutePath.Contains("/IStoreBrowseService/"))
            Interlocked.Increment(ref _apiCalls);
        var resp = _respond(request);
        resp.RequestMessage ??= request;
        return Task.FromResult(resp);
    }
}

/// <summary>Temp cache root + paths, mirroring the client's CloudRedirect layout
/// without touching the real AppData folder.</summary>
internal sealed class TempEnv : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "CloudReEdit_Store_" + Guid.NewGuid().ToString("N"));

    public string CachePath => Path.Combine(Root, "store_cache.json");
    public string ImageCacheDir => Path.Combine(Root, "store_images");

    public CloudReEdit.Services.SteamStoreClient NewClient(HttpMessageHandler handler)
    {
        Directory.CreateDirectory(Root);
        return new CloudReEdit.Services.SteamStoreClient(handler, CachePath, ImageCacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
