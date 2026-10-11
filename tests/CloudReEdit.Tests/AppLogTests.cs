using System.Text.RegularExpressions;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

/// <summary>Tests for the rolling file logger. Every test points the logger at its
/// own temp directory and shuts it down again, so nothing touches real AppData and
/// no test can observe another test's files.</summary>
public class AppLogTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CloudReEditAppLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultIsInert_NoFileIsCreatedBeforeInit()
    {
        AppLog.Shutdown();

        Assert.False(AppLog.IsEnabled);
        Assert.Equal("", AppLog.LogDirectory);
        Assert.Equal("", AppLog.CurrentFilePath);

        AppLog.Info("app", "this must not throw or write anywhere");
        AppLog.Exception("app", "nor should this", new InvalidOperationException("boom"));

        Assert.False(AppLog.IsEnabled);
        Assert.Equal("", AppLog.LogDirectory);
    }

    [Fact]
    public void Init_CreatesTodayFileWithExpectedNameAndFormat()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            AppLog.InitAt(dir);
            Assert.True(AppLog.IsEnabled);
            Assert.Equal(dir, AppLog.LogDirectory);

            var expectedName = $"CloudReEdit-{DateTime.Now:yyyyMMdd}.log";
            Assert.Equal(expectedName, Path.GetFileName(AppLog.CurrentFilePath));
            Assert.True(File.Exists(AppLog.CurrentFilePath));

            AppLog.Info("save", "hello world");

            var path = AppLog.CurrentFilePath;
            AppLog.Shutdown();

            var lines = File.ReadAllText(path).Split('\n')
                            .Select(l => l.TrimEnd('\r'))
                            .Where(l => l.Length > 0);

            Assert.Contains(lines, l => Regex.IsMatch(l,
                @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO {2}save: hello world$"));
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void AllLevels_AreWrittenWithTheirOwnTag()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            AppLog.InitAt(dir);
            AppLog.Debug("cat", "d");
            AppLog.Info("cat", "i");
            AppLog.Warn("cat", "w");
            AppLog.Error("cat", "e");

            var path = AppLog.CurrentFilePath;
            AppLog.Shutdown();

            var text = File.ReadAllText(path);
            Assert.Contains(" DEBUG cat: d", text);
            Assert.Contains(" INFO  cat: i", text);
            Assert.Contains(" WARN  cat: w", text);
            Assert.Contains(" ERROR cat: e", text);
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void ExceptionLogging_IncludesTypeMessageAndStaysOnOneLine()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            AppLog.InitAt(dir);
            AppLog.Exception("save", "phase vdf failed",
                new InvalidOperationException("inner failure"));

            var path = AppLog.CurrentFilePath;
            AppLog.Shutdown();

            var text = File.ReadAllText(path);
            var ours = text.Split('\n')
                           .Select(l => l.TrimEnd('\r'))
                           .Where(l => l.Contains("phase vdf failed"))
                           .ToArray();

            Assert.Single(ours);
            Assert.Contains("InvalidOperationException", ours[0]);
            Assert.Contains("inner failure", ours[0]);
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void Retention_KeepsOnlyTheNewestDailyFiles_AndLeavesUnrelatedOnesAlone()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            string DailyName(int daysAgo) => $"CloudReEdit-{DateTime.Now.AddDays(-daysAgo):yyyyMMdd}.log";

            const int created = AppLog.KeepFiles + 2;
            for (int i = 0; i < created; i++)
                File.WriteAllText(Path.Combine(dir, DailyName(i)), $"day -{i}\n");

            var unrelated = new[]
            {
                Path.Combine(dir, "notes.txt"),
                Path.Combine(dir, "CloudReEdit-notadate.log"),
                Path.Combine(dir, "CloudReEdit-20260101.json"),
            };
            foreach (var file in unrelated)
                File.WriteAllText(file, "keep me\n");

            AppLog.InitAt(dir);
            AppLog.Shutdown();

            var present = Directory.GetFiles(dir, "CloudReEdit-*.log")
                                   .Select(Path.GetFileName)
                                   .Where(n => Regex.IsMatch(n ?? "", @"^CloudReEdit-\d{8}\.log$"))
                                   .ToArray();

            Assert.Equal(AppLog.KeepFiles, present.Length);
            Assert.Contains(DailyName(0), present);
            Assert.DoesNotContain(DailyName(AppLog.KeepFiles), present);
            Assert.DoesNotContain(DailyName(AppLog.KeepFiles + 1), present);

            foreach (var file in unrelated)
                Assert.True(File.Exists(file), $"unrelated file was deleted: {file}");
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void Retention_NeverDeletesFilesThatAreNotDailyLogNames()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            const int fakes = AppLog.KeepFiles + 5;
            var fakePaths = new List<string>();
            for (int i = 0; i < fakes; i++)
            {
                var cloudReEdit = Path.Combine(dir, $"CloudReEdit-{i}.log");
                File.WriteAllText(cloudReEdit, "x\n");
                fakePaths.Add(cloudReEdit);
                File.WriteAllText(Path.Combine(dir, $"Other-{i}.log"), "x\n");
            }

            AppLog.InitAt(dir);
            AppLog.Shutdown();

            Assert.Equal(fakes, Directory.GetFiles(dir, "Other-*.log").Length);

            foreach (var file in fakePaths)
                Assert.True(File.Exists(file), $"non-daily log name was deleted: {file}");
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void DailyFile_DoesNotGrowPastTheCap_AndWritesASingleMarker()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            var path = Path.Combine(dir, $"CloudReEdit-{DateTime.Now:yyyyMMdd}.log");
            File.WriteAllText(path, new string('x', (int)AppLog.MaxFileBytes - 64));

            AppLog.InitAt(dir);
            for (int i = 0; i < 500; i++)
                AppLog.Info("flood", $"entry {i} - {new string('y', 80)}");
            AppLog.Shutdown();

            var size = new FileInfo(path).Length;
            Assert.True(size <= AppLog.MaxFileBytes + 1024,
                $"day file grew to {size} bytes, cap is {AppLog.MaxFileBytes}");

            var text = File.ReadAllText(path);
            Assert.Contains("further entries for this day are dropped", text);
            Assert.DoesNotContain("entry 499", text);
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }

    [Fact]
    public void NeverThrows_WhenTheDirectoryCannotBeCreated()
    {
        var blocked = Path.Combine(NewTempDir(), "blocked");
        File.WriteAllText(blocked, "I am a file, not a directory");

        AppLog.Shutdown();
        try
        {
            AppLog.InitAt(blocked);

            AppLog.Info("app", "one");
            AppLog.Exception("app", "two", new Exception("three"));
            AppLog.Shutdown();

            AppLog.Info("app", "after shutdown");
            AppLog.Debug("app", "after shutdown");
            AppLog.Warn("app", "after shutdown");
            AppLog.Error("app", "after shutdown");
        }
        finally
        {
            AppLog.Shutdown();
        }

        Assert.False(AppLog.IsEnabled);
        Assert.Equal("", AppLog.LogDirectory);
    }

    [Fact]
    public void Reinit_IsIdempotent_AndCreatesASingleFilePerDay()
    {
        var dir = NewTempDir();
        AppLog.Shutdown();

        try
        {
            AppLog.InitAt(dir);
            AppLog.Shutdown();
            Assert.Equal(1, Directory.GetFiles(dir, "CloudReEdit-*.log").Length);

            AppLog.InitAt(dir);
            AppLog.Shutdown();
            Assert.Equal(1, Directory.GetFiles(dir, "CloudReEdit-*.log").Length);
        }
        finally
        {
            AppLog.Shutdown();
            TryDelete(dir);
        }
    }
}

