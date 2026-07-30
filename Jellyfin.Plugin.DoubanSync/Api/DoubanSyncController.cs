using System.Net.Mime;
using Jellyfin.Plugin.DoubanSync.Configuration;
using Jellyfin.Plugin.DoubanSync.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DoubanSync.Api;

/// <summary>
/// Administrative API for Douban Sync.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("DoubanSync")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class DoubanSyncController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly IDoubanClient _doubanClient;
    private readonly ICookieProtector _cookieProtector;
    private readonly ISyncQueue _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DoubanSyncController"/> class.
    /// </summary>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <param name="doubanClient">Douban client.</param>
    /// <param name="cookieProtector">Cookie protector.</param>
    /// <param name="queue">Persistent synchronization queue.</param>
    public DoubanSyncController(
        IUserManager userManager,
        IDoubanClient doubanClient,
        ICookieProtector cookieProtector,
        ISyncQueue queue)
    {
        _userManager = userManager;
        _doubanClient = doubanClient;
        _cookieProtector = cookieProtector;
        _queue = queue;
    }

    /// <summary>
    /// Gets safe configuration and queue state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Plugin state without cookie material.</returns>
    [HttpGet("State")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DoubanSyncState>> GetState(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var queue = await _queue.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var accounts = configuration.Accounts.ToDictionary(
            account => account.JellyfinUserId,
            account => account);
        return new DoubanSyncState
        {
            Enabled = configuration.Enabled,
            MarkPrivate = configuration.MarkPrivate,
            ShareToBroadcast = configuration.ShareToBroadcast,
            RequestIntervalSeconds = configuration.RequestIntervalSeconds,
            PendingCount = queue.PendingCount,
            Users = UserManagerCompatibility.GetUsers(_userManager)
                .Select(
                    user =>
                    {
                        accounts.TryGetValue(user.Id, out var account);
                        return new DoubanUserState
                        {
                            Id = user.Id,
                            Name = user.Username,
                            CookieConfigured = account is not null,
                            DoubanDisplayName = account?.DoubanDisplayName ?? string.Empty,
                            CookieUpdatedAtUtc = account?.CookieUpdatedAtUtc
                        };
                    })
                .OrderBy(user => user.Name, StringComparer.CurrentCulture)
                .ToArray(),
            Recent = queue.Recent
        };
    }

    /// <summary>
    /// Validates and stores a Douban browser Cookie for one Jellyfin user.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <param name="request">Raw browser Cookie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated account state.</returns>
    [HttpPost("Users/{userId:guid}/Credentials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DoubanUserState>> SaveCredentials(
        [FromRoute] Guid userId,
        [FromBody] SaveCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        string normalizedCookie;
        string displayName;
        try
        {
            normalizedCookie = CookieHeader.Normalize(request.Cookie);
            displayName = await _doubanClient.ValidateCookieAsync(
                normalizedCookie,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiError(ex.Message));
        }
        catch (DoubanAuthenticationException ex)
        {
            return BadRequest(new ApiError(ex.Message));
        }
        catch (DoubanRequestException ex)
        {
            return BadRequest(new ApiError(ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return BadRequest(new ApiError($"连接豆瓣失败：{ex.Message}"));
        }
        catch (TaskCanceledException)
        {
            return BadRequest(new ApiError("连接豆瓣超时。"));
        }

        var configuration = Plugin.Instance.Configuration;
        var accounts = configuration.Accounts.ToList();
        accounts.RemoveAll(account => account.JellyfinUserId == userId);
        var updatedAt = DateTime.UtcNow;
        accounts.Add(
            new DoubanAccountConfiguration
            {
                JellyfinUserId = userId,
                ProtectedCookie = _cookieProtector.Protect(normalizedCookie),
                DoubanDisplayName = displayName,
                CookieUpdatedAtUtc = updatedAt
            });
        configuration.Accounts = accounts.ToArray();
        Plugin.Instance.SaveConfiguration();
        await _queue.RequeueFailedAsync(userId, cancellationToken).ConfigureAwait(false);

        return new DoubanUserState
        {
            Id = userId,
            Name = user.Username,
            CookieConfigured = true,
            DoubanDisplayName = displayName,
            CookieUpdatedAtUtc = updatedAt
        };
    }

    /// <summary>
    /// Removes one user's stored Douban Cookie.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Users/{userId:guid}/Credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteCredentials([FromRoute] Guid userId)
    {
        var configuration = Plugin.Instance.Configuration;
        configuration.Accounts = configuration.Accounts
            .Where(account => account.JellyfinUserId != userId)
            .ToArray();
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Updates non-secret synchronization settings.
    /// </summary>
    /// <param name="request">New settings.</param>
    /// <returns>No content.</returns>
    [HttpPost("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SaveSettings([FromBody] SaveSettingsRequest request)
    {
        if (request.RequestIntervalSeconds is < 3 or > 60)
        {
            return BadRequest(new ApiError("请求间隔必须在 3 到 60 秒之间。"));
        }

        var configuration = Plugin.Instance.Configuration;
        configuration.Enabled = request.Enabled;
        configuration.MarkPrivate = request.MarkPrivate;
        configuration.ShareToBroadcast = request.ShareToBroadcast;
        configuration.RequestIntervalSeconds = request.RequestIntervalSeconds;
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Requeues failed records for one Jellyfin user.
    /// </summary>
    /// <param name="userId">Jellyfin user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Users/{userId:guid}/Retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RetryFailures(
        [FromRoute] Guid userId,
        CancellationToken cancellationToken)
    {
        await _queue.RequeueFailedAsync(userId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>
/// Raw credential-import request.
/// </summary>
public sealed class SaveCredentialsRequest
{
    /// <summary>
    /// Gets or sets the Cookie request header copied from a browser.
    /// </summary>
    public string Cookie { get; set; } = string.Empty;
}

/// <summary>
/// Non-secret plugin settings request.
/// </summary>
public sealed class SaveSettingsRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether event synchronization is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether marks are private.
    /// </summary>
    public bool MarkPrivate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether marks are shared to the Douban broadcast.
    /// </summary>
    public bool ShareToBroadcast { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay between Douban requests.
    /// </summary>
    public int RequestIntervalSeconds { get; set; }
}

/// <summary>
/// Safe plugin state.
/// </summary>
public sealed class DoubanSyncState
{
    /// <summary>
    /// Gets or sets a value indicating whether event synchronization is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether marks are private.
    /// </summary>
    public bool MarkPrivate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether marks are shared to the Douban broadcast.
    /// </summary>
    public bool ShareToBroadcast { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay between Douban requests.
    /// </summary>
    public int RequestIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets configured Jellyfin users.
    /// </summary>
    public IReadOnlyList<DoubanUserState> Users { get; set; } = [];

    /// <summary>
    /// Gets or sets the pending queue size.
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>
    /// Gets or sets recent synchronization records.
    /// </summary>
    public IReadOnlyList<SyncRecord> Recent { get; set; } = [];
}

/// <summary>
/// Safe credential state for one Jellyfin user.
/// </summary>
public sealed class DoubanUserState
{
    /// <summary>
    /// Gets or sets the Jellyfin user identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a protected Cookie is present.
    /// </summary>
    public bool CookieConfigured { get; set; }

    /// <summary>
    /// Gets or sets the validated Douban display name.
    /// </summary>
    public string DoubanDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the Cookie was replaced.
    /// </summary>
    public DateTime? CookieUpdatedAtUtc { get; set; }
}

/// <summary>
/// Simple API error body.
/// </summary>
/// <param name="Message">Reader-facing error message.</param>
public sealed record ApiError(string Message);
