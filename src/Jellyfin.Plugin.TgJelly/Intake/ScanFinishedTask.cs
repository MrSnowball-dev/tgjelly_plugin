using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Runs at the end of every full library validation and only queues a <c>ScanFinished</c> event. Jellyfin finds
/// it by scanning (it must stay public) and creates it from the container.
/// </summary>
public sealed class ScanFinishedTask : ILibraryPostScanTask
{
    private readonly IntakeQueues _queues;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanFinishedTask"/> class.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    public ScanFinishedTask(IntakeQueues queues) => _queues = queues;

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _queues.PublishMedia(MediaIntakeKind.ScanFinished, null);
        progress?.Report(100);
        return Task.CompletedTask;
    }
}
