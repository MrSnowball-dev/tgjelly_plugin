using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// What every consumer does: build the payload and publish it, never throwing into Jellyfin's publisher (F6, §5.4 rule 3).
/// </summary>
internal static class ConsumerSupport
{
    public static void Publish(IntakeQueues queues, ILogger logger, string eventKey, Func<Dictionary<string, string>> build)
    {
        try
        {
            queues.Publish(eventKey, build());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TgJelly could not take a {Event} event", eventKey);
        }
    }

    public static Dictionary<string, string> Values(params (string Name, string? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            result[name] = value ?? string.Empty;
        }

        return result;
    }
}
