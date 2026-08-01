using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace CloudReEdit.Services.Cloud;

/// <summary>
/// Read/write access to a CloudRedirect account-scope stats blob
/// (<c>{accountId}/0/stats.json</c>), which maps appId string -> app stats JSON.
/// </summary>
public interface ICloudProvider
{
    string DisplayName { get; }
    bool CanWrite { get; }

    /// <summary>Returns the account blob, or null if it doesn't exist yet.</summary>
    Task<JsonObject?> GetAccountStatsAsync(string accountId);

    /// <summary>Uploads/replaces the account blob.</summary>
    Task PutAccountStatsAsync(string accountId, JsonObject stats);
}
