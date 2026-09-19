using System.IO;
using System.Text;
using System.Threading.Tasks;
using CloudReEdit.Services.Cloud;
using Xunit;

namespace CloudReEdit.Tests;

public class FolderProviderTests
{
    private const string AccountId = "76561198000000000";

    [Fact]
    public async Task Get_WhenAbsent_ReportsNotExists()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);

        var fetch = await provider.GetAccountStatsAsync(AccountId);

        Assert.False(fetch.Exists);
        Assert.Null(fetch.Raw);
    }

    [Fact]
    public async Task PutThenGet_RoundTripsRawBytesAndEtag()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);
        byte[] raw = Encoding.UTF8.GetBytes("{\"a\":1}");

        await provider.PutAccountStatsAsync(AccountId, raw);
        var fetch = await provider.GetAccountStatsAsync(AccountId);

        Assert.True(fetch.Exists);
        Assert.Equal(raw, fetch.Raw);
        Assert.Equal(CloudReEdit.Tests.FakeCloud.Sha(raw), fetch.ETag);
        Assert.NotNull(fetch.Parsed);
    }

    [Fact]
    public async Task Put_IfNoneMatch_WhenObjectExists_ThrowsConcurrent()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);
        await provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":1}"));

        await Assert.ThrowsAsync<CloudChangedConcurrentlyException>(
            () => provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":2}"),
                ifNoneMatchCreate: true));
    }

    [Fact]
    public async Task Put_IfMatch_WhenEtagMismatches_ThrowsConcurrent()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);
        await provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":1}"));

        await Assert.ThrowsAsync<CloudChangedConcurrentlyException>(
            () => provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":2}"),
                ifMatchETag: "deadbeef"));
    }

    [Fact]
    public async Task Put_IfMatch_WhenEtagMatches_Writes()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);
        byte[] first = Encoding.UTF8.GetBytes("{\"a\":1}");
        await provider.PutAccountStatsAsync(AccountId, first);
        string etag = CloudReEdit.Tests.FakeCloud.Sha(first);

        await provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":2}"), ifMatchETag: etag);

        var fetch = await provider.GetAccountStatsAsync(AccountId);
        Assert.Equal(2, fetch.Parsed!["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task Delete_RemovesObject_AndIsIdempotent()
    {
        using var env = Env.New();
        var provider = new FolderProvider(env.SyncPath);
        await provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"a\":1}"));

        await provider.DeleteAccountStatsAsync(AccountId);
        Assert.False((await provider.GetAccountStatsAsync(AccountId)).Exists);

        await provider.DeleteAccountStatsAsync(AccountId);
        Assert.False((await provider.GetAccountStatsAsync(AccountId)).Exists);
    }

    private sealed class Env : System.IDisposable
    {
        public string Root { get; private set; } = "";
        public string SyncPath => Path.Combine(Root, "sync");

        public static Env New()
        {
            var env = new Env
            {
                Root = Path.Combine(Path.GetTempPath(), "CloudReEdit_FolderTest_" + System.Guid.NewGuid().ToString("N")),
            };
            Directory.CreateDirectory(env.SyncPath);
            return env;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}