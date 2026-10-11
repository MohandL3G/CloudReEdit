using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CloudReEdit.Services;

internal static class FileUtils
{
    private static void WriteFlushAndPublish(string path, System.Action<FileStream> writer)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, 81920, FileOptions.None))
            {
                writer(fs);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); }
            catch (Exception cleanupEx)
            {
                AppLog.Warn("io", $"temporary file cleanup failed for \"{tmp}\": {cleanupEx.GetType().Name}: {cleanupEx.Message}");
            }
            throw;
        }
    }

    public static void AtomicWriteAllBytes(string path, byte[] data)
    {
        WriteFlushAndPublish(path, fs => fs.Write(data, 0, data.Length));
    }

    public static void AtomicWriteAllText(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        WriteFlushAndPublish(path, fs => fs.Write(bytes, 0, bytes.Length));
    }

    public static void AtomicCopy(string sourcePath, string destPath)
    {
        WriteFlushAndPublish(destPath, fs =>
        {
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, 81920, FileOptions.SequentialScan);
            src.CopyTo(fs);
        });
    }
}
