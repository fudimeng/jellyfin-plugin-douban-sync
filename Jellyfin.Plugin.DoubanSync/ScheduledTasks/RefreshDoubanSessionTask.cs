using Jellyfin.Plugin.DoubanSync.Configuration;
using Jellyfin.Plugin.DoubanSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.ScheduledTasks;

/// <summary>
/// Refreshes configured Douban sessions once per day at a persisted random time.
/// </summary>
public sealed partial class RefreshDoubanSessionTask : IScheduledTask
{
    /// <summary>
    /// The interval used to check whether a randomly scheduled refresh is due.
    /// </summary>
    internal static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(30);

    private readonly IDoubanClient _doubanClient;
    private readonly ICookieProtector _cookieProtector;
    private readonly INotificationService _notificationService;
    private readonly ILogger<RefreshDoubanSessionTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshDoubanSessionTask"/> class.
    /// </summary>
    /// <param name="doubanClient">Douban web client.</param>
    /// <param name="cookieProtector">Cookie encryption service.</param>
    /// <param name="notificationService">Invalid-Cookie notification service.</param>
    /// <param name="logger">Logger.</param>
    public RefreshDoubanSessionTask(
        IDoubanClient doubanClient,
        ICookieProtector cookieProtector,
        INotificationService notificationService,
        ILogger<RefreshDoubanSessionTask> logger)
    {
        _doubanClient = doubanClient;
        _cookieProtector = cookieProtector;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "DoubanDailyRandomSessionRefresh";

    /// <inheritdoc />
    public string Name => "每日随机刷新豆瓣登录状态";

    /// <inheritdoc />
    public string Description => "每天在随机时间验证豆瓣登录状态并保存豆瓣返回的 Cookie 更新。";

    /// <inheritdoc />
    public string Category => "豆瓣同步";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = DefaultCheckInterval.Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        if (!configuration.DailyTokenRefreshEnabled || configuration.Accounts.Length == 0)
        {
            progress.Report(100);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var changed = false;
        for (var index = 0; index < configuration.Accounts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = configuration.Accounts[index];
            if (account.NextTokenRefreshUtc is null)
            {
                if (string.IsNullOrEmpty(account.TokenRefreshError))
                {
                    account.NextTokenRefreshUtc = CreateInitialRefreshUtc(
                        nowUtc,
                        Random.Shared.Next(5, 24 * 60 + 1));
                    changed = true;
                }

                progress.Report((index + 1) * 100d / configuration.Accounts.Length);
                continue;
            }

            if (account.NextTokenRefreshUtc > nowUtc)
            {
                progress.Report((index + 1) * 100d / configuration.Accounts.Length);
                continue;
            }

            changed = true;
            try
            {
                var cookie = _cookieProtector.Unprotect(account.ProtectedCookie);
                var result = await _doubanClient.RefreshSessionAsync(
                    cookie,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(result.Cookie, cookie, StringComparison.Ordinal))
                {
                    account.ProtectedCookie = _cookieProtector.Protect(result.Cookie);
                    account.CookieUpdatedAtUtc = nowUtc;
                    account.InvalidCookieNotifiedForUpdateUtc = null;
                }

                account.DoubanDisplayName = result.DisplayName;
                account.LastTokenRefreshUtc = nowUtc;
                account.TokenRefreshError = string.Empty;
                account.NextTokenRefreshUtc = CreateNextDailyRefreshUtc(
                    nowUtc,
                    TimeZoneInfo.Local,
                    Random.Shared.Next(24 * 60));
                LogRefreshSucceeded(_logger, account.JellyfinUserId, account.NextTokenRefreshUtc.Value);
            }
            catch (DoubanAuthenticationException ex)
            {
                await SuspendRefreshAsync(
                    configuration,
                    account,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await SuspendRefreshAsync(
                    configuration,
                    account,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DoubanRequestException or HttpRequestException)
            {
                ScheduleAfterTemporaryFailure(account, nowUtc, ex.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ScheduleAfterTemporaryFailure(account, nowUtc, "豆瓣请求超时。");
            }

            progress.Report((index + 1) * 100d / configuration.Accounts.Length);
        }

        if (changed)
        {
            Plugin.Instance.SaveConfiguration();
        }
    }

    internal static DateTime CreateInitialRefreshUtc(DateTime nowUtc, int delayMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delayMinutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delayMinutes, 24 * 60);
        return nowUtc.AddMinutes(delayMinutes);
    }

    internal static DateTime CreateNextDailyRefreshUtc(
        DateTime nowUtc,
        TimeZoneInfo timeZone,
        int minuteOfDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minuteOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minuteOfDay, 24 * 60);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            timeZone);
        var nextLocal = DateTime.SpecifyKind(
            localNow.Date.AddDays(1).AddMinutes(minuteOfDay),
            DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(nextLocal))
        {
            nextLocal = nextLocal.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, timeZone);
    }

    private void ScheduleAfterTemporaryFailure(
        DoubanAccountConfiguration account,
        DateTime nowUtc,
        string message)
    {
        account.TokenRefreshError = message;
        account.NextTokenRefreshUtc = CreateNextDailyRefreshUtc(
            nowUtc,
            TimeZoneInfo.Local,
            Random.Shared.Next(24 * 60));
        LogRefreshDeferred(_logger, account.JellyfinUserId, message, account.NextTokenRefreshUtc.Value);
    }

    private async Task NotifyInvalidCookieOnceAsync(
        PluginConfiguration configuration,
        DoubanAccountConfiguration account,
        string message,
        CancellationToken cancellationToken)
    {
        if (account.InvalidCookieNotifiedForUpdateUtc == account.CookieUpdatedAtUtc)
        {
            return;
        }

        var delivered = await _notificationService.NotifyCookieInvalidAsync(
            configuration,
            account,
            message,
            cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            account.InvalidCookieNotifiedForUpdateUtc = account.CookieUpdatedAtUtc;
        }
    }

    private async Task SuspendRefreshAsync(
        PluginConfiguration configuration,
        DoubanAccountConfiguration account,
        string message,
        CancellationToken cancellationToken)
    {
        account.TokenRefreshError = message;
        account.NextTokenRefreshUtc = null;
        LogRefreshSuspended(_logger, account.JellyfinUserId, message);
        await NotifyInvalidCookieOnceAsync(
            configuration,
            account,
            message,
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Refreshed Douban session for Jellyfin user {UserId}; next refresh at {NextRefreshUtc}")]
    private static partial void LogRefreshSucceeded(
        ILogger logger,
        Guid userId,
        DateTime nextRefreshUtc);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Suspended Douban session refresh for Jellyfin user {UserId}: {FailureMessage}")]
    private static partial void LogRefreshSuspended(
        ILogger logger,
        Guid userId,
        string failureMessage);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Deferred Douban session refresh for Jellyfin user {UserId}: {FailureMessage}; next attempt at {NextRefreshUtc}")]
    private static partial void LogRefreshDeferred(
        ILogger logger,
        Guid userId,
        string failureMessage,
        DateTime nextRefreshUtc);
}
