using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Logging;

/// <summary>
/// Registers <see cref="RedactingLogger{T}"/> for TgJelly types.
/// </summary>
public static class RedactedLoggingExtensions
{
    /// <summary>
    /// Makes <c>ILogger&lt;T&gt;</c> resolve to a <see cref="RedactingLogger{T}"/>. A closed generic registration
    /// wins over Jellyfin's open <c>ILogger&lt;&gt;</c>, and only affects <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The TgJelly type whose constructor takes the logger.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddRedactedLogger<T>(this IServiceCollection services)
        => services.AddSingleton<ILogger<T>>(sp => new RedactingLogger<T>(sp.GetRequiredService<ILoggerFactory>()));
}
