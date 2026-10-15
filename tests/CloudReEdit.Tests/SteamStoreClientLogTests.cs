using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CloudReEdit.Services;
using Xunit;
using static CloudReEdit.Tests.SteamStoreFixtures;

namespace CloudReEdit.Tests;

/// <summary>Verifies every store-lookup failure reaches the AppLog with the reason
/// that explains it - rejected host, HTTP status, or parse error - instead of
/// failing silently. Shares the "AppLog" collection with <see cref="AppLogTests"/>
/// because <see cref="AppLog"/> is a process-wide singleton: the two classes must
/// never initialize/shutdown the logger at the same time.</summary>
[Collection("AppLog")]
public class SteamStoreClientLogTests : IDisposable
{
    private readonly string _logDir = Path.Combine(
        Path.GetTempPath(), "CloudReEdit_StoreLog_" + Guid.NewGuid().ToString("N"));

    public SteamStoreClientLogTests()
    {
        AppLog.Shutdown();
        AppLog.InitAt(_logDir);
    }

    public void Dispose()
    {
        AppLog.Shutdown();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string ReadLog()
        => string.Join("\n", Directory.GetFiles(_logDir).Select(File.ReadAllText));

    [Fact]
    public async Task ApiHttpFailure_LogsTheStatusCode()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req =>
            Respond(HttpStatusCode.TooManyRequests, finalUri: req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
        Assert.Contains("HTTP 429", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiRedirectOffTheAllowlist_LogsTheRejectedHost()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ => Redirect(new Uri("https://attacker.example/steal")));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
        Assert.Equal(1, handler.Calls);

        var log = ReadLog();
        Assert.Contains("redirect rejected", log, StringComparison.Ordinal);
        Assert.Contains("attacker.example", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiMalformedJson_LogsTheParseFailure()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req =>
            Respond("<html>not json</html>", finalUri: req.RequestUri));
        using var client = env.NewClient(handler);

        var result = await client.GetAppInfoAsync(new uint[] { 440 });

        Assert.Empty(result);
        Assert.Contains("could not be parsed", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageRedirectOffTheAllowlist_LogsTheRejectedHost()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(_ => Redirect(new Uri("https://attacker.example/steal")));
        using var client = env.NewClient(handler);

        await client.DownloadImageToCacheAsync(CdnUrl,
            Path.Combine(env.ImageCacheDir, "header.jpg"));

        Assert.Equal(1, handler.Calls);

        var log = ReadLog();
        Assert.Contains("image download redirect rejected", log, StringComparison.Ordinal);
        Assert.Contains("attacker.example", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageOffAllowlistStartUrl_LogsTheRejectedUrl()
    {
        using var env = new TempEnv();
        var handler = new StubHandler(req => Respond(PngBytes(), "image/png", req.RequestUri));
        using var client = env.NewClient(handler);

        await client.DownloadImageToCacheAsync("https://attacker.example/x.jpg",
            Path.Combine(env.ImageCacheDir, "header.jpg"));

        Assert.Equal(0, handler.Calls);
        Assert.Contains("image download rejected", ReadLog(), StringComparison.Ordinal);
    }
}
