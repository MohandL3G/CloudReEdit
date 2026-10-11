using System;
using System.IO;
using System.Linq;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class BackupRetentionTests : IDisposable
{
    private const string AccountId = "76561198000000000";
    private const uint AppId = 123;
    private const string OtherAccountId = "76561198000000001";
    private const uint OtherAppId = 456;

    private readonly string _root;

    public BackupRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "CloudReEditRetention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private string MakeBackup(string accountId, uint appId, string timestamp)
    {
        var dir = Path.Combine(_root, $"{timestamp}_{accountId}-{appId}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        return dir;
    }

    private static string Stamp(int millisecond)
        => $"20260101_120000_{millisecond:D3}";

    private string[] Remaining()
        => Directory.GetDirectories(_root)
                   .Select(d => Path.GetFileName(d)!)
                   .OrderBy(n => n, StringComparer.Ordinal)
                   .ToArray();

    // ---------------- Prune ----------------

    [Fact]
    public void Prune_KeepsNewestFive_ForSamePair()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var result = BackupRetention.Prune(_root);

        Assert.Equal(7, result.Scanned);
        Assert.Equal(2, result.Deleted);
        Assert.Empty(result.Errors);

        var left = Remaining();
        Assert.Equal(5, left.Length);
        Assert.Equal(new[]
        {
            "20260101_120000_002_76561198000000000-123",
            "20260101_120000_003_76561198000000000-123",
            "20260101_120000_004_76561198000000000-123",
            "20260101_120000_005_76561198000000000-123",
            "20260101_120000_006_76561198000000000-123",
        }, left);
    }

    [Fact]
    public void Prune_LeavesOtherPairsAlone()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));
        for (int i = 0; i < 3; i++)
            MakeBackup(OtherAccountId, OtherAppId, Stamp(i));

        var result = BackupRetention.Prune(_root);

        Assert.Equal(2, result.Deleted);
        Assert.Empty(result.Errors);

        var left = Remaining();
        Assert.Equal(8, left.Length);
        Assert.Equal(3, left.Count(n => n!.EndsWith("-456", StringComparison.Ordinal)));
        Assert.Equal(5, left.Count(n => n!.EndsWith("-123", StringComparison.Ordinal)));
    }

    [Fact]
    public void Prune_DoesNotPruneAcrossGamesOfSameAccount()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, OtherAppId, Stamp(i));

        var result = BackupRetention.Prune(_root);

        Assert.Equal(4, result.Deleted);
        Assert.Equal(10, Remaining().Length);
    }

    [Fact]
    public void Prune_NeverDeletesProtectedDir()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var oldest = Path.Combine(_root, $"{Stamp(0)}_{AccountId}-{AppId}");
        var result = BackupRetention.Prune(_root, protectDir: oldest);

        Assert.True(Directory.Exists(oldest));
        Assert.Equal(1, result.Deleted);
        Assert.Equal(6, Remaining().Length);
    }

    [Fact]
    public void Prune_SkipsNamesItCannotParse()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var junk1 = Path.Combine(_root, "not_a_backup");
        var junk2 = Path.Combine(_root, "20260101_120000_000");
        var junk3 = Path.Combine(_root, "20260101_120000_000_123-abc");
        Directory.CreateDirectory(junk1);
        Directory.CreateDirectory(junk2);
        Directory.CreateDirectory(junk3);

        var result = BackupRetention.Prune(_root);

        Assert.Equal(10, result.Scanned);
        Assert.Equal(2, result.Deleted);
        Assert.True(Directory.Exists(junk1));
        Assert.True(Directory.Exists(junk2));
        Assert.True(Directory.Exists(junk3));
    }

    [Fact]
    public void Prune_MissingRootIsANoOp()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var result = BackupRetention.Prune(missing);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Prune_EmptyRootIsANoOp()
    {
        var result = BackupRetention.Prune(_root);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Prune_KeepsExactlyKeepPerPair_WhenFewerExist()
    {
        for (int i = 0; i < 5; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var result = BackupRetention.Prune(_root);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(5, Remaining().Length);
    }

    [Fact]
    public void Prune_CanKeepFewerThanDefault()
    {
        for (int i = 0; i < 7; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var result = BackupRetention.Prune(_root, keep: 2);

        Assert.Equal(5, result.Deleted);
        Assert.Equal(new[]
        {
            "20260101_120000_005_76561198000000000-123",
            "20260101_120000_006_76561198000000000-123",
        }, Remaining());
    }

    [Fact]
    public void Prune_TreatsUniquenessSuffixAsSamePair()
    {
        for (int i = 0; i < 5; i++)
            MakeBackup(AccountId, AppId, Stamp(i));

        var collided = Path.Combine(_root, $"{Stamp(0)}_{AccountId}-{AppId}-1");
        Directory.CreateDirectory(collided);
        File.WriteAllText(Path.Combine(collided, "manifest.json"), "{}");

        var result = BackupRetention.Prune(_root);

        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(collided));
        Assert.Equal(5, Remaining().Length);
    }

    [Fact]
    public void Prune_RecordsDeleteFailures_AndKeepsGoing()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? locked = null;
        for (int i = 0; i < 7; i++)
        {
            var dir = MakeBackup(AccountId, AppId, Stamp(i));
            if (i == 0) locked = dir;
        }

        using (new FileStream(Path.Combine(locked!, "locked.bin"),
                              FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = BackupRetention.Prune(_root);

            Assert.Single(result.Errors);
            Assert.Contains(locked!, result.Errors[0]);
            Assert.Equal(1, result.Deleted);
            Assert.True(Directory.Exists(locked!));
        }

        Assert.False(Directory.Exists(Path.Combine(_root, $"{Stamp(1)}_{AccountId}-{AppId}")));
    }

    // ---------------- Stats ----------------

    [Fact]
    public void GetStats_CountsFolders_AndTotalsBytes()
    {
        MakeBackup(AccountId, AppId, Stamp(0));
        MakeBackup(OtherAccountId, AppId, Stamp(1));
        MakeBackup(AccountId, OtherAppId, Stamp(2));

        var root = Path.Combine(_root, $"{Stamp(0)}_{AccountId}-{AppId}");
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[24]);

        var expectedBytes = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                                     .Sum(f => new FileInfo(f).Length);

        var stats = BackupRetention.GetStats(_root);

        Assert.Equal(_root, stats.Root);
        Assert.Equal(3, stats.Count);
        Assert.True(stats.TotalBytes >= 124);
        Assert.Equal(expectedBytes, stats.TotalBytes);
    }

    [Fact]
    public void GetStats_MissingRootReportsZeroes()
    {
        var stats = BackupRetention.GetStats(Path.Combine(_root, "nope"));

        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.TotalBytes);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatSize_RendersHumanReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, BackupRetention.FormatSize(bytes));
    }

    // ---------------- Name parsing ----------------

    [Theory]
    [InlineData("20260101_120000_000_76561198000000000-123", "76561198000000000", 123u)]
    [InlineData("20260101_235959_999_42-1", "42", 1u)]
    [InlineData("20260101_120000_000_76561198000000000-123-7", "76561198000000000", 123u)]
    [InlineData("20991231_000000_000_1-4294967295", "1", 4294967295u)]
    public void TryParseDirName_AcceptsSaveProtocolNames(string name, string accountId, uint appId)
    {
        Assert.True(BackupRetention.TryParseDirName(name, out var entry));
        Assert.Equal(accountId, entry.AccountId);
        Assert.Equal(appId, entry.AppId);
        Assert.Equal(name.Substring(0, 19), entry.Timestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not_a_backup")]
    [InlineData("20260101_120000_000")]
    [InlineData("20260101_120000_000_")]
    [InlineData("20260101_120000_000_123-")]
    [InlineData("20260101_120000_000_-123")]
    [InlineData("20260101_120000_000_123-abc")]
    [InlineData("20260101_120000_00_123-456")]
    [InlineData("abcdefgh_120000_000_123-456")]
    [InlineData("20260101_120000_000x123-456")]
    [InlineData("20260101-120000-000_123-456")]
    public void TryParseDirName_RejectsOtherNames(string name)
    {
        Assert.False(BackupRetention.TryParseDirName(name, out _));
    }
}
