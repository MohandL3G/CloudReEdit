using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudReEdit.Services;

/// <summary>Severity of a log entry.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Rolling plain-text file logger writing
/// <c>%AppData%\CloudReEdit\logs\CloudReEdit-yyyyMMdd.log</c> — one file per day,
/// the oldest files deleted at startup once more than <see cref="KeepFiles"/>
/// accumulate, and a day's file capped at <see cref="MaxFileBytes"/> (once full a
/// single marker line is written and further entries for that day are dropped, so
/// a day never grows past the cap).
///
/// Each entry is <c>timestamp level category: message</c> on its own line, appended
/// and flushed per write so a crash still leaves usable output.
///
/// The logger is inert until <see cref="Init"/> runs, which keeps unit tests from
/// touching real AppData. It never throws: every failure is swallowed so a logging
/// problem can never surface as an application or save failure. Callers must never
/// pass credentials — no tokens, DPAPI output, provider config, or bucket keys.</summary>
public static class AppLog
{
    /// <summary>How many daily log files to keep.</summary>
    public const int KeepFiles = 7;

    /// <summary>Approximate per-file cap; a day's log never grows past this.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private const string FilePrefix = "CloudReEdit-";
    private const string FileSuffix = ".log";

    private static readonly object Gate = new();

    private static StreamWriter? _writer;
    private static string _directory = "";
    private static string _day = "";
    private static long _written;
    private static bool _truncationNotified;

    /// <summary>Directory holding the daily log files. Empty until initialized.</summary>
    public static string LogDirectory
    {
        get { lock (Gate) return _directory; }
    }

    /// <summary>Path of the log file for the current day, or an empty string when
    /// the logger is not initialized.</summary>
    public static string CurrentFilePath
    {
        get
        {
            lock (Gate)
                return _writer == null || _day.Length == 0
                    ? ""
                    : Path.Combine(_directory, FileName(_day));
        }
    }

    /// <summary>Whether the logger is writing.</summary>
    public static bool IsEnabled
    {
        get { lock (Gate) return _writer != null; }
    }

    /// <summary>Opens the log under <c>%AppData%\CloudReEdit\logs</c>, deletes
    /// files older than <see cref="KeepFiles"/>, and starts today's file.</summary>
    public static void Init()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            InitAt(Path.Combine(root, "CloudReEdit", "logs"));
        }
        catch { }
    }

    /// <summary>Flushes and closes the log and returns it to its inert state.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _directory = "";
            _day = "";
            _written = 0;
            _truncationNotified = false;
        }
    }

    public static void Debug(string category, string message)
        => Write(LogLevel.Debug, category, message, null);

    public static void Info(string category, string message)
        => Write(LogLevel.Info, category, message, null);

    public static void Warn(string category, string message)
        => Write(LogLevel.Warn, category, message, null);

    public static void Error(string category, string message)
        => Write(LogLevel.Error, category, message, null);

    /// <summary>Logs an exception with its type, message and stack trace plus a
    /// little context describing what was being attempted.</summary>
    public static void Exception(string category, string context, Exception exception)
        => Write(LogLevel.Error, category, context, exception);

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder(160)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ')
                .Append(LevelTag(level))
                .Append(' ')
                .Append(string.IsNullOrEmpty(category) ? "app" : category)
                .Append(": ")
                .Append(message ?? "");

            if (ex != null)
            {
                line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace))
                    line.Append(" @ ").Append(ex.StackTrace.Replace("\r\n", " ").Replace("\n", " "));
            }

            AppendLine(line.ToString());
        }
        catch { }
    }

    private static void AppendLine(string line)
    {
        var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

        lock (Gate)
        {
            if (_writer == null) return;

            try
            {
                var today = DateTime.Now.ToString("yyyyMMdd");
                if (!string.Equals(today, _day, StringComparison.Ordinal))
                    Reopen(today);

                if (_written + bytes > MaxFileBytes)
                {
                    if (!_truncationNotified)
                    {
                        _truncationNotified = true;
                        _writer.WriteLine(
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} WARN  app: log file reached " +
                            $"{MaxFileBytes} bytes; further entries for this day are dropped.");
                        _writer.Flush();
                    }
                    return;
                }

                _writer.WriteLine(line);
                _writer.Flush();
                _written += bytes;
            }
            catch { }
        }
    }

    private static void Reopen(string day)
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }

        _day = day;
        _written = 0;
        _truncationNotified = false;

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FileName(day));
        _written = File.Exists(path) ? new FileInfo(path).Length : 0;
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FileName(string day) => FilePrefix + day + FileSuffix;

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO ",
    };

    /// <summary>Initializes against an explicit directory and deletes every daily
    /// file beyond the newest <see cref="KeepFiles"/>. Exposed for tests.</summary>
    internal static void InitAt(string logDirectory)
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _day = "";
            _written = 0;
            _truncationNotified = false;

            _directory = logDirectory ?? "";
            if (_directory.Length == 0) return;

            try
            {
                Directory.CreateDirectory(_directory);
                DeleteExcessFiles(_directory);
                Reopen(DateTime.Now.ToString("yyyyMMdd"));
            }
            catch
            {
                _writer = null;
            }
        }
    }

    /// <summary>Deletes every <c>CloudReEdit-yyyyMMdd.log</c> file beyond the
    /// newest <see cref="KeepFiles"/>, oldest first. Files that are not ours are
    /// never touched. A failure to delete one file does not stop the others.</summary>
    internal static int DeleteExcessFiles(string directory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            files = Directory.GetFiles(directory, FilePrefix + "*" + FileSuffix)
                             .Where(IsDailyLogFile)
                             .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                             .ToArray();
        }
        catch { return 0; }

        int removed = 0;
        foreach (var file in files.Skip(KeepFiles))
        {
            try { File.Delete(file); removed++; }
            catch { }
        }
        return removed;
    }

    private static bool IsDailyLogFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length != FilePrefix.Length + 8 + FileSuffix.Length) return false;
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal)) return false;
        if (!name.EndsWith(FileSuffix, StringComparison.Ordinal)) return false;

        for (int i = 0; i < 8; i++)
            if (!char.IsAsciiDigit(name[FilePrefix.Length + i])) return false;

        return true;
    }
}
