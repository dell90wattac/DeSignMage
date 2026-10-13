using System.Globalization;
using System.Text;

namespace DeSignMage.Feedback;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// The program's log: a ring buffer of the latest lines (what a bug report attaches) plus a rolling file in
/// <c>%LOCALAPPDATA%\DeSignMage\logs</c>. Writing never throws and never blocks on the disk for long. Messages carry ids, file
/// names and counts, never document text.
/// </summary>
public sealed class LogService : IDisposable
{
    private const int MaxMessageLines = 120;
    private const int MaxLineLength = 600;

    private readonly object _lock = new();
    private readonly string[] _ring;
    private readonly string? _directory;
    private readonly long _maxFileBytes;
    private readonly int _keepFiles;
    private readonly Timer? _flushTimer;
    private int _head;
    private int _count;
    private StreamWriter? _writer;
    private long _fileSize;
    private bool _dirty;
    private int _fileFailures;
    private bool _disposed;

    public LogService(int ringCapacity = 2000, string? directory = null, long maxFileBytes = 1_000_000, int keepFiles = 5)
    {
        _ring = new string[Math.Max(10, ringCapacity)];
        _directory = directory;
        _maxFileBytes = maxFileBytes;
        _keepFiles = Math.Max(1, keepFiles);
        if (directory is not null)
        {
            OpenFile();
            _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The file being written now (null when the log is memory-only or the disk failed).</summary>
    public string? CurrentFile => _directory is null || _writer is null ? null : Path.Combine(_directory, "designmage.log");

    public void Write(LogLevel level, string category, string message)
    {
        try
        {
            var lines = Redactor.Scrub(message).Replace("\r", "").Split('\n');
            string tag = level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" };
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            lock (_lock)
            {
                if (_disposed) return;
                for (int i = 0; i < lines.Length && i < MaxMessageLines; i++)
                {
                    var text = TextUtil.Truncate(lines[i], MaxLineLength);
                    var line = i == 0 ? $"{stamp} {tag} [{category}] {text}" : $"    {text}";
                    _ring[(_head + _count) % _ring.Length] = line;
                    if (_count < _ring.Length) _count++;
                    else _head = (_head + 1) % _ring.Length;
                    WriteToFile(line);
                }
                if (lines.Length > MaxMessageLines) WriteToFile($"    … {lines.Length - MaxMessageLines} more lines");
                if (level >= LogLevel.Error) FlushLocked();
            }
        }
        catch (Exception)
        {
            // Logging must never be the thing that breaks the program.
        }
    }

    /// <summary>The latest <paramref name="count"/> lines of this session, oldest first.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_lock)
        {
            int take = Math.Min(Math.Max(0, count), _count);
            var result = new string[take];
            for (int i = 0; i < take; i++) result[i] = _ring[(_head + _count - take + i) % _ring.Length];
            return result;
        }
    }

    public void Flush()
    {
        lock (_lock) FlushLocked();
    }

    private void FlushLocked()
    {
        try
        {
            if (_dirty) _writer?.Flush();
            _dirty = false;
        }
        catch (Exception)
        {
            DisableFile();
        }
    }

    private void WriteToFile(string line)
    {
        if (_writer is null) return;
        try
        {
            if (_fileSize > _maxFileBytes)
            {
                CloseFile();
                Rotate();
                OpenFile();
                if (_writer is null) return;
            }
            _writer.WriteLine(line);
            _fileSize += line.Length + 2;
            _dirty = true;
        }
        catch (Exception)
        {
            if (++_fileFailures > 3) DisableFile();
        }
    }

    private void OpenFile()
    {
        try
        {
            Directory.CreateDirectory(_directory!);
            var path = Path.Combine(_directory!, "designmage.log");
            // Shared, so a second copy of the program (or a text editor) never stops logging.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _fileSize = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (_fileSize > _maxFileBytes)
            {
                CloseFile();
                Rotate();
                OpenFile();
            }
        }
        catch (Exception)
        {
            _writer = null;
        }
    }

    private void Rotate()
    {
        try
        {
            string P(int n) => Path.Combine(_directory!, n == 0 ? "designmage.log" : $"designmage.{n}.log");
            if (File.Exists(P(_keepFiles - 1))) File.Delete(P(_keepFiles - 1));
            for (int n = _keepFiles - 2; n >= 0; n--)
                if (File.Exists(P(n))) File.Move(P(n), P(n + 1), overwrite: true);
        }
        catch (Exception)
        {
            // Another copy of the program may hold the file; keep appending to it.
        }
    }

    private void CloseFile()
    {
        try { _writer?.Dispose(); }
        catch (Exception) { }
        _writer = null;
    }

    private void DisableFile() => CloseFile();

    public void Dispose()
    {
        _flushTimer?.Dispose();
        lock (_lock)
        {
            FlushLocked();
            CloseFile();
            _disposed = true;
        }
    }
}

/// <summary>The program-wide log. Memory-only until <see cref="Initialize"/> adds the file.</summary>
public static class AppLog
{
    public static LogService Service { get; private set; } = new();

    /// <summary>Starts the rolling file in <paramref name="directory"/>. Call once, first thing at startup.</summary>
    public static void Initialize(string directory, int ringCapacity = 2000)
    {
        var old = Service;
        Service = new LogService(ringCapacity, directory);
        old.Dispose();
    }

    /// <summary>Replaces the log (tests use a private one).</summary>
    public static void Use(LogService service) => Service = service;

    public static void Info(string category, string message) => Service.Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Service.Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Service.Write(LogLevel.Error, category, message);
    public static void Debug(string category, string message) => Service.Write(LogLevel.Debug, category, message);

    /// <summary>A caught error or exception: the message, then the exception with its stack.</summary>
    public static void Error(string category, string message, Exception ex) => Service.Write(LogLevel.Error, category, $"{message}: {ex}");

    /// <summary>A command run from a menu, toolbar button or key (by command id).</summary>
    public static void Command(string id) => Service.Write(LogLevel.Info, "cmd", id);

    public static IReadOnlyList<string> Tail(int count) => Service.Tail(count);
    public static void Flush() => Service.Flush();
}
