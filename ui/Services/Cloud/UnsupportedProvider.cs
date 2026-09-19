using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>Read-only stand-in for providers CloudReEdit can't write to yet
/// (gdrive/onedrive via OAuth, local-only). Keeps the pipeline uniform and never
/// pretends it wrote anything.</summary>
public sealed class UnsupportedProvider : ICloudProvider
{
    private readonly string _displayName;

    public UnsupportedProvider(string displayName) => _displayName = displayName;

    public string DisplayName => _displayName;
    public bool CanWrite => false;

    public Task<AccountStatsFetch> GetAccountStatsAsync(string accountId)
        => Task.FromResult(new AccountStatsFetch { Exists = false });

    public Task PutAccountStatsAsync(string accountId, byte[] raw,
        string? ifMatchETag = null, bool ifNoneMatchCreate = false)
        => throw new NotSupportedException(
            $"Cloud write is not supported for {_displayName} in CloudReEdit yet.");

    public Task DeleteAccountStatsAsync(string accountId)
        => throw new NotSupportedException(
            $"Cloud delete is not supported for {_displayName} in CloudReEdit yet.");
}