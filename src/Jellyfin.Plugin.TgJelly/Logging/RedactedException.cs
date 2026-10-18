using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.TgJelly.Logging;

/// <summary>
/// Wraps an exception for logging, so its message and its <see cref="ToString"/> output (what log sinks print,
/// including inner exceptions) have bot tokens redacted.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Only ever wraps an existing exception for logging.")]
public sealed class RedactedException : Exception
{
    private readonly Exception _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedactedException"/> class.
    /// </summary>
    /// <param name="inner">The exception to log.</param>
    public RedactedException(Exception inner)
        : base(SecretRedactor.Redact(inner?.Message))
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public override string? StackTrace => _inner.StackTrace;

    /// <inheritdoc />
    public override string ToString() => SecretRedactor.Redact(_inner.ToString());
}
