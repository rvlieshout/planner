using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Velopack.Logging;

namespace Planner.Client.Infrastructure;

/// <summary>Appends to a daily log file in the user's profile.
///
/// A desktop client fails on machines nobody can attach a debugger to, and the failure this project
/// cares about most — "the update never arrived" or "login stopped working" — leaves no trace anywhere
/// else. One file the user can attach to a support mail is worth more than a console nobody sees.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _writeLock = new();
    private readonly LogLevel _minimum;

    public FileLoggerProvider(LogLevel minimum = LogLevel.Information)
    {
        _minimum = minimum;
        AppPaths.EnsureCreated();
        TrimOldLogs();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        // Categories are namespace-qualified; the last segment is what a reader actually scans for.
        var shortCategory = category[(category.LastIndexOf('.') + 1)..];

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(shortCategory).Append(": ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(AppPaths.LogFile, line.AppendLine().ToString());
            }
            catch (IOException)
            {
                // Logging must never be the reason the app falls over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trc",
        LogLevel.Debug => "dbg",
        LogLevel.Information => "inf",
        LogLevel.Warning => "wrn",
        LogLevel.Error => "err",
        LogLevel.Critical => "crt",
        _ => "???"
    };

    /// <summary>Keeps a fortnight. Long enough to investigate "it broke last week", short enough that
    /// nobody's profile fills up.</summary>
    private static void TrimOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "planner-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

/// <summary>Bridges Velopack's logging onto the same file, so the update story reads as one narrative
/// instead of two half-stories in different places.</summary>
public sealed class VelopackLoggerAdapter(ILogger logger) : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        var level = logLevel switch
        {
            VelopackLogLevel.Trace => LogLevel.Trace,
            VelopackLogLevel.Debug => LogLevel.Debug,
            VelopackLogLevel.Information => LogLevel.Information,
            VelopackLogLevel.Warning => LogLevel.Warning,
            VelopackLogLevel.Error => LogLevel.Error,
            VelopackLogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.Debug
        };

#pragma warning disable CA2254 // The message is already formatted by Velopack.
        logger.Log(level, exception, message ?? string.Empty);
#pragma warning restore CA2254
    }
}
