using System.IO;
using System.Text.Json.Nodes;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class SaveHelpersTests
{
    private const string SixTabs = "\t\t\t\t\t\t";

    [Fact]
    public void TryGetAppPlaytimeValues_ReturnsAllThreeValues()
    {
        var vdf = BuildVdf(AppBlock("123",
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"Playtime2wks\"\t\t\"80\"\n" +
            SixTabs + "\"LastPlayed\"\t\t\"123456\"\n"));
        using var env = VdfEnv.New(vdf);

        bool ok = VdfEditor.TryGetAppPlaytimeValues(env.Path, "123",
            out var p, out var p2, out var lp);

        Assert.True(ok);
        Assert.Equal(100, p);
        Assert.Equal(80, p2);
        Assert.Equal(123456, lp);
    }

    [Fact]
    public void TryGetAppPlaytimeValues_Missing2wks_ReturnsZero()
    {
        var vdf = BuildVdf(AppBlock("123",
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"LastPlayed\"\t\t\"123456\"\n"));
        using var env = VdfEnv.New(vdf);

        bool ok = VdfEditor.TryGetAppPlaytimeValues(env.Path, "123",
            out var p, out var p2, out _);

        Assert.True(ok);
        Assert.Equal(100, p);
        Assert.Equal(0, p2);
    }

    [Fact]
    public void AppBlockExists_TrueForPresentApp_FalseForOther()
    {
        var vdf = BuildVdf(AppBlock("123", SixTabs + "\"Playtime\"\t\t\"100\"\n"));
        using var env = VdfEnv.New(vdf);

        Assert.True(VdfEditor.AppBlockExists(env.Path, "123"));
        Assert.False(VdfEditor.AppBlockExists(env.Path, "999"));
    }

    [Fact]
    public void UnrelatedContentUnchanged_TrueAfterPatch()
    {
        var before = BuildVdf(AppBlock("123",
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"Settings\"\t\t\"keep\"\n") +
            AppBlock("456", SixTabs + "\"Playtime\"\t\t\"777\"\n"));
        using var env = VdfEnv.New(before);

        VdfEditor.PatchAppPlaytime(env.Path, "123", 200, 35, 999999);
        string after = File.ReadAllText(env.Path);

        Assert.True(VdfEditor.UnrelatedContentUnchanged(before, after, "123"));
    }

    [Fact]
    public void UnrelatedContentUnchanged_False_WhenOtherAppEdited()
    {
        var before = BuildVdf(AppBlock("123", SixTabs + "\"Playtime\"\t\t\"100\"\n") +
            AppBlock("456", SixTabs + "\"Playtime\"\t\t\"777\"\n"));
        var after = BuildVdf(AppBlock("123", SixTabs + "\"Playtime\"\t\t\"200\"\n") +
            AppBlock("456", SixTabs + "\"Playtime\"\t\t\"778\"\n"));

        Assert.False(VdfEditor.UnrelatedContentUnchanged(before, after, "123"));
    }

    [Fact]
    public void SemanticallyEquals_TrueForEqualModels()
    {
        var a = PlaytimeModel.Parse(Json("{\"minutes_2weeks\":50,\"last_played\":7,\"per_device\":" +
            "{\"pc\":{\"windows\":30,\"mac\":20},\"__migrated_localconfig\":{\"windows\":50}}}"));
        var b = PlaytimeModel.Parse(Json("{\"minutes_2weeks\":50,\"last_played\":7,\"per_device\":" +
            "{\"pc\":{\"windows\":30,\"mac\":20},\"__migrated_localconfig\":{\"windows\":50}}}"));

        Assert.True(a.SemanticallyEquals(b));
    }

    [Fact]
    public void SemanticallyEquals_False_WhenDayBucketDiffers()
    {
        var a = PlaytimeModel.Parse(DaysModel(("19000", 5), ("19001", 6)));
        var b = PlaytimeModel.Parse(DaysModel(("19000", 5), ("19001", 7)));
        var c = PlaytimeModel.Parse(DaysModel(("19000", 5)));

        Assert.False(a.SemanticallyEquals(b));
        Assert.False(a.SemanticallyEquals(c));
    }

    [Fact]
    public void SemanticallyEquals_False_WhenDayTrackingFlagDiffers()
    {
        var tracked = PlaytimeModel.Parse(Json("{\"per_device\":{\"pc\":{\"windows\":30,\"days\":{}}}}"));
        var legacy = PlaytimeModel.Parse(Json("{\"per_device\":{\"pc\":{\"windows\":30}}}"));
        var roundTrip = PlaytimeModel.Parse(tracked.ToJson());

        Assert.True(tracked.HasDayTracking);
        Assert.False(legacy.HasDayTracking);
        Assert.False(tracked.SemanticallyEquals(legacy));
        Assert.True(tracked.SemanticallyEquals(roundTrip));
    }

    private static JsonObject Json(string s) => JsonNode.Parse(s) as JsonObject ?? new JsonObject();

    private static JsonObject DaysModel(params (string Day, long Minutes)[] days)
    {
        var daysObj = new JsonObject();
        foreach (var (day, minutes) in days)
            daysObj[day] = minutes;
        return new JsonObject
        {
            ["per_device"] = new JsonObject
            {
                ["pc"] = new JsonObject
                {
                    ["windows"] = 30,
                    ["days"] = daysObj,
                },
            },
        };
    }

    private static string BuildVdf(string appBlocks) =>
        "\"UserLocalConfigStore\"\n{\n" +
        "\t\"Software\"\n\t{\n" +
        "\t\t\"Valve\"\n\t\t{\n" +
        "\t\t\t\"Steam\"\n\t\t\t{\n" +
        "\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        appBlocks +
        "\t\t\t\t}\n" +
        "\t\t\t}\n" +
        "\t\t}\n" +
        "\t}\n" +
        "}\n";

    private static string AppBlock(string appId, string inner) =>
        $"\t\t\t\t\t\"{appId}\"\n" +
        "\t\t\t\t\t{\n" +
        inner +
        "\t\t\t\t\t}\n";

    private sealed class VdfEnv : System.IDisposable
    {
        public string Path { get; private set; } = "";

        public static VdfEnv New(string content)
        {
            var env = new VdfEnv
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "CloudReEdit_Vdf_" + System.Guid.NewGuid().ToString("N") + ".vdf"),
            };
            System.IO.File.WriteAllText(env.Path, content);
            return env;
        }

        public void Dispose()
        {
            try { System.IO.File.Delete(Path); } catch { }
        }
    }
}