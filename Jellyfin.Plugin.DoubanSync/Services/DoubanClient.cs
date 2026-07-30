using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Minimal client for the Douban web endpoints needed by this plugin.
/// </summary>
public interface IDoubanClient
{
    /// <summary>
    /// Validates a browser Cookie header against the signed-in user's page.
    /// </summary>
    /// <param name="cookie">Normalized Cookie header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Douban display name when available.</returns>
    Task<string> ValidateCookieAsync(string cookie, CancellationToken cancellationToken);

    /// <summary>
    /// Searches Douban movie suggestions.
    /// </summary>
    /// <param name="query">Movie title.</param>
    /// <param name="cookie">Normalized Cookie header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching suggestions.</returns>
    Task<IReadOnlyList<DoubanMovieCandidate>> SearchMoviesAsync(
        string query,
        string cookie,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an IMDb identifier through Douban's movie search and verifies the subject page.
    /// </summary>
    /// <param name="imdbId">Normalized IMDb identifier.</param>
    /// <param name="cookie">Normalized Cookie header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verified Douban subject identifier, or <see langword="null"/>.</returns>
    Task<string?> FindMovieByImdbAsync(
        string imdbId,
        string cookie,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks one Douban movie as watched.
    /// </summary>
    /// <param name="doubanId">Douban subject identifier.</param>
    /// <param name="cookie">Normalized Cookie header.</param>
    /// <param name="markPrivate">Whether the interest is visible only to the user.</param>
    /// <param name="shareToBroadcast">Whether the interest is shared to the user's broadcast.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the subject was newly marked or was already watched.</returns>
    Task<DoubanMarkResult> MarkWatchedAsync(
        string doubanId,
        string cookie,
        bool markPrivate,
        bool shareToBroadcast,
        CancellationToken cancellationToken);
}

/// <summary>
/// Serializes all Douban traffic and spaces requests apart.
/// </summary>
public interface IDoubanRequestGate
{
    /// <summary>
    /// Waits until another request may be sent.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DoubanRequestGate : IDoubanRequestGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    /// <inheritdoc />
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var interval = TimeSpan.FromSeconds(
                Math.Clamp(Plugin.Instance.Configuration.RequestIntervalSeconds, 3, 60));
            var remaining = interval - (DateTime.UtcNow - _lastRequestUtc);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}

/// <inheritdoc />
public sealed partial class DoubanClient : IDoubanClient
{
    private static readonly Uri MineUri = new("https://www.douban.com/mine/");
    private readonly HttpClient _httpClient;
    private readonly IDoubanRequestGate _requestGate;
    private readonly ILogger<DoubanClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DoubanClient"/> class.
    /// </summary>
    /// <param name="httpClient">Typed HTTP client.</param>
    /// <param name="requestGate">Shared request limiter.</param>
    /// <param name="logger">Logger.</param>
    public DoubanClient(
        HttpClient httpClient,
        IDoubanRequestGate requestGate,
        ILogger<DoubanClient> logger)
    {
        _httpClient = httpClient;
        _requestGate = requestGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ValidateCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Get, MineUri, cookie);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (!response.IsSuccessStatusCode || IsAuthenticationPage(finalUri))
        {
            throw new DoubanAuthenticationException("豆瓣 Cookie 已失效或触发了登录验证。");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new DoubanAuthenticationException("豆瓣返回了安全验证页，请先在同一出口网络的浏览器中完成验证。");
        }

        var match = ProfileNameRegex().Match(body);
        return match.Success
            ? WebUtility.HtmlDecode(StripTagsRegex().Replace(match.Groups[1].Value, string.Empty)).Trim()
            : "已登录";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DoubanMovieCandidate>> SearchMoviesAsync(
        string query,
        string cookie,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var uri = new Uri(
            "https://movie.douban.com/j/subject_suggest?q="
            + Uri.EscapeDataString(query.Trim()));
        using var request = CreateRequest(HttpMethod.Get, uri, cookie);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureUsableResponse(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await JsonSerializer.DeserializeAsync<List<DoubanMovieCandidate>>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return candidates?.Where(candidate => string.Equals(candidate.Type, "movie", StringComparison.Ordinal)).ToArray()
            ?? [];
    }

    /// <inheritdoc />
    public async Task<string?> FindMovieByImdbAsync(
        string imdbId,
        string cookie,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var searchUri = new Uri(
            "https://search.douban.com/movie/subject_search?search_text="
            + Uri.EscapeDataString(imdbId)
            + "&cat=1002");
        using var searchRequest = CreateRequest(HttpMethod.Get, searchUri, cookie);
        searchRequest.Headers.Referrer = new Uri("https://movie.douban.com/");
        using var searchResponse = await _httpClient.SendAsync(
            searchRequest,
            cancellationToken).ConfigureAwait(false);
        EnsureUsableResponse(searchResponse);
        var searchBody = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string? candidateId;
        try
        {
            candidateId = ExtractImdbSearchCandidateId(searchBody, imdbId);
        }
        catch (JsonException ex)
        {
            throw new DoubanRequestException("豆瓣 IMDb 搜索返回了无法识别的数据。", ex);
        }

        if (candidateId is null)
        {
            return null;
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var subjectUri = new Uri($"https://movie.douban.com/subject/{candidateId}/");
        using var subjectRequest = CreateRequest(HttpMethod.Get, subjectUri, cookie);
        subjectRequest.Headers.Referrer = searchUri;
        using var subjectResponse = await _httpClient.SendAsync(
            subjectRequest,
            cancellationToken).ConfigureAwait(false);
        EnsureUsableResponse(subjectResponse);
        var subjectBody = await subjectResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return SubjectMatchesImdbId(subjectBody, imdbId) ? candidateId : null;
    }

    /// <inheritdoc />
    public async Task<DoubanMarkResult> MarkWatchedAsync(
        string doubanId,
        string cookie,
        bool markPrivate,
        bool shareToBroadcast,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(doubanId) || !doubanId.All(char.IsDigit))
        {
            throw new ArgumentException("豆瓣条目 ID 必须为数字。", nameof(doubanId));
        }

        var subjectUri = new Uri($"https://movie.douban.com/subject/{doubanId}/");
        var endpoint = new Uri($"https://movie.douban.com/j/subject/{doubanId}/interest");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using (var statusRequest = CreateRequest(HttpMethod.Get, endpoint, cookie))
        using (var statusResponse = await _httpClient.SendAsync(statusRequest, cancellationToken).ConfigureAwait(false))
        {
            EnsureUsableResponse(statusResponse);
            var statusBody = await statusResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsAlreadyWatchedResponse(statusBody))
                {
                    return DoubanMarkResult.AlreadyWatched;
                }
            }
            catch (JsonException ex)
            {
                throw new DoubanRequestException("豆瓣返回了无法识别的收藏状态，未执行更新。", ex);
            }
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Post, endpoint, cookie);
        request.Headers.Referrer = subjectUri;
        request.Headers.Add("Origin", "https://movie.douban.com");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        request.Content = new FormUrlEncodedContent(
            CreateInterestFormValues(cookie, markPrivate, shareToBroadcast));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureUsableResponse(response);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var result = root.TryGetProperty("r", out var resultElement)
                ? resultElement.GetInt32()
                : -1;
            if (result == 0)
            {
                return DoubanMarkResult.Marked;
            }

            var code = root.TryGetProperty("code", out var codeElement)
                ? codeElement.ToString()
                : "unknown";
            if (string.Equals(code, "403", StringComparison.Ordinal))
            {
                throw new DoubanAuthenticationException("豆瓣拒绝了 Cookie 或 ck，请重新导入 Cookie。");
            }

            throw new DoubanRequestException($"豆瓣未接受“看过”操作，返回代码：{code}。");
        }
        catch (JsonException ex)
        {
            LogNonJsonResponse(_logger, doubanId);
            throw new DoubanRequestException("豆瓣返回了无法识别的响应，可能触发了安全验证。", ex);
        }
    }

    internal static bool IsAlreadyWatchedResponse(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("interest_status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "collect", StringComparison.Ordinal);
    }

    internal static string? ExtractImdbSearchCandidateId(string responseBody, string imdbId)
    {
        var match = SubjectSearchDataRegex().Match(responseBody);
        if (!match.Success)
        {
            return null;
        }

        var data = JsonSerializer.Deserialize<DoubanSubjectSearchData>(match.Groups[1].Value);
        if (data is null
            || !string.Equals(data.Text, imdbId, StringComparison.OrdinalIgnoreCase)
            || data.Total != 1
            || data.Items.Count != 1
            || data.Items[0].Id <= 0)
        {
            return null;
        }

        return data.Items[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static bool SubjectMatchesImdbId(string responseBody, string imdbId)
    {
        var match = SubjectImdbIdRegex().Match(responseBody);
        return match.Success
            && string.Equals(match.Groups[1].Value, imdbId, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> CreateInterestFormValues(
        string cookie,
        bool markPrivate,
        bool shareToBroadcast)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("interest", "collect"),
            new("ck", CookieHeader.GetCsrfToken(cookie)),
            new("foldcollect", "U")
        };
        if (markPrivate)
        {
            values.Add(new KeyValuePair<string, string>("private", "on"));
        }

        if (shareToBroadcast)
        {
            values.Add(new KeyValuePair<string, string>("share-shuo", "douban"));
        }

        return values;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string cookie)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!request.Headers.TryAddWithoutValidation("Cookie", cookie))
        {
            request.Dispose();
            throw new ArgumentException("Cookie 格式无效。", nameof(cookie));
        }

        return request;
    }

    private static bool IsAuthenticationPage(Uri? uri)
    {
        return uri is null
            || string.Equals(uri.Host, "accounts.douban.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "sec.douban.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureUsableResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || IsAuthenticationPage(response.RequestMessage?.RequestUri))
        {
            throw new DoubanAuthenticationException("豆瓣 Cookie 已失效或请求被安全验证拦截。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DoubanRequestException(
                $"豆瓣请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");
        }
    }

    [GeneratedRegex(
        """<div[^>]*class=["'][^"']*db-usr-profile[^"']*["'][^>]*>.*?<h1[^>]*>(.*?)</h1>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex("""window\.__DATA__\s*=\s*(\{.*?\});""", RegexOptions.Singleline)]
    private static partial Regex SubjectSearchDataRegex();

    [GeneratedRegex("""IMDb:\s*</span>\s*(tt\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectImdbIdRegex();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Douban returned a non-JSON response while marking subject {DoubanId}")]
    private static partial void LogNonJsonResponse(ILogger logger, string doubanId);
}

internal sealed class DoubanSubjectSearchData
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<DoubanSubjectSearchItem> Items { get; set; } = [];
}

internal sealed class DoubanSubjectSearchItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
}

/// <summary>
/// One result from Douban's subject suggestion endpoint.
/// </summary>
public sealed class DoubanMovieCandidate
{
    /// <summary>
    /// Gets or sets the subject identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the localized title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original title.
    /// </summary>
    [JsonPropertyName("sub_title")]
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    [JsonPropertyName("year")]
    public string Year { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the result type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Result of checking and applying one Douban watched mark.
/// </summary>
public enum DoubanMarkResult
{
    /// <summary>
    /// The subject was newly marked as watched.
    /// </summary>
    Marked,

    /// <summary>
    /// The subject was already marked as watched and was not updated.
    /// </summary>
    AlreadyWatched
}

/// <summary>
/// Failure caused by an expired or rejected Douban login session.
/// </summary>
public sealed class DoubanAuthenticationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DoubanAuthenticationException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public DoubanAuthenticationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Failure returned by a Douban web endpoint.
/// </summary>
public sealed class DoubanRequestException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DoubanRequestException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public DoubanRequestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DoubanRequestException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Underlying error.</param>
    public DoubanRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
