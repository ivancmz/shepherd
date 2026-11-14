using System.Globalization;
using System.Text;

namespace Shepherd.Protocol;

/// <summary>
/// Append-only diagnostic log. Safe for several processes appending to the same file (each
/// line is one short append with shared access), never throws, and rotates by size
/// (<c>name.log</c> → <c>name.log.1</c>).
/// </summary>
public sealed class FileLog
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly string _prefix;
    private readonly bool _echoToStderr;
    private readonly Lock _lock = new();

    public FileLog(string path, string prefix = "", long maxBytes = 5 * 1024 * 1024, bool echoToStderr = false)
    {
        _path = path;
        _prefix = prefix;
        _maxBytes = maxBytes;
        _echoToStderr = echoToStderr;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public string FilePath => _path;

    public void Write(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {_prefix}{message}\n");
        lock (_lock)
        {
            if (_echoToStderr) Console.Error.Write(line);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Rotate();
                    using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(Encoding.UTF8.GetBytes(line));
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(5); // another process is appending or rotating
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes) return;
        File.Move(_path, _path + ".1", overwrite: true);
    }

    /// <summary>Deletes <c>*.log</c> files in <paramref name="dir"/> older than <paramref name="age"/>.</summary>
    public static void Prune(string dir, TimeSpan age)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.log*"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow - age) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
