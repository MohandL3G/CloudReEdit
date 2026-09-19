using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudReEdit.Services.Cloud;
using Xunit;

namespace CloudReEdit.Tests;

public class S3ProviderTests
{
    private const string AccountId = "76561198000000000";

    private static S3Credentials Creds() => new()
    {
        AccessKeyId = "AK",
        SecretAccessKey = "SK",
        Bucket = "bkt",
        KeyPrefix = "cr",
        Endpoint = "https://minio.local:9000",
        Region = "us-east-1",
    };

    [Fact]
    public async Task Get_NotFound_ReportsNotExists()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var provider = new S3Provider(Creds(), handler);

        var fetch = await provider.GetAccountStatsAsync(AccountId);

        Assert.False(fetch.Exists);
        Assert.Equal("/bkt/cr/76561198000000000/0/stats.json", handler.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Get_Found_ReturnsRawEtagAndParsed()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"x\":1}");
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"");
            return resp;
        });
        using var provider = new S3Provider(Creds(), handler);

        var fetch = await provider.GetAccountStatsAsync(AccountId);

        Assert.True(fetch.Exists);
        Assert.Equal(body, fetch.Raw);
        Assert.Equal("\"e1\"", fetch.ETag);
        Assert.Equal(1, fetch.Parsed!["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task Put_IfNoneMatchRejected_ThrowsConcurrent()
    {
        var handler = new StubHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            if (req.Headers.TryGetValues("If-None-Match", out var v) && v.Contains("*"))
                resp.StatusCode = HttpStatusCode.PreconditionFailed;
            return resp;
        });
        using var provider = new S3Provider(Creds(), handler);

        await Assert.ThrowsAsync<CloudChangedConcurrentlyException>(
            () => provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"x\":2}"),
                ifNoneMatchCreate: true));
    }

    [Fact]
    public async Task Put_IfMatchRejected_ThrowsConcurrent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        using var provider = new S3Provider(Creds(), handler);

        await Assert.ThrowsAsync<CloudChangedConcurrentlyException>(
            () => provider.PutAccountStatsAsync(AccountId, Encoding.UTF8.GetBytes("{\"x\":2}"),
                ifMatchETag: "\"e1\""));
    }

    [Fact]
    public async Task Put_Success_SendsBodyAndSignsConditions()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"x\":2}");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var provider = new S3Provider(Creds(), handler);

        await provider.PutAccountStatsAsync(AccountId, body, ifMatchETag: "\"e1\"");

        Assert.Equal("{\"x\":2}", Encoding.UTF8.GetString(handler.LastBody!));
        Assert.Equal("\"e1\"", handler.Last!.Headers.GetValues("If-Match")!.Single());
        string auth = handler.Last.Headers.GetValues("Authorization").Single();
        Assert.Contains("Credential=AK/", auth);
        Assert.Contains("SignedHeaders=host;if-match;x-amz-content-sha256;x-amz-date", auth);
        string sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        Assert.Equal(sha, handler.Last.Headers.GetValues("x-amz-content-sha256").Single());
    }

    [Fact]
    public async Task Delete_NotFound_TreatedAsOk()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var provider = new S3Provider(Creds(), handler);

        await provider.DeleteAccountStatsAsync(AccountId);

        Assert.Equal(HttpMethod.Delete, handler.Last!.Method);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? Last;
        public byte[]? LastBody;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            if (request.Content != null)
                LastBody = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(_respond(request));
        }
    }
}