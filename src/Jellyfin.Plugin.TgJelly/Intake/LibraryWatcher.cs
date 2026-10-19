using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Library;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Turns Jellyfin's item callbacks into media intake (spec §7.4 step 1). Callbacks are O(1), never throw and never do
/// I/O (§5.4 rule 3). Also tells the Aggregator whether a scan is running.
/// </summary>
public sealed class LibraryWatcher : BackgroundService, IScanState
{
    private readonly ILibraryManager _library;
    private readonly IntakeQueues _queues;
    private readonly PendingIdSet _pending;
    private readonly CompatResult _compat;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly Lock _gate = new();
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWatcher"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="queues">The intake queues.</param>
    /// <param name="pending">The pending-ID set.</param>
    /// <param name="compat">The compatibility result; an incompatible build never subscribes.</param>
    /// <param name="logger">The logger.</param>
    public LibraryWatcher(ILibraryManager library, IntakeQueues queues, PendingIdSet pending, CompatResult compat, ILogger<LibraryWatcher> logger)
    {
        _library = library;
        _queues = queues;
        _pending = pending;
        _compat = compat;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsScanRunning => _library.IsScanRunning;

    /// <summary>
    /// Gets or sets how a track's album is found (replaced in tests).
    /// </summary>
    internal Func<Audio, Guid> AlbumOf { get; set; } = JellyfinItems.AlbumOf;

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly library watcher did not stop cleanly");
        }
    }

    /// <summary>
    /// Handles <see cref="ILibraryManager.ItemAdded"/>.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event.</param>
    internal void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var facts = JellyfinItems.Describe(e.Item, AlbumOf);
            switch (MediaFilter.Classify(facts))
            {
                case FilterVerdict.Keep:
                    _pending.Add(facts.PendingId);
                    _queues.PublishMedia(MediaIntakeKind.Added, facts);
                    break;
                case FilterVerdict.SeriesContext:
                    _queues.PublishMedia(MediaIntakeKind.SeriesAdded, facts);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not take an added item");
        }
    }

    /// <summary>
    /// Handles <see cref="ILibraryManager.ItemUpdated"/>: forwarded only for pending items whose metadata is ready (F13).
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event.</param>
    internal void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var facts = JellyfinItems.Describe(e.Item, AlbumOf);
            if (_pending.Contains(facts.PendingId) && facts.DateLastRefreshed > DateTime.MinValue)
            {
                _queues.PublishMedia(MediaIntakeKind.Updated, facts);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not take an updated item");
        }
    }

    /// <summary>
    /// Handles <see cref="ILibraryManager.ItemRemoved"/>: records the identities of removed items (upgrade detection).
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event.</param>
    internal void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var facts = JellyfinItems.Describe(e.Item, AlbumOf);
            if (MediaFilter.IsAnnounced(facts.Kind) && !facts.IsVirtual)
            {
                _queues.PublishMedia(MediaIntakeKind.Removed, facts);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not take a removed item");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("library watcher", RunAsync, TimeProvider.System, _logger, stoppingToken);

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
                _library.ItemAdded += OnItemAdded;
                _library.ItemUpdated += OnItemUpdated;
                _library.ItemRemoved += OnItemRemoved;
                _subscribed = true;
            }
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }

            _library.ItemAdded -= OnItemAdded;
            _library.ItemUpdated -= OnItemUpdated;
            _library.ItemRemoved -= OnItemRemoved;
            _subscribed = false;
        }
    }
}
