using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// The event log (design §8): one row per message per destination, kept in memory and written to
/// <c>event-log.jsonl</c> by a background writer, so a slow or failing disk never delays a message. Rows older than
/// <see cref="Retention"/> and beyond <see cref="MaxRows"/> are dropped, oldest first. A missing, unreadable or newer
/// file never stops anything: it is moved aside as <c>.corrupt</c> and the log starts empty, saying so.
/// </summary>
public sealed class EventLog : IDisposable
{
    /// <summary>The most rows kept.</summary>
    public const int MaxRows = 5_000;

    /// <summary>The file format this build reads and writes.</summary>
    public const int FormatVersion = 1;

    private const string HeaderProperty = "tgjellyEventLog";

    /// <summary>How long rows are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly TimeSpan AgePruneInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions LineOptions = new(JsonDefaults.Options) { WriteIndented = false };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<EventLog> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly Dictionary<string, LogEntry> _rows = new(StringComparer.Ordinal);
    private readonly AsyncSignal _signal = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _load;
    private bool _loaded;
    private bool _dirty;
    private bool _startedEmpty;
    private bool _disposed;
    private DateTimeOffset _lastAgePrune = DateTimeOffset.MinValue;
    private string? _lastWriteError;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventLog"/> class. Nothing is read until the log is first used.
    /// </summary>
    /// <param name="paths">The data folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public EventLog(DataPaths paths, TimeProvider time, ILogger<EventLog> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.EventLog;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a task that completes once the file has been read (starting the log if needed).</summary>
    internal Task Loaded
    {
        get
        {
            EnsureStarted();
            return _load!;
        }
    }

    /// <summary>Gets or sets how long the writer waits after a change, so a burst becomes one write.</summary>
    internal TimeSpan WriteDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets how the file is written (replaced in tests).</summary>
    internal Action<string, string> WriteFile { get; set; } = (path, text) => AtomicFile.WriteAllText(path, text);

    /// <summary>
    /// Adds a row, or replaces the row with the same ID. Never waits on the disk.
    /// </summary>
    /// <param name="entry">The row.</param>
    public void Upsert(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Change(() =>
        {
            _rows[entry.Id] = entry;
            return true;
        });
    }

    /// <summary>
    /// Returns a page of rows, newest first, with the facts the Log's top line needs.
    /// </summary>
    /// <param name="query">The filters and paging.</param>
    /// <returns>The page.</returns>
    public LogPage Query(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureStarted();
        var now = _time.GetUtcNow();
        LogPage page;
        bool pruned;
        lock (_gate)
        {
            pruned = Prune(now, force: true);
            var rows = _rows.Values;
            var lastSent = rows.Where(r => IsSent(r.Result)).Select(r => (DateTimeOffset?)r.UpdatedAt).DefaultIfEmpty().Max();
            var lastEvent = rows.Select(r => (DateTimeOffset?)r.At).DefaultIfEmpty().Max();
            var items = rows
                .Where(r => (!query.ProblemsOnly || IsProblem(r))
                    && (query.DestinationId is null || string.Equals(r.DestinationId, query.DestinationId, StringComparison.Ordinal))
                    && (query.EventKey is null || string.Equals(r.EventKey, query.EventKey, StringComparison.Ordinal))
                    && (query.Before is not { } before || r.At < before))
                .OrderByDescending(r => r.At)
                .ThenByDescending(r => r.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, query.Limit))
                .ToList();
            page = new LogPage(items, lastSent, lastEvent, _startedEmpty);
        }

        if (pruned)
        {
            _signal.Set();
        }

        return page;
    }

    /// <summary>
    /// Returns a row.
    /// </summary>
    /// <param name="id">The row ID.</param>
    /// <returns>The row, or <c>null</c>.</returns>
    public LogEntry? Find(string id)
    {
        EnsureStarted();
        lock (_gate)
        {
            return _rows.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Changes a row atomically: <paramref name="change"/> gets the current row (or <c>null</c>) and returns the new
    /// one, or <c>null</c> to leave it as it is.
    /// </summary>
    /// <param name="id">The row ID.</param>
    /// <param name="change">The change.</param>
    public void Update(string id, Func<LogEntry?, LogEntry?> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Change(() =>
        {
            if (change(_rows.GetValueOrDefault(id)) is not { } updated)
            {
                return false;
            }

            _rows.Remove(id);
            _rows[updated.Id] = updated;
            return true;
        });
    }

    /// <summary>
    /// Changes every row that matches.
    /// </summary>
    /// <param name="match">Which rows.</param>
    /// <param name="change">The change.</param>
    public void UpdateWhere(Func<LogEntry, bool> match, Func<LogEntry, LogEntry> change)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(change);
        Change(() =>
        {
            var hits = _rows.Values.Where(match).ToList();
            foreach (var row in hits)
            {
                _rows[row.Id] = change(row);
            }

            return hits.Count > 0;
        });
    }

    /// <summary>
    /// Removes rows.
    /// </summary>
    /// <param name="ids">The row IDs.</param>
    public void Remove(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Change(() =>
        {
            var removed = false;
            foreach (var id in ids)
            {
                removed |= _rows.Remove(id);
            }

            return removed;
        });
    }

    /// <summary>
    /// Writes pending changes now, on the calling thread (shutdown and tests).
    /// </summary>
    public void Flush()
    {
        try
        {
            WriteNow();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("TgJelly could not write the event log: {Error}", ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stop.Cancel();
        Flush();
        _stop.Dispose();
    }

    private static bool IsSent(LogResult result)
        => result is LogResult.Sent or LogResult.SentLate or LogResult.SentWithBuiltIn or LogResult.SentWithoutPhoto;

    private static bool IsProblem(LogEntry row)
        => row.Result is LogResult.Failed or LogResult.SentLate or LogResult.SentWithBuiltIn or LogResult.SentWithoutPhoto
            || (row.Result == LogResult.Waiting && row.Telegram is not null);

    private static List<LogEntry>? Parse(string[] lines)
    {
        if (lines.Length == 0)
        {
            return null;
        }

        try
        {
            using (var header = JsonDocument.Parse(lines[0]))
            {
                if (header.RootElement.ValueKind != JsonValueKind.Object
                    || !header.RootElement.TryGetProperty(HeaderProperty, out var version)
                    || version.ValueKind != JsonValueKind.Number
                    || version.GetInt32() != FormatVersion)
                {
                    return null;
                }
            }

            var rows = new List<LogEntry>(lines.Length);
            foreach (var line in lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                if (JsonSerializer.Deserialize<LogEntry>(line, LineOptions) is not { Id.Length: > 0 } row)
                {
                    return null;
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_load is not null)
            {
                return;
            }

            _load = Task.Run(Load);
            _ = Task.Run(WriteLoopAsync);
        }
    }

    private void Change(Func<bool> change)
    {
        EnsureStarted();
        var now = _time.GetUtcNow();
        bool changed;
        lock (_gate)
        {
            changed = change();
            changed |= Prune(now, force: false);
            _dirty |= changed;
        }

        if (changed)
        {
            _signal.Set();
        }
    }

    // Under _gate. Returns whether a row was removed.
    private bool Prune(DateTimeOffset now, bool force)
    {
        var removed = false;
        if (force || now - _lastAgePrune >= AgePruneInterval)
        {
            _lastAgePrune = now;
            var cutoff = now - Retention;
            foreach (var id in _rows.Values.Where(r => r.At < cutoff).Select(r => r.Id).ToList())
            {
                removed |= _rows.Remove(id);
            }
        }

        var over = _rows.Count - MaxRows;
        if (over == 1)
        {
            removed |= _rows.Remove(_rows.Values.MinBy(r => r.At)!.Id);
        }
        else if (over > 1)
        {
            foreach (var id in _rows.Values.OrderBy(r => r.At).Take(over).Select(r => r.Id).ToList())
            {
                removed |= _rows.Remove(id);
            }
        }

        if (removed)
        {
            _dirty = true;
        }

        return removed;
    }

    private void Load()
    {
        List<LogEntry>? rows = null;
        var empty = true;
        try
        {
            if (File.Exists(_path))
            {
                rows = Parse(File.ReadAllLines(_path));
                if (rows is null)
                {
                    MoveAside("it is unreadable or was written by a newer version of TgJelly");
                }
                else
                {
                    empty = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoveAside(ex.Message);
        }

        var dirty = false;
        lock (_gate)
        {
            foreach (var row in rows ?? [])
            {
                // A row recorded before the file was read is newer than the file's copy.
                _rows.TryAdd(row.Id, row);
            }

            _startedEmpty = empty;
            _loaded = true;
            Prune(_time.GetUtcNow(), force: true);
            dirty = _dirty;
        }

        if (dirty)
        {
            _signal.Set();
        }
    }

    private void MoveAside(string reason)
    {
        _logger.LogWarning("TgJelly's event log could not be read ({Reason}); it starts empty and the old file is kept as {File}", reason, _path + ".corrupt");
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("TgJelly could not move the unreadable event log aside: {Error}", ex.Message);
        }
    }

    private async Task WriteLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _signal.WaitAsync(Timeout.InfiniteTimeSpan, TimeProvider.System, token).ConfigureAwait(false);
                await _load!.ConfigureAwait(false);
                if (WriteDelay > TimeSpan.Zero)
                {
                    await Task.Delay(WriteDelay, token).ConfigureAwait(false);
                }

                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping: Dispose writes what is left.
        }
        catch (ObjectDisposedException)
        {
            // Stopping.
        }
    }

    private void WriteNow()
    {
        lock (_writeGate)
        {
            LogEntry[] snapshot;
            lock (_gate)
            {
                if (!_loaded || !_dirty)
                {
                    return;
                }

                snapshot = [.. _rows.Values.OrderBy(r => r.At)];
                _dirty = false;
            }

            var text = new StringBuilder();
            text.Append("{\"").Append(HeaderProperty).Append("\":").Append(FormatVersion).Append("}\n");
            foreach (var row in snapshot)
            {
                text.Append(JsonSerializer.Serialize(row, LineOptions)).Append('\n');
            }

            try
            {
                WriteFile(_path, text.ToString());
                _lastWriteError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    _dirty = true;
                }

                // Once per distinct problem, so a stuck disk doesn't flood Jellyfin's log; the rows stay in memory.
                if (ex.Message != _lastWriteError)
                {
                    _lastWriteError = ex.Message;
                    _logger.LogWarning("TgJelly could not write the event log; it will try again with the next change: {Error}", ex.Message);
                }
            }
        }
    }
}
