using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Hosting;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Watches <see cref="ITaskManager.TaskCompleted"/> (F18): failed tasks become <c>server.task_failed</c>, and the
/// scheduled library scan is the fallback <c>ScanFinished</c> (§7.4 step 5).
/// </summary>
public sealed class TaskWatcher : BackgroundService
{
    private const string RefreshLibraryKey = "RefreshLibrary";

    private readonly ITaskManager _tasks;
    private readonly IntakeQueues _queues;
    private readonly CompatResult _compat;
    private readonly ILogger<TaskWatcher> _logger;
    private readonly Lock _gate = new();
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWatcher"/> class.
    /// </summary>
    /// <param name="tasks">Jellyfin's task manager.</param>
    /// <param name="queues">The intake queues.</param>
    /// <param name="compat">The compatibility result.</param>
    /// <param name="logger">The logger.</param>
    public TaskWatcher(ITaskManager tasks, IntakeQueues queues, CompatResult compat, ILogger<TaskWatcher> logger)
    {
        _tasks = tasks;
        _queues = queues;
        _compat = compat;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                _tasks.TaskCompleted -= OnTaskCompleted;
                _subscribed = false;
            }
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly task watcher did not stop cleanly");
        }
    }

    /// <summary>
    /// Handles a finished task.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event.</param>
    internal void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        try
        {
            var result = e.Result;
            if (result is null)
            {
                return;
            }

            if (result.Status == TaskCompletionStatus.Failed)
            {
                _queues.Publish("server.task_failed", Consumers.ConsumerSupport.Values(
                    (EventFields.Task, result.Name ?? e.Task?.Name),
                    (EventFields.Error, result.ErrorMessage)));
            }

            if (string.Equals(result.Key, RefreshLibraryKey, StringComparison.Ordinal))
            {
                _queues.PublishMedia(MediaIntakeKind.ScanFinished, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not take a finished task");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("task watcher", RunAsync, TimeProvider.System, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_compat.IsCompatible)
        {
            return;
        }

        lock (_gate)
        {
            if (!_subscribed)
            {
                _tasks.TaskCompleted += OnTaskCompleted;
                _subscribed = true;
            }
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }
}
