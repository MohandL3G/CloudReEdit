using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudReEdit.Services;

/// <summary>Durable per-save record written atomically into the backup directory.
/// Records what existed before, what the target was, and the final phase/result so
/// a future recovery feature can restore the exact previous state.</summary>
public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = "";
    public string AccountId { get; set; } = "";
    public uint AppId { get; set; }
    public string? ProviderName { get; set; }

    public ManifestTarget? Target { get; set; }
    public ManifestCopy? Local { get; set; }
    public ManifestCopy? Cloud { get; set; }
    public ManifestCopy? Vdf { get; set; }

    public string Phase { get; set; } = "";
    public string Result { get; set; } = "";

    public string ToJson()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class ManifestTarget
{
    public long TotalMinutes { get; set; }
    public long TwoWeekMinutes { get; set; }
    public long LastPlayed { get; set; }
}

public sealed class ManifestCopy
{
    public bool ExistedBefore { get; set; }
    public string? Sha256 { get; set; }
    public string State { get; set; } = "";
    public string? ETag { get; set; }
}