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
}
