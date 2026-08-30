using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aperture.Api.Tests;

/// <summary>One emitted log event, kept in the shape the tests assert against.</summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    int EventId,
    string Message,
    IReadOnlyDictionary<string, object?> State)
{
    /// <summary>The value of a structured field, or null when the event did not carry it.</summary>
    public object? Field(string name) => State.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Collects what the host logs, so a test can assert on the deny <em>reason</em> rather than on
/// the 401 that every reason produces.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    /// <summary>
    /// Safe because the tests share one fixture and xunit runs a collection serially. A test
    /// that clears here and asserts below cannot be interleaved with another.
    /// </summary>
    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var fields = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.Where(p => p.Key != "{OriginalFormat}")
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : [];

            entries.Enqueue(new CapturedLog(
                category, logLevel, eventId.Id, formatter(state, exception), fields));
        }
    }
}
