using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CloudReEdit.Services.Cloud;

/// <summary>Result of a cloud GET: whether the object exists, its exact raw bytes,
/// an optimistic-concurrency token (ETag when available) and a best-effort parse.
/// <see cref="Exists"/> distinguishes "absent" from "present but malformed".</summary>
public sealed class AccountStatsFetch
{
    public bool Exists { get; init; }
    public byte[]? Raw { get; init; }
    public string? ETag { get; init; }
    public JsonObject? Parsed { get; init; }
}

/// <summary>Thrown when a cloud precondition (If-Match / If-None-Match) fails:
/// the object changed concurrently, so the write was rejected without mutating it.</summary>
public sealed class CloudChangedConcurrentlyException : InvalidOperationException
{
    public CloudChangedConcurrentlyException(string message) : base(message) { }
    public CloudChangedConcurrentlyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Read/write access to a CloudRedirect account-scope stats blob
/// (<c>{accountId}/0/stats.json</c>), which maps appId string -> app stats JSON.
/// </summary>
public interface ICloudProvider
{
    string DisplayName { get; }
    bool CanWrite { get; }

    /// <summary>Returns the account blob with its exact raw bytes, or
    /// <see cref="AccountStatsFetch.Exists"/> == false when it doesn't exist yet.
    /// 4xx auth / network / precondition failures are surfaced as exceptions.</summary>
    Task<AccountStatsFetch> GetAccountStatsAsync(string accountId);

    /// <summary>Uploads/replaces the account blob from exact raw bytes. The two
    /// conditionals are optimistic-concurrency guards used when present:
    /// <c>ifMatchETag</c> for existing objects, <c>ifNoneMatchCreate</c> when the
    /// object was absent. Implementations are allowed to ignore conditions on
    /// providers that don't support them, so post-write verification stays mandatory.</summary>
    Task PutAccountStatsAsync(string accountId, byte[] raw,
        string? ifMatchETag = null, bool ifNoneMatchCreate = false);

    /// <summary>Deletes the account blob. Used to roll back a blob this transaction
    /// created (absent before). Deleting an object that no longer matches the
    /// transaction's own write is never safe and callers guard against it.</summary>
    Task DeleteAccountStatsAsync(string accountId);
}