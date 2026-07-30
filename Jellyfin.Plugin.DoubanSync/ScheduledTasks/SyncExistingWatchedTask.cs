using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DoubanSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.ScheduledTasks;

/// <summary>
/// Queues watched movies and fully watched seasons in Jellyfin.
/// </summary>
public sealed partial class SyncExistingWatchedTask : IScheduledTask
{
    /// <summary>
    /// The default interval between watched-library scans.
    /// </summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ISyncQueue _queue;
    private readonly ILogger<SyncExistingWatchedTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncExistingWatchedTask"/> class.
    /// </summary>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <param name="userDataManager">Jellyfin user-data manager.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="queue">Persistent queue.</param>
    /// <param name="logger">Logger.</param>
    public SyncExistingWatchedTask(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ISyncQueue queue,
        ILogger<SyncExistingWatchedTask> logger)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "DoubanSyncEvery30Minutes";

    /// <inheritdoc />
    public string Name => "每 30 分钟同步已观看影视到豆瓣";

    /// <inheritdoc />
    public string Description => "每 30 分钟扫描已观看电影及全部普通集已看完的剧集季，并加入豆瓣同步队列。";

    /// <inheritdoc />
    public string Category => "豆瓣同步";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = DefaultInterval.Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.Enabled)
        {
            progress.Report(100);
            return;
        }

        var configuredUserIds = Plugin.Instance.Configuration.Accounts
            .Select(account => account.JellyfinUserId)
            .ToHashSet();
        var users = UserManagerCompatibility.GetUsers(_userManager)
            .Where(user => configuredUserIds.Contains(user.Id))
            .ToArray();
        if (users.Length == 0)
        {
            progress.Report(100);
            return;
        }

        var completedUsers = 0;
        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = _libraryManager.GetItemList(
                new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    IsPlayed = true,
                    IsVirtualItem = false,
                    Recursive = true
                });

            foreach (var movie in movies.OfType<Movie>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userData = _userDataManager.GetUserData(user, movie);
                var job = SyncJob.FromMovie(user.Id, movie, userData?.LastPlayedDate);
                await _queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
            }

            var seasons = _libraryManager.GetItemList(
                    new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Season],
                        IsVirtualItem = false,
                        Recursive = true
                    })
                .OfType<Season>()
                .Where(season => season.IndexNumber.GetValueOrDefault() > 0)
                .ToArray();
            var episodesBySeason = _libraryManager.GetItemList(
                    new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true
                    })
                .OfType<Episode>()
                .Where(episode => episode.ParentIndexNumber.GetValueOrDefault() > 0)
                .GroupBy(episode => episode.ParentId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var regularSeasonCounts = seasons
                .Where(season => season.Series is not null)
                .GroupBy(season => season.Series.Id)
                .ToDictionary(group => group.Key, group => group.Count());

            var completedSeasonCount = 0;
            foreach (var season in seasons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (season.Series is not Series series
                    || !episodesBySeason.TryGetValue(season.Id, out var episodes)
                    || !IsSeasonComplete(
                        episodes.Select(episode => _userDataManager.GetUserData(user, episode)?.Played == true)))
                {
                    continue;
                }

                var lastPlayedDate = episodes
                    .Select(episode => _userDataManager.GetUserData(user, episode)?.LastPlayedDate)
                    .Where(value => value.HasValue)
                    .Max();
                var allowSeriesIdentityFallback = season.IndexNumber == 1
                    && regularSeasonCounts.TryGetValue(series.Id, out var regularSeasonCount)
                    && regularSeasonCount == 1;
                var job = SyncJob.FromSeason(
                    user.Id,
                    season,
                    series,
                    lastPlayedDate,
                    allowSeriesIdentityFallback);
                await _queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
                completedSeasonCount++;
            }

            completedUsers++;
            progress.Report(completedUsers * 100d / users.Length);
            LogItemsQueued(_logger, movies.Count, completedSeasonCount, user.Username);
        }
    }

    /// <summary>
    /// Returns whether a season contains at least one episode and every episode is played.
    /// </summary>
    /// <param name="playedStates">Played state for every physical regular-season episode.</param>
    /// <returns><see langword="true"/> when the whole season is watched.</returns>
    internal static bool IsSeasonComplete(IEnumerable<bool> playedStates)
    {
        var states = playedStates.ToArray();
        return states.Length > 0 && states.All(played => played);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Queued {MovieCount} watched movies and {SeasonCount} completed seasons for Douban synchronization for user {UserName}")]
    private static partial void LogItemsQueued(
        ILogger logger,
        int movieCount,
        int seasonCount,
        string userName);
}
