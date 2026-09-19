using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>
/// Native AWS SigV4 client for S3 / S3-compatible endpoints (MinIO, RustFS,
/// Cloudflare R2) using path-style addressing: <c>/{bucket}/{key}</c>.
/// Keeps the existing endpoint/path/key behavior; adds raw GET with ETag,
/// optimistic-concurrency guards on PUT, and DELETE for absent-object rollback.
/// </summary>
public sealed class S3Provider : ICloudProvider, IDisposable
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly S3Credentials _creds;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _basePath;

    public S3Provider(S3Credentials creds)
        : this(creds, null)
    {
    }

    internal S3Provider(S3Credentials creds, HttpMessageHandler? handler)
    {
        _creds = creds;

        string ep = creds.Endpoint;
        if (string.IsNullOrEmpty(ep))
            throw new InvalidOperationException("S3 endpoint is missing in credentials.");
        if (!ep.Contains("://"))
            ep = "https://" + ep;

        _baseUri = new Uri(ep, UriKind.Absolute);
        _basePath = _baseUri.AbsolutePath.TrimEnd('/');
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string DisplayName => "S3 / R2";
    public bool CanWrite => true;

    private string ObjectKey(string accountId)
    {
        string prefix = (_creds.KeyPrefix ?? "").Trim('/');
        if (prefix.Length > 0) prefix += "/";
        return prefix + accountId + "/0/stats.json";
    }

    public async Task<AccountStatsFetch> GetAccountStatsAsync(string accountId)
    {
        var (body, etag) = await SendAsync("GET", accountId, null, null, false).ConfigureAwait(false);
        if (body == null)
            return new AccountStatsFetch { Exists = false };

        JsonObject? parsed = null;
        try
        {
            parsed = JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject;
        }
        catch
        {
            parsed = null;
        }
        return new AccountStatsFetch { Exists = true, Raw = body, ETag = etag, Parsed = parsed };
    }

    public async Task PutAccountStatsAsync(string accountId, byte[] raw,
        string? ifMatchETag = null, bool ifNoneMatchCreate = false)
    {
        await SendAsync("PUT", accountId, raw, ifMatchETag, ifNoneMatchCreate).ConfigureAwait(false);
    }

    public async Task DeleteAccountStatsAsync(string accountId)
    {
        await SendAsync("DELETE", accountId, null, null, false).ConfigureAwait(false);
    }

    private async Task<(byte[]? Body, string? ETag)> SendAsync(string method, string accountId,
        byte[]? body, string? ifMatchETag, bool ifNoneMatchCreate)
    {
        string key = ObjectKey(accountId);
        string requestPath = _basePath + "/" + _creds.Bucket + "/" + AwsEncode(key);

        var now = DateTime.UtcNow;
        string amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        string date = now.ToString("yyyyMMdd");
        string payloadHash = body == null ? EmptySha256 : Sha256Hex(body);

        string host = _baseUri.IsDefaultPort
            ? _baseUri.Host
            : $"{_baseUri.Host}:{_baseUri.Port}";

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (ifMatchETag != null) headers["if-match"] = ifMatchETag;
        if (ifNoneMatchCreate) headers["if-none-match"] = "*";

        var canonicalHeaderLines = new StringBuilder();
        foreach (var (name, value) in headers)
            canonicalHeaderLines.Append(name).Append(':').Append(value).Append('\n');
        string signedHeaders = string.Join(";", headers.Keys);

        string canonicalRequest =
            $"{method}\n{requestPath}\n\n{canonicalHeaderLines}\n{signedHeaders}\n{payloadHash}";

        string scope = $"{date}/{_creds.Region}/s3/aws4_request";
        string stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";
        string signature = ComputeSignature(stringToSign, date);
        string authorization =
            $"AWS4-HMAC-SHA256 Credential={_creds.AccessKeyId}/{scope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        using var req = new HttpRequestMessage(new HttpMethod(method),
            new Uri(_baseUri, requestPath));
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (ifMatchETag != null)
            req.Headers.TryAddWithoutValidation("If-Match", ifMatchETag);
        if (ifNoneMatchCreate)
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");

        if (body != null)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Content = content;
        }

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        string? etag = null;
        if (resp.Headers.TryGetValues("ETag", out var etagValues))
            etag = etagValues.FirstOrDefault();

        if (resp.StatusCode == HttpStatusCode.NotFound && method is "GET" or "DELETE")
            return (null, etag);

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new CloudChangedConcurrentlyException(
                $"S3 {method} {requestPath} failed: preconditions were not met (412).");

        if (!resp.IsSuccessStatusCode)
        {
            string detail = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"S3 {method} {requestPath} failed ({resp.StatusCode}): {Truncate(detail, 300)}");
        }

        if (method == "GET")
            return (await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false), etag);

        return (null, etag);
    }

    private string ComputeSignature(string stringToSign, string date)
    {
        byte[] kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + _creds.SecretAccessKey), date);
        byte[] kRegion = Hmac(kDate, _creds.Region);
        byte[] kService = Hmac(kRegion, "s3");
        byte[] kSigning = Hmac(kService, "aws4_request");
        return Hex(Hmac(kSigning, stringToSign));
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(string data)
        => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    private static string Sha256Hex(byte[] data)
        => Hex(SHA256.HashData(data));

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string AwsEncode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (IsUnreserved(ch))
                sb.Append(ch);
            else
                sb.Append(Uri.EscapeDataString(ch.ToString()));
        }
        return sb.ToString();
    }

    private static bool IsUnreserved(char ch)
        => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '_' or '~' or '/';

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose() => _http.Dispose();
}