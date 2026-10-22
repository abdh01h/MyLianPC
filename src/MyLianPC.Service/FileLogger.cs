using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MyLianPC.Service;

/// <summary>Minimal daily-rolling file logger (keeps 14 days) plus an in-memory tail for diagnostics.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _write = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private readonly string _dir;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in new DirectoryInfo(dir).GetFiles("service-*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) f.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> Tail => [.. _tail];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName.Split('.').Last());

    internal void Write(LogLevel level, string category, string message, Exception? e)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString()[..4].ToUpperInvariant()}] {category}: {message}{(e is null ? "" : " | " + e.GetType().Name + ": " + e.Message)}";
        _tail.Enqueue(line);
        while (_tail.Count > 400) _tail.TryDequeue(out _);
        lock (_write)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"service-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() { }

    private sealed class Logger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
