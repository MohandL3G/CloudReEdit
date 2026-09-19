using System.Text.Json.Nodes;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class PlaytimeModelTests
{
    private const long SecondsPerDay = 86400L;

    // Fixed deterministic "now". All day indexes are derived from it so the
    // two-week window never depends on the machine clock.
    private const long Now = 1_700_000_000L;
    private static readonly long Today = Now / SecondsPerDay;

    private static long DayIndex(long daysRelativeToToday) => Today + daysRelativeToToday;

    private static PlaytimeModel ModelWithRealDevice(long total, params (long Day, long Mins)[] days)
    {
        var m = new PlaytimeModel();
        var dev = new DevicePlaytime { Name = "DESKTOP-TEST", Windows = total };
        foreach (var (day, mins) in days) dev.Days[day] = mins;
        m.PerDevice.Add(dev);
        return m;
    }

    [Fact]
    public void Legacy_NoDays_ReturnsTotal()
    {
        var m = ModelWithRealDevice(500);
        Assert.False(m.HasDayTracking);
        Assert.Equal(500, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void DayTracked_RecentDays_ReturnsRecentSum()
    {
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 60),
            (DayIndex(-2), 60));

        Assert.True(m.HasDayTracking);
        Assert.Equal(120, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void DayTracked_RecentSumExceedsLifetime_CapsAtLifetime()
    {
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 400),
            (DayIndex(-2), 300));

        Assert.Equal(500, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void ExpiredDays_ExcludedFromTotal_ButSerializedVerbatim()
    {
        var pastDay = DayIndex(-20);
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 60),
            (pastDay, 999));

        Assert.Equal(60, m.CalculateCloudRedirectTwoWeekTotal(Now));

        var json = m.ToJson().ToJsonString();
        var reparsed = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);
        Assert.Equal(999, reparsed.PerDevice[0].Days[pastDay]);
    }

    [Fact]
    public void FutureDays_ExcludedFromTotal_ButSerializedVerbatim()
    {
        var futureDay = DayIndex(5);
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 60),
            (futureDay, 888));

        Assert.Equal(60, m.CalculateCloudRedirectTwoWeekTotal(Now));

        var json = m.ToJson().ToJsonString();
        var reparsed = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);
        Assert.Equal(888, reparsed.PerDevice[0].Days[futureDay]);
    }

    [Fact]
    public void DayTrackingWindow_DecaysToZero_WhenNoRecentSessions()
    {
        var m = ModelWithRealDevice(500, (DayIndex(-30), 120));

        Assert.Equal(0, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void DayBuckets_AtWindowBoundary_AreIncluded()
    {
        // Day -14 ends exactly at the cutoff instant (included, CloudRedirect
        // excludes only buckets whose day-end is at or before the cutoff).
        var m = ModelWithRealDevice(500, (DayIndex(-14), 30));

        Assert.Equal(30, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void RoundTrip_DaysSurvive()
    {
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 60),
            (DayIndex(-2), 90));

        var json = m.ToJson().ToJsonString();
        var reparsed = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);

        Assert.Equal(2, reparsed.PerDevice[0].Days.Count);
        Assert.Equal(60, reparsed.PerDevice[0].Days[DayIndex(-1)]);
        Assert.Equal(90, reparsed.PerDevice[0].Days[DayIndex(-2)]);
        Assert.Equal(150, reparsed.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void Parse_NonNumericDayKeys_AreDropped()
    {
        var json = """
            {
                "per_device": {
                    "DESKTOP-TEST": {
                        "windows": 500,
                        "mac": 0,
                        "linux": 0,
                        "days": {
                            "garbage": 42,
                            "123": 7
                        }
                    }
                }
            }
            """;

        var reparsed = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);

        Assert.Single(reparsed.PerDevice[0].Days);
        Assert.Equal(7, reparsed.PerDevice[0].Days[123]);
    }

    [Fact]
    public void LifetimeIncrease_GrowsMigratedBucket_LeavesDaysUntouched()
    {
        var m = ModelWithRealDevice(500,
            (DayIndex(-1), 60),
            (DayIndex(-2), 60));
        m.AdjustBucketsToTotal(750);

        var migrated = m.PerDevice.FirstOrDefault(d => d.Name == "__migrated_localconfig");
        Assert.NotNull(migrated);
        Assert.Equal(250, migrated!.Windows);
        Assert.Empty(migrated.Days);

        var real = m.PerDevice.First(d => d.Name == "DESKTOP-TEST");
        Assert.Equal(60, real.Days[DayIndex(-1)]);
        Assert.Equal(60, real.Days[DayIndex(-2)]);

        // Two-week stays derived from day history, not the inflated lifetime.
        Assert.Equal(120, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void LifetimeDecrease_RescalesRealBuckets_PreservesDays_CapsTwoWeek()
    {
        var m = ModelWithRealDevice(400,
            (DayIndex(-1), 70),
            (DayIndex(-2), 50));
        m.AdjustBucketsToTotal(100);

        Assert.Equal(100, m.Total);

        var real = m.PerDevice.First(d => d.Name == "DESKTOP-TEST");
        Assert.Equal(70, real.Days[DayIndex(-1)]);
        Assert.Equal(50, real.Days[DayIndex(-2)]);

        // Recent day sum (120) is capped by the reduced lifetime (100).
        Assert.Equal(100, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void AbsentDays_IsLegacy()
    {
        var json = """
            {
                "per_device": {
                    "DESKTOP-TEST": { "windows": 500, "mac": 0, "linux": 0 }
                }
            }
            """;
        var m = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);

        Assert.False(m.HasDayTracking);
        Assert.Equal(500, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void EmptyDays_IsDayTracked_TwoWeekZero()
    {
        var json = """
            {
                "per_device": {
                    "DESKTOP-TEST": {
                        "windows": 500, "mac": 0, "linux": 0,
                        "days": {}
                    }
                }
            }
            """;
        var m = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);

        Assert.True(m.HasDayTracking);
        Assert.Empty(m.PerDevice[0].Days);
        Assert.Equal(0, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void EmptyDays_SurviveSerialization()
    {
        var json = """
            {
                "per_device": {
                    "DESKTOP-TEST": {
                        "windows": 500, "mac": 0, "linux": 0,
                        "days": {}
                    }
                }
            }
            """;
        var m = PlaytimeModel.Parse(JsonNode.Parse(json) as JsonObject);
        var outJson = m.ToJson().ToJsonString();

        Assert.Contains("\"days\":{}", outJson);

        var roundtrip = PlaytimeModel.Parse(JsonNode.Parse(outJson) as JsonObject);
        Assert.True(roundtrip.HasDayTracking);
        Assert.Empty(roundtrip.PerDevice[0].Days);
        Assert.Equal(0, roundtrip.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void MultiDevice_RecentSum_IsSumAcrossDevices()
    {
        var m = new PlaytimeModel();
        var a = new DevicePlaytime { Name = "DESKTOP-ALPHA", Windows = 250 };
        a.Days[DayIndex(-1)] = 60;
        a.Days[DayIndex(-2)] = 40;
        var b = new DevicePlaytime { Name = "DESKTOP-BETA", Windows = 250 };
        b.Days[DayIndex(-1)] = 30;
        m.PerDevice.Add(a);
        m.PerDevice.Add(b);

        Assert.Equal(130, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void MultiDevice_RecentSumExceedsLifetime_CapsAtLifetime()
    {
        var m = new PlaytimeModel();
        var a = new DevicePlaytime { Name = "DESKTOP-ALPHA", Windows = 60 };
        a.Days[DayIndex(-1)] = 60;
        var b = new DevicePlaytime { Name = "DESKTOP-BETA", Windows = 30 };
        b.Days[DayIndex(-1)] = 120;
        m.PerDevice.Add(a);
        m.PerDevice.Add(b);

        // Recent sum (180) exceeds the lifetime (90); capped at lifetime.
        Assert.Equal(90, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }

    [Fact]
    public void MidnightBoundary_TodayMinus14Included_TodayMinus15Excluded()
    {
        long midnight = Today * SecondsPerDay; // exact UTC midnight
        var m = ModelWithRealDevice(1000,
            (Today - 14, 10),
            (Today - 15, 20),
            (Today, 30),
            (Today + 1, 40),
            (Today + 2, 50));

        // -14 ends after cutoff (included); -15 ends exactly AT the cutoff
        // (excluded); today and today+1 (CR slack) included; today+2 excluded.
        Assert.Equal(80, m.CalculateCloudRedirectTwoWeekTotal(midnight));
    }

    [Fact]
    public void LifetimeReductionToZero_PreservesDays_TwoWeekZero()
    {
        var m = ModelWithRealDevice(400,
            (DayIndex(-1), 70),
            (DayIndex(-2), 50));
        Assert.True(m.HasDayTracking);

        m.AdjustBucketsToTotal(0);

        Assert.Equal(0, m.Total);
        Assert.True(m.HasDayTracking);

        var real = m.PerDevice.First(d => d.Name == "DESKTOP-TEST");
        Assert.Equal(2, real.Days.Count);
        Assert.Equal(70, real.Days[DayIndex(-1)]);
        Assert.Equal(50, real.Days[DayIndex(-2)]);

        Assert.Equal(0, m.CalculateCloudRedirectTwoWeekTotal(Now));
    }
}