using System;

namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// The outcome of <see cref="CompatGuard.Evaluate(Version)"/>.
/// </summary>
/// <param name="IsCompatible">Whether intake, the bot and the outbox may run.</param>
/// <param name="BuildLabel">The Jellyfin line this build targets, e.g. "10.11" or "12".</param>
/// <param name="ServerVersion">The running server version.</param>
/// <param name="Message">The dashboard banner text when incompatible; otherwise <c>null</c>.</param>
public sealed record CompatResult(bool IsCompatible, string BuildLabel, Version ServerVersion, string? Message);
