using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>Read-only stand-in for providers CloudReEdit can't write to yet
/// (gdrive/onedrive via OAuth, local-only). Keeps the pipeline uniform.</summary>
public sealed class UnsupportedProvider : ICloudProvider
{
    private readonly string _displayName;

    public UnsupportedProvider(string displayName) => _displayName = displayName;

    public string DisplayName => _displayName;
    public bool CanWrite => false;

    public Task<JsonObject?> GetAccountStatsAsync(string accountId)
        => Task.FromResult<JsonObject?>(null);

    public Task PutAccountStatsAsync(string accountId, JsonObject stats)
        => throw new NotSupportedException(
            $"Cloud write is not supported for {_displayName} in CloudReEdit yet.");
}
