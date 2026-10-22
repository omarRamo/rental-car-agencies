using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Tests.Fakes;

/// <summary>A logger provider that keeps every entry (category, level, formatted message) for the assertions of a test.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    /// <summary>The warnings logged by the category whose name ends with <paramref name="category"/> (a type name).</summary>
    public IReadOnlyList<string> Warnings(string category) =>
        [.. _entries.Where(e => e.Level == LogLevel.Warning && e.Category.EndsWith(category, StringComparison.Ordinal)).Select(e => e.Message)];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
