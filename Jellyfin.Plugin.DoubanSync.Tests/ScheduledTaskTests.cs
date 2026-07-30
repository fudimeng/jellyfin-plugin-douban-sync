using Jellyfin.Plugin.DoubanSync.ScheduledTasks;

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
}
