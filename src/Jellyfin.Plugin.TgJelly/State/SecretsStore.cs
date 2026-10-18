using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Reads and writes secrets.json: atomic writes, mode 600 on Unix (spec §10.2).
/// </summary>
public sealed class SecretsStore
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<SecretsStore> _logger;
    private readonly Lock _gate = new();
    private BotSecrets? _cached;
    private string? _loadWarning;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretsStore"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock, used to name quarantined files.</param>
    /// <param name="logger">The logger.</param>
    public SecretsStore(DataPaths paths, TimeProvider time, ILogger<SecretsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.Secrets;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets the warning shown in Status when secrets.json was unreadable and has been moved aside; otherwise <c>null</c>.
    /// </summary>
    public string? LoadWarning
    {
        get
        {
            lock (_gate)
            {
                return _loadWarning;
            }
        }
    }

    /// <summary>
    /// Returns the current secrets. A missing file means no secrets; an unreadable file is moved aside.
    /// </summary>
    /// <returns>The secrets.</returns>
    public BotSecrets Load()
    {
        lock (_gate)
        {
            return _cached ??= ReadFromDisk();
        }
    }

    /// <summary>
    /// Replaces the secrets.
    /// </summary>
    /// <param name="secrets">The new secrets.</param>
    public void Save(BotSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        lock (_gate)
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(secrets, JsonDefaults.Options), ownerOnly: true);
            _cached = secrets;
        }
    }

    /// <summary>
    /// Changes the secrets in one step, e.g. <c>s =&gt; s with { BotToken = "" }</c>.
    /// </summary>
    /// <param name="change">Produces the new secrets from the current ones.</param>
    public void Update(Func<BotSecrets, BotSecrets> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Save(change(Load()));
        }
    }

    private BotSecrets ReadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return BotSecrets.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<BotSecrets>(File.ReadAllText(_path), JsonDefaults.Options) ?? BotSecrets.Empty;
        }
        catch (JsonException ex)
        {
            var moved = AtomicFile.Quarantine(_path, _time.GetUtcNow());
            _loadWarning = "secrets.json was unreadable and has been moved to " + Path.GetFileName(moved) + "; enter the bot token again.";
            _logger.LogError(ex, "secrets.json was unreadable and has been moved to {Path}", moved);
            return BotSecrets.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not moved aside: the file may be fine once its permissions are fixed.
            _loadWarning = "secrets.json can't be read: " + ex.Message;
            _logger.LogError(ex, "secrets.json can't be read");
            return BotSecrets.Empty;
        }
    }
}
