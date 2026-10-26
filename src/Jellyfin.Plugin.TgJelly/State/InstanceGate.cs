using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Owns <c>state.lock</c>. Until it has the lock, nothing that consumes intake or talks to Telegram may start.
/// Retries every 5 s.
/// </summary>
public sealed class InstanceGate : BackgroundService
{
    /// <summary>Status text while the lock is busy for less than 2 minutes.</summary>
    public const string WaitingForPreviousInstance = "Waiting for the previous instance to stop";

    /// <summary>Status text once the lock has been busy for 2 minutes or more.</summary>
    public const string AnotherProcessUsesFolder = "Another Jellyfin process is using TgJelly's data folder";

    /// <summary>Start of the status text when the data folder cannot be written at all.</summary>
    public const string CannotWriteFolderPrefix = "TgJelly can't write to its data folder";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForeignAfter = TimeSpan.FromMinutes(2);

    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceGate> _logger;
    private readonly TaskCompletionSource _owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private InstanceLock? _lock;
    private string _waitingMessage = WaitingForPreviousInstance;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceGate"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public InstanceGate(DataPaths paths, TimeProvider time, ILogger<InstanceGate> logger)
    {
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether this instance holds the lock.</summary>
    public bool IsOwner => _owned.Task.IsCompletedSuccessfully;

    /// <summary>Gets the status text while waiting for the lock; <c>null</c> once owned.</summary>
    public string? WaitingMessage
    {
        get
        {
            lock (_gate)
            {
                return IsOwner ? null : _waitingMessage;
            }
        }
    }

    /// <summary>
    /// Completes once this instance holds the lock.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the lock is held.</returns>
    public Task WaitForOwnershipAsync(CancellationToken cancellationToken) => _owned.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly instance gate did not stop cleanly");
        }
        finally
        {
            Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Release();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("instance gate", AcquireAsync, _time, _logger, stoppingToken);

    private async Task AcquireAsync(CancellationToken stoppingToken)
    {
        var busySince = (DateTimeOffset?)null;
        string? lastDenied = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var acquired = InstanceLock.TryAcquire(_paths.Lock, out var denied);
            if (acquired is not null)
            {
                lock (_gate)
                {
                    _lock = acquired;
                }

                _owned.TrySetResult();
                _logger.LogInformation("TgJelly owns its data folder {Path}", _paths.Root);
                return;
            }

            string message;
            if (denied is not null)
            {
                busySince = null;
                message = CannotWriteFolderPrefix + " " + _paths.Root + ": " + denied.Message + " Check the folder's owner and permissions.";
                if (message != lastDenied)
                {
                    _logger.LogError(denied, "TgJelly can't write to its data folder {Path}", _paths.Root);
                    lastDenied = message;
                }
            }
            else
            {
                busySince ??= _time.GetUtcNow();
                lastDenied = null;
                message = _time.GetUtcNow() - busySince.Value < ForeignAfter ? WaitingForPreviousInstance : AnotherProcessUsesFolder;
            }

            lock (_gate)
            {
                _waitingMessage = message;
            }

            await Task.Delay(RetryInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
