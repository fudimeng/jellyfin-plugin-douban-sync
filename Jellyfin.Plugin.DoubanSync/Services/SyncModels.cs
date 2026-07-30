using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Kind of Jellyfin item represented by a synchronization job.
/// </summary>
public enum SyncMediaKind
{
    /// <summary>
    /// A movie.
    /// </summary>
    Movie,

    /// <summary>
    /// A fully watched television or animation season.
    /// </summary>
    Season
}

/// <summary>
/// Persisted immutable snapshot of one Jellyfin watched event.
/// </summary>
public sealed class SyncJob
{
    /// <summary>
    /// Gets or sets the job identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the Jellyfin user identifier.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the media kind.
    /// </summary>
    public SyncMediaKind MediaKind { get; set; }

    /// <summary>
    /// Gets or sets the localized title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original title.
    /// </summary>
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets provider identifiers captured from Jellyfin.
    /// </summary>
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the series title for a season job.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original series title for a season job.
    /// </summary>
    public string SeriesOriginalTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the one-based season number for a season job.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets whether a season may fall back to the unsuffixed series identity.
    /// </summary>
    public bool AllowSeriesIdentityFallback { get; set; }

    /// <summary>
    /// Gets or sets when Jellyfin recorded the movie as played.
    /// </summary>
    public DateTime WatchedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the number of failed processing attempts.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets when the job becomes eligible for another attempt.
    /// </summary>
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a job snapshot from a Jellyfin movie.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <param name="movie">Jellyfin movie.</param>
    /// <param name="watchedAtUtc">Last-played timestamp.</param>
    /// <returns>New job.</returns>
    public static SyncJob FromMovie(Guid userId, Movie movie, DateTime? watchedAtUtc)
    {
        return new SyncJob
        {
            UserId = userId,
            ItemId = movie.Id,
            MediaKind = SyncMediaKind.Movie,
            Name = movie.Name,
            OriginalTitle = movie.OriginalTitle ?? string.Empty,
            Year = movie.ProductionYear,
            ProviderIds = new Dictionary<string, string>(
                movie.ProviderIds,
                StringComparer.OrdinalIgnoreCase),
            WatchedAtUtc = watchedAtUtc ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a job snapshot from a fully watched Jellyfin season.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <param name="season">Jellyfin season.</param>
    /// <param name="series">Owning Jellyfin series.</param>
    /// <param name="watchedAtUtc">Most recent episode play timestamp.</param>
    /// <param name="allowSeriesIdentityFallback">
    /// Whether the series-level identifiers can safely represent this season.
    /// </param>
    /// <returns>New job.</returns>
    public static SyncJob FromSeason(
        Guid userId,
        Season season,
        Series series,
        DateTime? watchedAtUtc,
        bool allowSeriesIdentityFallback)
    {
        var seasonNumber = season.IndexNumber
            ?? throw new ArgumentException("季缺少季号。", nameof(season));
        var providerIds = new Dictionary<string, string>(
            season.ProviderIds,
            StringComparer.OrdinalIgnoreCase);
        if (allowSeriesIdentityFallback)
        {
            foreach (var providerId in series.ProviderIds)
            {
                providerIds.TryAdd(providerId.Key, providerId.Value);
            }
        }

        return new SyncJob
        {
            UserId = userId,
            ItemId = season.Id,
            MediaKind = SyncMediaKind.Season,
            Name = $"{series.Name} 第 {seasonNumber} 季",
            OriginalTitle = season.OriginalTitle ?? string.Empty,
            SeriesName = series.Name,
            SeriesOriginalTitle = series.OriginalTitle ?? string.Empty,
            SeasonNumber = seasonNumber,
            AllowSeriesIdentityFallback = allowSeriesIdentityFallback,
            Year = season.ProductionYear ?? series.ProductionYear,
            ProviderIds = providerIds,
            WatchedAtUtc = watchedAtUtc ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets a key used to collapse alternate versions and duplicate events.
    /// </summary>
    /// <returns>Stable per-user source key.</returns>
    public string GetDeduplicationKey()
    {
        var doubanId = MovieResolver.GetDirectDoubanId(ProviderIds);
        return doubanId is null
            ? $"{UserId:N}:item:{ItemId:N}"
            : $"{UserId:N}:douban:{doubanId}";
    }
}

/// <summary>
/// Reader-facing result of a synchronization attempt.
/// </summary>
public sealed class SyncRecord
{
    /// <summary>
    /// Gets or sets the job identifier.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user identifier.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the movie title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the result status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resolved Douban identifier.
    /// </summary>
    public string DoubanId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the failure message.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the result was recorded.
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets a copy of a failed job so it can be retried after credentials change.
    /// </summary>
    public SyncJob? FailedJob { get; set; }
}

/// <summary>
/// Safe queue status exposed to the plugin settings page.
/// </summary>
public sealed class SyncQueueSnapshot
{
    /// <summary>
    /// Gets or sets the number of pending jobs.
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// Gets or sets recent results.
    /// </summary>
    public IReadOnlyList<SyncRecord> Recent { get; set; } = [];
}
