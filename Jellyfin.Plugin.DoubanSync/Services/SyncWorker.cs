using Jellyfin.Plugin.DoubanSync.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Processes the persistent Douban synchronization queue.
/// </summary>
public sealed partial class SyncWorker : BackgroundService
{
    private readonly ISyncQueue _queue;
    private readonly IMovieResolver _resolver;
    private readonly IDoubanClient _doubanClient;
    private readonly ICookieProtector _cookieProtector;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SyncWorker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncWorker"/> class.
    /// </summary>
    /// <param name="queue">Persistent queue.</param>
    /// <param name="resolver">Movie resolver.</param>
    /// <param name="doubanClient">Douban web client.</param>
    /// <param name="cookieProtector">Cookie protector.</param>
    /// <param name="notificationService">Invalid-Cookie notification service.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(
        ISyncQueue queue,
        IMovieResolver resolver,
        IDoubanClient doubanClient,
        ICookieProtector cookieProtector,
        INotificationService notificationService,
        ILogger<SyncWorker> logger)
    {
        _queue = queue;
        _resolver = resolver;
        _doubanClient = doubanClient;
        _cookieProtector = cookieProtector;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _queue.GetDueJobAsync(stoppingToken).ConfigureAwait(false);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(SyncJob job, CancellationToken cancellationToken)
    {
        DoubanAccountConfiguration? account = null;
        try
        {
            var configuration = Plugin.Instance.Configuration;
            account = FindAccount(configuration, job.UserId)
                ?? throw new DoubanAuthenticationException("该 Jellyfin 用户尚未导入豆瓣 Cookie。");
            var cookie = _cookieProtector.Unprotect(account.ProtectedCookie);
            var doubanId = await _resolver.ResolveAsync(job, cookie, cancellationToken).ConfigureAwait(false);
            var markResult = await _doubanClient.MarkWatchedAsync(
                doubanId,
                cookie,
                configuration.MarkPrivate,
                configuration.ShareToBroadcast,
                cancellationToken).ConfigureAwait(false);
            await _queue.MarkSucceededAsync(job, doubanId, cancellationToken).ConfigureAwait(false);
            if (markResult == DoubanMarkResult.AlreadyWatched)
            {
                LogAlreadyWatched(_logger, job.Name, doubanId, job.UserId);
            }
            else
            {
                LogSyncSucceeded(_logger, job.Name, doubanId, job.UserId);
            }
        }
        catch (MovieMatchException ex)
        {
            await RecordFailureAsync(job, ex.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (DoubanAuthenticationException ex)
        {
            await RecordFailureAsync(job, ex.Message, false, cancellationToken).ConfigureAwait(false);
            if (account is not null)
            {
                await NotifyInvalidCookieOnceAsync(account, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            await RecordFailureAsync(job, ex.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (DoubanRequestException ex)
        {
            await RecordFailureAsync(job, ex.Message, true, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            await RecordFailureAsync(job, ex.Message, true, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(job, "豆瓣请求超时。", true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnexpectedFailure(_logger, ex, job.Name, job.UserId);
            await RecordFailureAsync(
                job,
                $"未预期错误：{ex.GetType().Name}。",
                true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyInvalidCookieOnceAsync(
        DoubanAccountConfiguration account,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (account.InvalidCookieNotifiedForUpdateUtc == account.CookieUpdatedAtUtc)
        {
            return;
        }

        var configuration = Plugin.Instance.Configuration;
        var delivered = await _notificationService.NotifyCookieInvalidAsync(
            configuration,
            account,
            failureMessage,
            cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            account.InvalidCookieNotifiedForUpdateUtc = account.CookieUpdatedAtUtc;
            Plugin.Instance.SaveConfiguration();
        }
    }

    private static DoubanAccountConfiguration? FindAccount(
        PluginConfiguration configuration,
        Guid userId)
    {
        return configuration.Accounts.FirstOrDefault(account => account.JellyfinUserId == userId);
    }

    private async Task RecordFailureAsync(
        SyncJob job,
        string error,
        bool retry,
        CancellationToken cancellationToken)
    {
        LogSyncFailed(_logger, job.Name, job.UserId, error);
        await _queue.MarkFailedAsync(job, error, retry, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Synced watched item {ItemName} to Douban subject {DoubanId} for Jellyfin user {UserId}")]
    private static partial void LogSyncSucceeded(
        ILogger logger,
        string itemName,
        string doubanId,
        Guid userId);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Skipped watched item {ItemName}; Douban subject {DoubanId} is already watched for Jellyfin user {UserId}")]
    private static partial void LogAlreadyWatched(
        ILogger logger,
        string itemName,
        string doubanId,
        Guid userId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Failed to sync watched item {ItemName} for Jellyfin user {UserId}: {FailureMessage}")]
    private static partial void LogSyncFailed(
        ILogger logger,
        string itemName,
        Guid userId,
        string failureMessage);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Unexpected error while syncing item {ItemName} for Jellyfin user {UserId}")]
    private static partial void LogUnexpectedFailure(
        ILogger logger,
        Exception exception,
        string itemName,
        Guid userId);
}
