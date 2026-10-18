using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Holds state.json and queue.json in memory and writes them on schedule (spec §10.3, §10.4).
/// Call <see cref="Load"/> once, after <see cref="InstanceGate"/> owns the data folder.
/// </summary>
public sealed class StateStore
{
    /// <summary>How many load warnings are kept (oldest dropped first), like the other capped status lists.</summary>
    public const int MaxLoadWarnings = 20;

    /// <summary>How long queue.json may stay dirty before it is written.</summary>
    public static readonly TimeSpan QueueWriteInterval = TimeSpan.FromSeconds(10);

    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<StateStore> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private StateDocument _state = new();
    private QueueDocument _queue = new();
    private bool _loaded;
    private bool _stateDirty;
    private bool _queueDirty;
    private bool _queueUrgent;
    private DateTimeOffset _lastQueueWrite = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public StateStore(DataPaths paths, TimeProvider time, ILogger<StateStore> logger)
    {
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether <see cref="Load"/> has run.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>
    /// Reads both files. Unreadable files are moved aside and replaced by empty documents.
    /// </summary>
    public void Load()
    {
        var now = _time.GetUtcNow();
        var state = ReadOrDefault<StateDocument>(_paths.State, now, out var stateWarning);
        var queue = ReadOrDefault<QueueDocument>(_paths.Queue, now, out var queueWarning);
        lock (_gate)
        {
            _state = state;
            _queue = queue;
            foreach (var warning in new[] { stateWarning, queueWarning })
            {
                if (warning is not null)
                {
                    _state.Status.LoadWarnings.Add(warning);
                    _stateDirty = true;
                }
            }

            while (_state.Status.LoadWarnings.Count > MaxLoadWarnings)
            {
                _state.Status.LoadWarnings.RemoveAt(0);
                _stateDirty = true;
            }

            _loaded = true;
        }
    }

    /// <summary>
    /// Reads from state.json's document.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Reads the value; must not keep references to the document.</param>
    /// <returns>The value.</returns>
    public T Read<T>(Func<StateDocument, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_state);
        }
    }

    /// <summary>
    /// Changes state.json's document; it is written within 1 s.
    /// </summary>
    /// <param name="change">The change.</param>
    public void Update(Action<StateDocument> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            EnsureLoaded();
            change(_state);
            _stateDirty = true;
        }
    }

    /// <summary>
    /// Reads from queue.json's document.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Reads the value; must not keep references to the document.</param>
    /// <returns>The value.</returns>
    public T ReadQueue<T>(Func<QueueDocument, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_queue);
        }
    }

    /// <summary>
    /// Changes queue.json's document; it is written within 10 s, or within 1 s when <paramref name="urgent"/>.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="urgent"><c>true</c> after queuing a security or server entry.</param>
    public void UpdateQueue(Action<QueueDocument> change, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            EnsureLoaded();
            change(_queue);
            _queueDirty = true;
            _queueUrgent |= urgent;
        }
    }

    /// <summary>
    /// Writes whatever is due now. Called once per second by the persistence loop.
    /// </summary>
    public void FlushDue() => Flush(force: false);

    /// <summary>
    /// Writes everything that changed, e.g. on shutdown.
    /// </summary>
    public void FlushAll() => Flush(force: true);

    private void Flush(bool force)
    {
        lock (_writeGate)
        {
            var now = _time.GetUtcNow();
            string? stateJson = null;
            string? queueJson = null;
            lock (_gate)
            {
                if (!_loaded)
                {
                    return;
                }

                if (_stateDirty)
                {
                    stateJson = JsonSerializer.Serialize(_state, JsonDefaults.Options);
                    _stateDirty = false;
                }

                if (_queueDirty && (force || _queueUrgent || now - _lastQueueWrite >= QueueWriteInterval))
                {
                    queueJson = JsonSerializer.Serialize(_queue, JsonDefaults.Options);
                    _queueDirty = false;
                    _queueUrgent = false;
                    _lastQueueWrite = now;
                }
            }

            // Each file is written on its own. A failed write (e.g. a read-only data folder) marks only that
            // file dirty again, so no change is lost; the first error is rethrown after both attempts.
            Exception? firstError = null;
            if (stateJson is not null)
            {
                try
                {
                    AtomicFile.WriteAllText(_paths.State, stateJson);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _stateDirty = true;
                    }

                    firstError ??= ex;
                }
            }

            if (queueJson is not null)
            {
                try
                {
                    AtomicFile.WriteAllText(_paths.Queue, queueJson);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _queueDirty = true;
                        _queueUrgent = true;
                    }

                    firstError ??= ex;
                }
            }

            if (firstError is not null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("TgJelly state is not loaded yet.");
        }
    }

    private T ReadOrDefault<T>(string path, DateTimeOffset now, out string? warning)
        where T : new()
    {
        warning = null;
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            var moved = AtomicFile.Quarantine(path, now);
            warning = Path.GetFileName(path) + " was unreadable and has been moved to " + Path.GetFileName(moved) + ".";
            _logger.LogError(ex, "TgJelly {File} was unreadable and has been moved to {Path}", Path.GetFileName(path), moved);
            return new T();
        }
    }
}
