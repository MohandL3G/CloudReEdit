using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>Direct filesystem provider for a folder / network drive. ETag is the
/// SHA-256 of the raw bytes, which makes If-Match/If-None-Match implementable.</summary>
public sealed class FolderProvider : ICloudProvider
{
    private readonly string _syncPath;

    public FolderProvider(string syncPath)
    {
        _syncPath = syncPath;
    }

    public string DisplayName => "Folder / network drive";
    public bool CanWrite => true;

    private string BlobPath(string accountId)
        => Path.Combine(_syncPath, accountId, "0", "stats.json");

    public Task<AccountStatsFetch> GetAccountStatsAsync(string accountId)
    {
        var path = BlobPath(accountId);
        if (!File.Exists(path))
            return Task.FromResult(new AccountStatsFetch { Exists = false });

        var raw = File.ReadAllBytes(path);
        JsonObject? parsed = null;
        try
        {
            parsed = JsonNode.Parse(Encoding.UTF8.GetString(raw)) as JsonObject;
        }
        catch
        {
            parsed = null;
        }
        return Task.FromResult(new AccountStatsFetch
        {
            Exists = true,
            Raw = raw,
            ETag = Sha256Hex(raw),
            Parsed = parsed,
        });
    }

    public Task PutAccountStatsAsync(string accountId, byte[] raw,
        string? ifMatchETag = null, bool ifNoneMatchCreate = false)
    {
        var path = BlobPath(accountId);
        if (ifNoneMatchCreate && File.Exists(path))
            throw new CloudChangedConcurrentlyException("Folder blob already exists (If-None-Match).");
        if (ifMatchETag != null)
        {
            if (!File.Exists(path))
                throw new CloudChangedConcurrentlyException("Folder blob is missing (If-Match).");
            if (Sha256Hex(File.ReadAllBytes(path)) != ifMatchETag)
                throw new CloudChangedConcurrentlyException("Folder blob changed concurrently (If-Match).");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileUtils.AtomicWriteAllBytes(path, raw);
        return Task.CompletedTask;
    }

    public Task DeleteAccountStatsAsync(string accountId)
    {
        var path = BlobPath(accountId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}