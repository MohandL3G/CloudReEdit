using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class EditorSnapshotTests
{
    private const long LastPlayed = 1700000000;

    private static PlaytimeModel Model(long total)
    {
        var m = new PlaytimeModel { LastPlayed = LastPlayed };
        if (total > 0)
            m.PerDevice.Add(new DevicePlaytime { Name = "pc", Windows = total });
        return m;
    }

    [Fact]
    public void UntouchedState_IsClean()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        Assert.False(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void DeviceCellChange_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        live.PerDevice[0].Windows = 300;
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void AddedDevice_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        live.PerDevice.Add(new DevicePlaytime { Name = "laptop", Windows = 5 });
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void RemovedDevice_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        live.PerDevice.RemoveAt(0);
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void RenamedDevice_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        live.PerDevice[0].Name = "workstation";
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void LastPlayedChange_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        Assert.True(baseline.DiffersFrom(live, live.Total, LastPlayed + 86400));
    }

    [Fact]
    public void TargetTotalChange_IsDirty()
    {
        var baseline = new EditorSnapshot(Model(100), 100, LastPlayed);
        var live = Model(100);
        Assert.True(baseline.DiffersFrom(live, 200, live.LastPlayed));
    }

    [Fact]
    public void DayHistory_IsPreservedInBaselineAndCompared()
    {
        var model = Model(100);
        model.PerDevice[0]._daysPropertyPresent = true;
        model.PerDevice[0].Days[20340] = 30;
        model.PerDevice[0].Days[20341] = 15;

        var baseline = new EditorSnapshot(model, 100, LastPlayed);
        var live = Model(100);
        live.PerDevice[0]._daysPropertyPresent = true;
        live.PerDevice[0].Days[20340] = 30;
        live.PerDevice[0].Days[20341] = 15;

        Assert.False(baseline.DiffersFrom(live, live.Total, live.LastPlayed));

        live.PerDevice[0].Days[20341] = 60;
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
    }

    [Fact]
    public void BaselineCapture_DoesNotShareModelState()
    {
        var model = Model(100);
        var baseline = new EditorSnapshot(model, 100, LastPlayed);

        model.PerDevice[0].Windows = 999;
        model.PerDevice[0].Days[20340] = 5;

        var pristine = Model(100);
        Assert.False(baseline.DiffersFrom(pristine, pristine.Total, pristine.LastPlayed));
    }

    [Fact]
    public void FailedSave_KeepsBaseline_SoEditsStayDirty()
    {
        var live = Model(100);
        var baseline = new EditorSnapshot(live, 100, LastPlayed);

        live.PerDevice[0].Windows = 200;

        // A failed save does not reload or replace the baseline.
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));
        Assert.Equal(100, baseline.Total);
        Assert.Equal(100, baseline.Model.Total);
    }

    [Fact]
    public void SuccessfulSave_FreshBaseline_SoEditsAreClean()
    {
        var live = Model(100);
        var baseline = new EditorSnapshot(live, 100, LastPlayed);

        live.PerDevice[0].Windows = 200;
        Assert.True(baseline.DiffersFrom(live, live.Total, live.LastPlayed));

        // After a committed save the page re-loads the canonical state and
        // captures a fresh baseline for it.
        var canonical = Model(200);
        baseline = new EditorSnapshot(canonical, 200, canonical.LastPlayed);

        Assert.False(baseline.DiffersFrom(canonical, canonical.Total, canonical.LastPlayed));
    }
}