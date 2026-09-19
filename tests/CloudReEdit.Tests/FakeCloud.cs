using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Tests;

/// <summary>In-memory ICloudProvider whose behavior tests can rewire between
/// pre-flight (GET) and the write/classify (PUT/GET) phases, so the transaction's
/// concurrency classification can be exercised deterministically.</summary>
internal sealed class FakeCloud : ICloudProvider
{
    public byte[]? Blob;
    public bool ThrowGet;
    public Func<byte[]?, Exception?>? PutBehavior;
    public bool DropPut;

    public int GetCalls;
    public int PutCalls;
    public bool DeleteCalled;
    public string? LastIfMatch;
    public bool LastIfNoneMatch;
    public byte[]? LastPutRaw;

    public string DisplayName => "FakeCloud";
    public bool CanWrite => true;

    public Task<AccountStatsFetch> GetAccountStatsAsync(string accountId)
    {
        GetCalls++;
        if (ThrowGet) throw new InvalidOperationException("fake GET failure");
        if (Blob == null) return Task.FromResult(new AccountStatsFetch { Exists = false });
        JsonObject? parsed = null;
        try { parsed = JsonNode.Parse(Encoding.UTF8.GetString(Blob)) as JsonObject; } catch { }
        return Task.FromResult(new AccountStatsFetch
        {
            Exists = true,
            Raw = Blob,
            ETag = Sha(Blob),
            Parsed = parsed,
        });
    }

    public Task PutAccountStatsAsync(string accountId, byte[] raw,
        string? ifMatchETag = null, bool ifNoneMatchCreate = false)
    {
        PutCalls++;
        LastIfMatch = ifMatchETag;
        LastIfNoneMatch = ifNoneMatchCreate;

        if (PutBehavior != null)
        {
            var ex = PutBehavior(Blob);
            if (ex != null) throw ex;
        }

        if (ifNoneMatchCreate && Blob != null)
            throw new CloudChangedConcurrentlyException("fake If-None-Match failed");
        if (ifMatchETag != null && (Blob == null || Sha(Blob) != ifMatchETag))
            throw new CloudChangedConcurrentlyException("fake If-Match failed");

        if (DropPut)
            return Task.CompletedTask;

        Blob = raw;
        LastPutRaw = raw;
        return Task.CompletedTask;
    }

    public Task DeleteAccountStatsAsync(string accountId)
    {
        DeleteCalled = true;
        Blob = null;
        return Task.CompletedTask;
    }

    public static string Sha(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}