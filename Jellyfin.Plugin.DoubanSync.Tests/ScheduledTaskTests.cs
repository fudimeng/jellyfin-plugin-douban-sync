using Jellyfin.Plugin.DoubanSync.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class ScheduledTaskTests
{
    [Fact]
    public void DefaultInterval_IsThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), SyncExistingWatchedTask.DefaultInterval);
    }

    [Theory]
    [InlineData(new[] { true }, true)]
    [InlineData(new[] { true, true, true }, true)]
    [InlineData(new[] { true, false, true }, false)]
    [InlineData(new bool[0], false)]
    public void SeasonCompletion_RequiresEveryPhysicalEpisode(bool[] playedStates, bool expected)
    {
        Assert.Equal(expected, SyncExistingWatchedTask.IsSeasonComplete(playedStates));
    }

    [Fact]
    public void EpisodeGrouping_UsesSeasonIdInsteadOfSeriesParentId()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            ParentId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = 1,
            IndexNumber = 1
        };

        var grouped = SyncExistingWatchedTask.GroupEpisodesBySeason([episode]);

        Assert.True(grouped.ContainsKey(seasonId));
        Assert.False(grouped.ContainsKey(seriesId));
        Assert.Same(episode, Assert.Single(grouped[seasonId]));
    }

    [Fact]
    public void InitialTokenRefresh_UsesPersistedDelayWithinTheNextDay()
    {
        var now = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);

        var next = RefreshDoubanSessionTask.CreateInitialRefreshUtc(now, 731);

        Assert.Equal(now.AddMinutes(731), next);
    }

    [Fact]
    public void DailyTokenRefresh_UsesRandomMinuteOnTheNextLocalDate()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Test UTC+8",
            TimeSpan.FromHours(8),
            "Test UTC+8",
            "Test UTC+8");
        var now = new DateTime(2026, 10, 10, 15, 30, 0, DateTimeKind.Utc);

        var next = RefreshDoubanSessionTask.CreateNextDailyRefreshUtc(
            now,
            timeZone,
            8 * 60 + 17);

        Assert.Equal(new DateTime(2026, 10, 11, 0, 17, 0, DateTimeKind.Utc), next);
    }
}
