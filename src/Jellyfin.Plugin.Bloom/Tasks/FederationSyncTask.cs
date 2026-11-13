using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Bloom.Tasks;

/// <summary>
/// Periodic sync of every friend (catches anything a change notification missed).
/// </summary>
public class FederationSyncTask : IScheduledTask
{
    private readonly SyncService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSyncTask"/> class.
    /// </summary>
    /// <param name="sync">Sync service.</param>
    public FederationSyncTask(SyncService sync)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public string Name => Text.Get("TaskName");

    /// <inheritdoc />
    public string Key => "BloomSync";

    /// <inheritdoc />
    public string Description => Text.Get("TaskDescription");

    /// <inheritdoc />
    public string Category => "Bloom";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _sync.SyncAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
