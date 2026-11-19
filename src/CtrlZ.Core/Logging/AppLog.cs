// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;

namespace CtrlZ.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed record LogLine(DateTimeOffset Timestamp, LogLevel Level, string Message);

public interface IAppLog
{
    string LogFilePath { get; }
    string LogsFolder { get; }
    event EventHandler<LogLine>? LineWritten;
    void Write(LogLevel level, string message, Exception? exception = null);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();
    public string LogFilePath => string.Empty;
    public string LogsFolder => string.Empty;
    public event EventHandler<LogLine>? LineWritten { add { } remove { } }
    public void Write(LogLevel level, string message, Exception? exception = null) { }
}

/// <summary>
/// Small dependency-free file logger. Appends to a per-day file, rotates at 10 MB and
/// publishes lines so the Settings page can show a tail without re-reading the disk.
/// </summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _logsFolder;
    private string _currentDate = string.Empty;
    private string _filePath = string.Empty;
    private bool _disposed;

    public FileAppLog(string logsFolder)
    {
        _logsFolder = logsFolder;
        Directory.CreateDirectory(_logsFolder);
        EnsureFile();
    }

    public string LogFilePath
    {
        get { lock (_gate) { return _filePath; } }
    }

    public string LogsFolder => _logsFolder;

    public event EventHandler<LogLine>? LineWritten;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var timestamp = DateTimeOffset.Now;
        var text = new StringBuilder()
            .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append("  ")
            .Append(level.ToString().ToUpperInvariant().PadRight(5))
            .Append("  ")
            .Append(message);

        if (exception is not null)
        {
            text.AppendLine().Append(exception);
        }

        var line = text.ToString();
        var evt = new LogLine(timestamp, level, message);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureFile();
                File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false));
                if (new FileInfo(_filePath).Length > MaxFileBytes)
                {
                    Rotate();
                }
            }
            catch
            {
                // Logging must never take the application down.
            }
        }

        try { LineWritten?.Invoke(this, evt); } catch { /* subscriber failure is not fatal */ }
    }

    public IReadOnlyList<string> Tail(int maxLines = 300)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return Array.Empty<string>();
                }

                return File.ReadLines(_filePath).TakeLast(Math.Max(1, maxLines)).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private void EnsureFile()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_currentDate != today || string.IsNullOrEmpty(_filePath))
        {
            _currentDate = today;
            _filePath = Path.Combine(_logsFolder, $"ctrlz-{today}.log");
            Directory.CreateDirectory(_logsFolder);
        }
    }

    private void Rotate()
    {
        try
        {
            var archive = Path.Combine(_logsFolder,
                $"ctrlz-{_currentDate}-{DateTime.Now:HHmmss}.log");
            File.Move(_filePath, archive, overwrite: true);
            File.WriteAllText(_filePath, string.Empty);
        }
        catch
        {
            // Keep writing to the current file if rotation fails.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
