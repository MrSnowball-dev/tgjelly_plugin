using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Logging;

/// <summary>
/// The logger every TgJelly type gets (spec §12): the formatted message and the exception are redacted with
/// <see cref="SecretRedactor"/> before they reach Jellyfin's log sinks.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
public sealed class RedactingLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedactingLogger{T}"/> class.
    /// </summary>
    /// <param name="factory">Jellyfin's logger factory.</param>
    public RedactingLogger(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inner = factory.CreateLogger(typeof(T).FullName ?? typeof(T).Name);
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!_inner.IsEnabled(logLevel))
        {
            return;
        }

        var message = SecretRedactor.Redact(formatter(state, exception));
        var redacted = exception is null ? null : new RedactedException(exception);
        _inner.Log(logLevel, eventId, message, redacted, static (m, _) => m);
    }
}
