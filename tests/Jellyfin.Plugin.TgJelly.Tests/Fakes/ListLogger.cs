using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps every entry, for asserting what was logged.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public (LogLevel Level, string Message, Exception? Exception)[] Entries => _entries.ToArray();

    public int Count(LogLevel level) => _entries.Count(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
