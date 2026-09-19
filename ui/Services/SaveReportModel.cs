using System.Collections.Generic;

namespace CloudReEdit.Services;

/// <summary>Semantic outcome of a multi-copy save.</summary>
public enum SaveOutcome
{
    NotSaved,
    SavedVerified,
    SavedUnverified,
    PartialRolledBack,
    RollbackFailed,
    BackupFailed,
    CloudChangedConcurrently,
    CloudStateUnknown,
    CloudDiverged,
    CloudWriteFailed,
}

public enum CopyTarget
{
    LocalJson,
    CloudBlob,
    Vdf,
}

/// <summary>Post-save state of a single copy target.</summary>
public enum CopyState
{
    NotAttempted,
    SkippedAbsentVdf,
    SkippedReadOnlyCloud,
    WriteFailed,
    Written,
    Unverified,
    Verified,
    Divergent,
    RolledBack,
    RestoreFailed,
}

public sealed class CopyStatus
{
    public CopyTarget Target { get; set; }
    public CopyState State { get; set; } = CopyState.NotAttempted;
}

/// <summary>Truthful save result: one per-copy state plus human readable messages.
/// Never reports "nothing was changed" when any copy reached <c>Written</c>,
/// <c>Divergent</c> or <c>RestoreFailed</c>.</summary>
public sealed class SaveReport
{
    public SaveOutcome Outcome { get; set; }
    public List<CopyStatus> Copies { get; } = new();
    public List<string> Messages { get; } = new();
    public string? BackupDir { get; set; }
}

/// <summary>Concrete file paths used by a single save transaction. Kept explicit so
/// the transaction can be tested against temp directories without Steam state.</summary>
public sealed class SavePaths
{
    public string LocalStatsPath { get; set; } = "";
    public string VdfPath { get; set; } = "";
    public string BackupRoot { get; set; } = "";
}

/// <summary>Portable identity + source model for a save. Deliberately not
/// <see cref="EditorContext"/> (which drags in Steam/config lookup) so the
/// transaction compiles in the pure-net test project.</summary>
public sealed class SaveScope
{
    public string AccountId { get; set; } = "";
    public uint AppId { get; set; }
    public PlaytimeModel Source { get; set; } = new();
}