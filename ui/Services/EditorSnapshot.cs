namespace CloudReEdit.Services;

/// <summary>Frozen copy of every value a playtime save depends on: the deep
/// per-device model (cells, names, membership, verbatim day buckets) plus the
/// effective total and last-played values that would be written. The editor
/// captures a baseline after each canonical load and compares later editor
/// states to it to decide whether the user has unsaved changes.</summary>
public sealed class EditorSnapshot
{
    public PlaytimeModel Model { get; }
    public long Total { get; }
    public long LastPlayed { get; }

    public EditorSnapshot(PlaytimeModel model, long total, long lastPlayed)
    {
        Model = model.Clone();
        Total = total;
        LastPlayed = lastPlayed;
    }

    /// <summary>True when a live editor state differs from this baseline in any
    /// way that would change a save: device cells/names/membership, the verbatim
    /// per-device day buckets, the requested total or last-played.</summary>
    public bool DiffersFrom(PlaytimeModel liveModel, long liveTotal, long liveLastPlayed)
        => Total != liveTotal
            || LastPlayed != liveLastPlayed
            || !Model.SemanticallyEquals(liveModel);
}