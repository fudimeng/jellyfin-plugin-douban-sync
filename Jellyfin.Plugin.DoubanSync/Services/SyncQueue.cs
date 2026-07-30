using System.Text.Json;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Persistent synchronization queue.
/// </summary>
public interface ISyncQueue
{
    /// <summary>
    /// Enqueues a watched item unless an equivalent job is already pending, completed, or suppressed.
    /// </summary>
    /// <param name="job">Job snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnqueueAsync(SyncJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the next due job without removing it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Due job or <see langword="null"/>.</returns>
    Task<SyncJob?> GetDueJobAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Marks a job as successfully completed.
    /// </summary>
    /// <param name="job">Completed job.</param>
    /// <param name="doubanId">Resolved Douban identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkSucceededAsync(SyncJob job, string doubanId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt.
    /// </summary>
    /// <param name="job">Failed job.</param>
    /// <param name="failureMessage">Safe error message.</param>
    /// <param name="retry">Whether the job should be retried automatically.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkFailedAsync(
        SyncJob job,
        string failureMessage,
        bool retry,
        CancellationToken cancellationToken);

    /// <summary>
    /// Requeues permanent failures for one user.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RequeueFailedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a safe status snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queue status.</returns>
    Task<SyncQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class SyncQueue : ISyncQueue, IDisposable
{
    private const int MaximumPendingJobs = 5000;
    private const int MaximumRecentRecords = 100;
    private const int MaximumAttempts = 5;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _statePath;
    private readonly ILogger<SyncQueue> _logger;
    private QueueState? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueue"/> class.
    /// </summary>
    /// <param name="paths">Jellyfin application paths.</param>
    /// <param name="logger">Logger.</param>
    public SyncQueue(IApplicationPaths paths, ILogger<SyncQueue> logger)
    {
        var directory = Path.Combine(paths.DataPath, "douban-sync");
        Directory.CreateDirectory(directory);
        FilePermissionHelper.RestrictDirectoryToCurrentUser(directory);
        _statePath = Path.Combine(directory, "queue.json");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(SyncJob job, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            var key = job.GetDeduplicationKey();
            if (state.Pending.Any(existing => existing.GetDeduplicationKey() == key)
                || state.CompletedKeys.Contains(key)
                || state.SuppressedKeys.Contains(key))
            {
                return;
            }

            if (state.Pending.Count >= MaximumPendingJobs)
            {
                throw new InvalidOperationException($"豆瓣同步队列已达到 {MaximumPendingJobs} 条上限。");
            }

            state.Pending.Add(job);
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncJob?> GetDueJobAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            return state.Pending
                .Where(job => job.NextAttemptUtc <= DateTime.UtcNow)
                .OrderBy(job => job.NextAttemptUtc)
                .ThenBy(job => job.WatchedAtUtc)
                .FirstOrDefault();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkSucceededAsync(
        SyncJob job,
        string doubanId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            state.Pending.RemoveAll(item => item.Id == job.Id);
            state.CompletedKeys.Add(job.GetDeduplicationKey());
            AddRecent(
                state,
                new SyncRecord
                {
                    JobId = job.Id,
                    UserId = job.UserId,
                    ItemId = job.ItemId,
                    Name = job.Name,
                    Status = "success",
                    DoubanId = doubanId
                });
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(
        SyncJob job,
        string failureMessage,
        bool retry,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            var stored = state.Pending.FirstOrDefault(item => item.Id == job.Id);
            if (stored is null)
            {
                return;
            }

            stored.Attempts++;
            if (retry && stored.Attempts < MaximumAttempts)
            {
                var delayMinutes = Math.Pow(2, stored.Attempts - 1);
                stored.NextAttemptUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
            }
            else
            {
                state.Pending.Remove(stored);
                state.SuppressedKeys.Add(stored.GetDeduplicationKey());
                AddRecent(
                    state,
                    new SyncRecord
                    {
                        JobId = stored.Id,
                        UserId = stored.UserId,
                        ItemId = stored.ItemId,
                        Name = stored.Name,
                        Status = "failed",
                        Error = failureMessage,
                        FailedJob = stored
                    });
            }

            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RequeueFailedAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            var failed = state.Recent
                .Where(record => record.UserId == userId && record.FailedJob is not null)
                .Select(record => record.FailedJob!)
                .ToArray();
            foreach (var job in failed)
            {
                job.Attempts = 0;
                job.NextAttemptUtc = DateTime.UtcNow;
                var key = job.GetDeduplicationKey();
                state.SuppressedKeys.Remove(key);
                if (!state.Pending.Any(item => item.GetDeduplicationKey() == key)
                    && !state.CompletedKeys.Contains(key))
                {
                    state.Pending.Add(job);
                }
            }

            state.Recent.RemoveAll(record => record.UserId == userId && record.FailedJob is not null);
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
            return new SyncQueueSnapshot
            {
                PendingCount = state.Pending.Count,
                Recent = state.Recent
                    .Select(
                        record => new SyncRecord
                        {
                            JobId = record.JobId,
                            UserId = record.UserId,
                            ItemId = record.ItemId,
                            Name = record.Name,
                            Status = record.Status,
                            DoubanId = record.DoubanId,
                            Error = record.Error,
                            TimestampUtc = record.TimestampUtc
                        })
                    .ToArray()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void AddRecent(QueueState state, SyncRecord record)
    {
        state.Recent.Insert(0, record);
        if (state.Recent.Count > MaximumRecentRecords)
        {
            state.Recent.RemoveRange(MaximumRecentRecords, state.Recent.Count - MaximumRecentRecords);
        }
    }

    private async Task<QueueState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
        {
            return _state;
        }

        if (!File.Exists(_statePath))
        {
            _state = new QueueState();
            return _state;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            _state = await JsonSerializer.DeserializeAsync<QueueState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ?? new QueueState();
            _state.Pending ??= [];
            _state.Recent ??= [];
            _state.CompletedKeys ??= [];
            _state.SuppressedKeys ??= [];
        }
        catch (JsonException ex)
        {
            var backupPath = _statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_statePath, backupPath);
            LogCorruptQueue(_logger, ex, backupPath);
            _state = new QueueState();
        }

        return _state;
    }

    private async Task SaveStateAsync(QueueState state, CancellationToken cancellationToken)
    {
        var temporaryPath = _statePath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporaryPath, _statePath, true);
    }

    private sealed class QueueState
    {
        public List<SyncJob> Pending { get; set; } = [];

        public List<SyncRecord> Recent { get; set; } = [];

        public HashSet<string> CompletedKeys { get; set; } = [];

        public HashSet<string> SuppressedKeys { get; set; } = [];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Douban sync queue was corrupt and was moved to {BackupPath}")]
    private static partial void LogCorruptQueue(
        ILogger logger,
        Exception exception,
        string backupPath);
}
