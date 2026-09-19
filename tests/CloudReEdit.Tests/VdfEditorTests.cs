using System.IO;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class VdfEditorTests
{
    private const string SixTabs = "\t\t\t\t\t\t";

    [Fact]
    public void PatchAppPlaytime_UpdatesExistingKeys()
    {
        var vdf = BuildVdf(AppBlock("123",
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"Playtime2wks\"\t\t\"80\"\n" +
            SixTabs + "\"LastPlayed\"\t\t\"123456\"\n"));
        var path = WriteTempVdf(vdf);
        try
        {
            Assert.True(VdfEditor.PatchAppPlaytime(path, "123", 200, 35, 999999));

            var text = File.ReadAllText(path);
            Assert.Contains("\"Playtime\"\t\t\"200\"", text);
            Assert.Contains("\"Playtime2wks\"\t\t\"35\"", text);
            Assert.Contains("\"LastPlayed\"\t\t\"999999\"", text);
            Assert.DoesNotContain("\"Playtime\"\t\t\"100\"", text);
            Assert.DoesNotContain("\"Playtime2wks\"\t\t\"80\"", text);
            Assert.DoesNotContain("\"LastPlayed\"\t\t\"123456\"", text);
        }
        finally { CleanupVdf(path); }
    }

    [Fact]
    public void PatchAppPlaytime_MissingPlaytime2wks_NotInserted()
    {
        var vdf = BuildVdf(AppBlock("123",
            SixTabs + "\"Playtime\"\t\t\"100\"\n" +
            SixTabs + "\"LastPlayed\"\t\t\"123456\"\n"));
        var path = WriteTempVdf(vdf);
        try
        {
            Assert.True(VdfEditor.PatchAppPlaytime(path, "123", 200, 35, 999999));

            var text = File.ReadAllText(path);
            Assert.Contains("\"Playtime\"\t\t\"200\"", text);
            Assert.Contains("\"LastPlayed\"\t\t\"999999\"", text);
            Assert.DoesNotContain("Playtime2wks", text);
        }
        finally { CleanupVdf(path); }
    }

    [Fact]
    public void PatchAppPlaytime_PreservesCrlfAndUnrelatedContent()
    {
        var vdf = BuildVdf(
            AppBlock("123",
                SixTabs + "\"Playtime\"\t\t\"100\"\n" +
                SixTabs + "\"LastPlayed\"\t\t\"123456\"\n" +
                SixTabs + "\"Settings\"\t\t\"keep\"\n") +
            AppBlock("456",
                SixTabs + "\"Playtime\"\t\t\"777\"\n" +
                SixTabs + "\"LastPlayed\"\t\t\"111\"\n"));
        vdf = vdf.Replace("\n", "\r\n");
        var path = WriteTempVdf(vdf);
        try
        {
            Assert.True(VdfEditor.PatchAppPlaytime(path, "123", 200, 35, 999999));

            var text = File.ReadAllText(path);

            // CRLF preserved: no bare \n remains.
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
            Assert.Contains("\"Playtime\"\t\t\"200\"\r\n", text);
            Assert.Contains("\"LastPlayed\"\t\t\"999999\"\r\n", text);

            // Unrelated app block untouched.
            Assert.Contains("\"456\"", text);
            Assert.Contains("\"Playtime\"\t\t\"777\"", text);
            Assert.Contains("\"LastPlayed\"\t\t\"111\"", text);

            // Unrelated field inside the patched block untouched.
            Assert.Contains("\"Settings\"\t\t\"keep\"", text);

            // Structure headings preserved.
            Assert.Contains("\"UserLocalConfigStore\"", text);
            Assert.Contains("\"apps\"", text);
        }
        finally { CleanupVdf(path); }
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

    private static string WriteTempVdf(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CloudReEdit_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "localconfig.vdf");
        File.WriteAllText(path, content);
        return path;
    }

    private static void CleanupVdf(string path) =>
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
}