using Jellyfin.Plugin.DoubanSync.Services;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class MovieResolverTests
{
    [Theory]
    [InlineData("DoubanID", "1292052")]
    [InlineData("douban", "https://movie.douban.com/subject/1292052/")]
    [InlineData("MetaSharkID", "Douban_1292052")]
    public void GetDirectDoubanId_SupportsKnownProviders(string key, string value)
    {
        var providers = new Dictionary<string, string> { [key] = value };

        Assert.Equal("1292052", MovieResolver.GetDirectDoubanId(providers));
    }

    [Theory]
    [InlineData("Imdb", "tt38067963")]
    [InlineData("IMDb", "https://www.imdb.com/title/TT38067963/")]
    public void GetImdbId_SupportsKnownFormats(string key, string value)
    {
        var providers = new Dictionary<string, string> { [key] = value };

        Assert.Equal("tt38067963", MovieResolver.GetImdbId(providers));
    }

    [Fact]
    public async Task ResolveAsync_UsesVerifiedImdbMatchBeforeTitle()
    {
        var client = new FakeDoubanClient([], "36576765");
        var resolver = new MovieResolver(client);
        var job = new SyncJob
        {
            Name = "双喜",
            Year = 2026,
            ProviderIds = new Dictionary<string, string>
            {
                ["Imdb"] = "tt38067963"
            }
        };

        var result = await resolver.ResolveAsync(job, "unused", CancellationToken.None);

        Assert.Equal("36576765", result);
        Assert.Empty(client.TitleQueries);
    }

    [Fact]
    public async Task ResolveAsync_RequiresExactTitleAndYear()
    {
        var client = new FakeDoubanClient(
        [
            new DoubanMovieCandidate
            {
                Id = "1292052",
                Title = "肖申克的救赎",
                OriginalTitle = "The Shawshank Redemption",
                Year = "1994",
                Type = "movie"
            },
            new DoubanMovieCandidate
            {
                Id = "9999999",
                Title = "肖申克的救赎",
                Year = "2024",
                Type = "movie"
            }
        ]);
        var resolver = new MovieResolver(client);
        var job = new SyncJob
        {
            Name = "肖申克的救赎",
            OriginalTitle = "The Shawshank Redemption",
            Year = 1994
        };

        var result = await resolver.ResolveAsync(job, "unused", CancellationToken.None);

        Assert.Equal("1292052", result);
    }

    [Fact]
    public async Task ResolveAsync_RejectsAmbiguousCandidates()
    {
        var client = new FakeDoubanClient(
        [
            new DoubanMovieCandidate
            {
                Id = "1",
                Title = "同名电影",
                Year = "2020",
                Type = "movie"
            },
            new DoubanMovieCandidate
            {
                Id = "2",
                Title = "同名电影",
                Year = "2020",
                Type = "movie"
            }
        ]);
        var resolver = new MovieResolver(client);
        var job = new SyncJob { Name = "同名电影", Year = 2020 };

        await Assert.ThrowsAsync<MovieMatchException>(
            () => resolver.ResolveAsync(job, "unused", CancellationToken.None));
    }

    [Fact]
    public void SeasonSearchTitles_IncludeChineseAndEnglishSeasonVariants()
    {
        var job = new SyncJob
        {
            MediaKind = SyncMediaKind.Season,
            Name = "万神殿 第 1 季",
            SeriesName = "万神殿（2022）",
            SeriesOriginalTitle = "Pantheon",
            SeasonNumber = 1
        };

        var titles = MovieResolver.GetSearchTitles(job);

        Assert.Contains("万神殿 第一季", titles);
        Assert.Contains("万神殿 第1季", titles);
        Assert.Contains("Pantheon Season 1", titles);
        Assert.DoesNotContain("万神殿", titles);
    }

    [Fact]
    public void FirstAndOnlySeasonSearch_CanUseUnsuffixedSeriesTitle()
    {
        var job = new SyncJob
        {
            MediaKind = SyncMediaKind.Season,
            SeriesName = "寡妇湾",
            SeriesOriginalTitle = "Widow's Bay",
            SeasonNumber = 1,
            AllowSeriesIdentityFallback = true
        };

        var titles = MovieResolver.GetSearchTitles(job);

        Assert.Contains("寡妇湾", titles);
        Assert.Contains("Widow's Bay", titles);
    }

    [Fact]
    public async Task ResolveAsync_MatchesCompletedSeasonByExactSeasonTitleAndYear()
    {
        var client = new FakeDoubanClient(
        [
            new DoubanMovieCandidate
            {
                Id = "34990593",
                Title = "万神殿 第一季",
                OriginalTitle = "Pantheon",
                Year = "2022",
                Type = "movie"
            }
        ]);
        var resolver = new MovieResolver(client);
        var job = new SyncJob
        {
            MediaKind = SyncMediaKind.Season,
            Name = "万神殿 第 1 季",
            SeriesName = "万神殿",
            SeriesOriginalTitle = "Pantheon",
            SeasonNumber = 1,
            Year = 2022
        };

        var result = await resolver.ResolveAsync(job, "unused", CancellationToken.None);

        Assert.Equal("34990593", result);
        Assert.Contains("万神殿 第一季", client.TitleQueries);
    }

    private sealed class FakeDoubanClient : IDoubanClient
    {
        private readonly IReadOnlyList<DoubanMovieCandidate> _candidates;
        private readonly string? _imdbResult;

        public FakeDoubanClient(
            IReadOnlyList<DoubanMovieCandidate> candidates,
            string? imdbResult = null)
        {
            _candidates = candidates;
            _imdbResult = imdbResult;
        }

        public List<string> TitleQueries { get; } = [];

        public Task<string> ValidateCookieAsync(string cookie, CancellationToken cancellationToken)
            => Task.FromResult("tester");

        public Task<IReadOnlyList<DoubanMovieCandidate>> SearchMoviesAsync(
            string query,
            string cookie,
            CancellationToken cancellationToken)
        {
            TitleQueries.Add(query);
            return Task.FromResult(_candidates);
        }

        public Task<string?> FindMovieByImdbAsync(
            string imdbId,
            string cookie,
            CancellationToken cancellationToken)
            => Task.FromResult(_imdbResult);

        public Task<DoubanMarkResult> MarkWatchedAsync(
            string doubanId,
            string cookie,
            bool markPrivate,
            bool shareToBroadcast,
            CancellationToken cancellationToken)
            => Task.FromResult(DoubanMarkResult.Marked);
    }
}
