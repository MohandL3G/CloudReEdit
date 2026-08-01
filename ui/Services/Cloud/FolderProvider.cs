using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>Direct filesystem provider for a folder / network drive.</summary>
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

    public Task<JsonObject?> GetAccountStatsAsync(string accountId)
    {
        var path = BlobPath(accountId);
        if (!File.Exists(path)) return Task.FromResult<JsonObject?>(null);

        try
        {
            var text = File.ReadAllText(path);
            return Task.FromResult(JsonNode.Parse(text) as JsonObject);
        }
        catch
        {
            return Task.FromResult<JsonObject?>(null);
        }
    }

    public Task PutAccountStatsAsync(string accountId, JsonObject stats)
    {
        var path = BlobPath(accountId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileUtils.AtomicWriteAllText(path, stats.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }));
        return Task.CompletedTask;
    }
}
