using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Resolves a Jellyfin movie or completed-season snapshot to a Douban subject identifier.
/// </summary>
public interface IMovieResolver
{
    /// <summary>
    /// Resolves a movie or completed season.
    /// </summary>
    /// <param name="job">Synchronization job.</param>
    /// <param name="cookie">Douban Cookie header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Douban subject identifier.</returns>
    Task<string> ResolveAsync(SyncJob job, string cookie, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class MovieResolver : IMovieResolver
{
    private static readonly string[] DirectProviderKeys = ["DoubanID", "DoubanId", "Douban"];
    private static readonly string[] ImdbProviderKeys = ["Imdb", "IMDb", "IMDB"];
    private readonly IDoubanClient _doubanClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieResolver"/> class.
    /// </summary>
    /// <param name="doubanClient">Douban web client.</param>
    public MovieResolver(IDoubanClient doubanClient)
    {
        _doubanClient = doubanClient;
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(
        SyncJob job,
        string cookie,
        CancellationToken cancellationToken)
    {
        var directId = GetDirectDoubanId(job.ProviderIds);
        if (directId is not null)
        {
            return directId;
        }

        var imdbId = GetImdbId(job.ProviderIds);
        if (imdbId is not null)
        {
            var imdbMatch = await _doubanClient.FindMovieByImdbAsync(
                imdbId,
                cookie,
                cancellationToken).ConfigureAwait(false);
            if (imdbMatch is not null)
            {
                return imdbMatch;
            }
        }

        var queries = GetSearchTitles(job);
        var candidates = new Dictionary<string, DoubanMovieCandidate>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var results = await _doubanClient.SearchMoviesAsync(
                query,
                cookie,
                cancellationToken).ConfigureAwait(false);
            foreach (var candidate in results)
            {
                if (!string.IsNullOrWhiteSpace(candidate.Id) && candidate.Id.All(char.IsDigit))
                {
                    candidates[candidate.Id] = candidate;
                }
            }
        }

        var ranked = candidates.Values
            .Select(candidate => new { Candidate = candidate, Score = Score(job, candidate) })
            .OrderByDescending(entry => entry.Score)
            .ToArray();
        var minimumScore = job.Year.HasValue ? 120 : 100;
        if (ranked.Length == 0 || ranked[0].Score < minimumScore)
        {
            throw new MovieMatchException(
                $"无法可靠匹配《{job.Name}》({job.Year?.ToString(CultureInfo.InvariantCulture) ?? "年份未知"})；"
                + "IMDb 与标题搜索均未得到唯一结果，建议使用 MetaShark 刮削出 DoubanID。");
        }

        if (ranked.Length > 1 && ranked[0].Score == ranked[1].Score)
        {
            throw new MovieMatchException($"《{job.Name}》存在多个同分豆瓣条目，已停止自动匹配。");
        }

        return ranked[0].Candidate.Id;
    }

    /// <summary>
    /// Reads a direct Douban identifier from known provider-id formats.
    /// </summary>
    /// <param name="providerIds">Jellyfin provider identifiers.</param>
    /// <returns>Douban identifier or <see langword="null"/>.</returns>
    internal static string? GetDirectDoubanId(IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var providerKey in DirectProviderKeys)
        {
            var pair = providerIds.FirstOrDefault(
                item => string.Equals(item.Key, providerKey, StringComparison.OrdinalIgnoreCase));
            var id = ExtractNumericId(pair.Value);
            if (id is not null)
            {
                return id;
            }
        }

        var metaShark = providerIds.FirstOrDefault(
            item => string.Equals(item.Key, "MetaSharkID", StringComparison.OrdinalIgnoreCase));
        if (metaShark.Value?.StartsWith("Douban_", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ExtractNumericId(metaShark.Value["Douban_".Length..]);
        }

        return null;
    }

    /// <summary>
    /// Reads and normalizes an IMDb identifier from provider IDs.
    /// </summary>
    /// <param name="providerIds">Jellyfin provider identifiers.</param>
    /// <returns>IMDb identifier or <see langword="null"/>.</returns>
    internal static string? GetImdbId(IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var providerKey in ImdbProviderKeys)
        {
            var pair = providerIds.FirstOrDefault(
                item => string.Equals(item.Key, providerKey, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var match = ImdbIdRegex().Match(pair.Value);
            if (match.Success)
            {
                return match.Value.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string? ExtractNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.All(char.IsDigit))
        {
            return value;
        }

        var match = DoubanSubjectIdRegex().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int Score(SyncJob job, DoubanMovieCandidate candidate)
    {
        var expectedTitles = GetSearchTitles(job)
            .Select(NormalizeTitle)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var candidateTitles = new[] { NormalizeTitle(candidate.Title), NormalizeTitle(candidate.OriginalTitle) }
            .Where(value => value.Length > 0);

        var score = candidateTitles.Any(expectedTitles.Contains) ? 100 : 0;
        if (job.Year.HasValue
            && int.TryParse(candidate.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var candidateYear)
            && candidateYear == job.Year)
        {
            score += 20;
        }

        return score;
    }

    /// <summary>
    /// Builds exact search-title variants for a movie or completed season.
    /// </summary>
    /// <param name="job">Synchronization job.</param>
    /// <returns>Deduplicated search titles.</returns>
    internal static IReadOnlyList<string> GetSearchTitles(SyncJob job)
    {
        if (job.MediaKind != SyncMediaKind.Season || !job.SeasonNumber.HasValue)
        {
            return new[] { job.Name, job.OriginalTitle }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var seasonNumber = job.SeasonNumber.Value;
        var seriesTitles = new[] { job.SeriesName, job.SeriesOriginalTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(StripTrailingYear)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = new List<string>();
        foreach (var title in seriesTitles)
        {
            results.Add($"{title} 第{ToChineseNumber(seasonNumber)}季");
            results.Add($"{title} 第{seasonNumber.ToString(CultureInfo.InvariantCulture)}季");
            results.Add($"{title} Season {seasonNumber.ToString(CultureInfo.InvariantCulture)}");
            results.Add($"{title} S{seasonNumber.ToString("00", CultureInfo.InvariantCulture)}");
            if (job.AllowSeriesIdentityFallback && seasonNumber == 1)
            {
                results.Add(title);
            }
        }

        return results
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StripTrailingYear(string value)
    {
        return TrailingYearRegex().Replace(value.Trim(), string.Empty).Trim();
    }

    private static string ToChineseNumber(int value)
    {
        if (value <= 0 || value >= 100)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        if (value < 10)
        {
            return digits[value];
        }

        var tens = value / 10;
        var ones = value % 10;
        return (tens == 1 ? string.Empty : digits[tens])
            + "十"
            + (ones == 0 ? string.Empty : digits[ones]);
    }

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?:movie\.douban\.com/subject/|Douban[_:-])(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DoubanSubjectIdRegex();

    [GeneratedRegex(@"tt\d{7,10}", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"\s*[（(]\d{4}[）)]\s*$")]
    private static partial Regex TrailingYearRegex();
}

/// <summary>
/// Raised when a Jellyfin movie or season cannot be matched without a high risk of a false positive.
/// </summary>
public sealed class MovieMatchException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MovieMatchException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public MovieMatchException(string message)
        : base(message)
    {
    }
}
