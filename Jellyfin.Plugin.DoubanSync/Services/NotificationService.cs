using System.Net.Http.Json;
using Jellyfin.Plugin.DoubanSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Sends administrator notifications when a Douban Cookie becomes invalid.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends an invalid-Cookie notification through every configured channel.
    /// </summary>
    /// <param name="configuration">Current plugin configuration.</param>
    /// <param name="account">Affected Douban account.</param>
    /// <param name="failureMessage">Authentication failure detail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether at least one configured channel accepted the notification.</returns>
    Task<bool> NotifyCookieInvalidAsync(
        PluginConfiguration configuration,
        DoubanAccountConfiguration account,
        string failureMessage,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class NotificationService : INotificationService
{
    private const string NotificationTitle = "豆瓣同步 Cookie 已失效";
    private readonly HttpClient _httpClient;
    private readonly ICookieProtector _protector;
    private readonly ILogger<NotificationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationService"/> class.
    /// </summary>
    public NotificationService(
        HttpClient httpClient,
        ICookieProtector protector,
        ILogger<NotificationService> logger)
    {
        _httpClient = httpClient;
        _protector = protector;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> NotifyCookieInvalidAsync(
        PluginConfiguration configuration,
        DoubanAccountConfiguration account,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var message = $"豆瓣账号“{account.DoubanDisplayName}”的 Cookie 已失效，请在 Jellyfin 豆瓣同步设置中重新导入。原因：{failureMessage}";
        var delivered = false;

        if (!string.IsNullOrWhiteSpace(configuration.ProtectedBarkUrl))
        {
            delivered |= await TrySendBarkAsync(
                configuration.ProtectedBarkUrl,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(configuration.ProtectedDiscordWebhookUrl))
        {
            delivered |= await TrySendDiscordAsync(
                configuration.ProtectedDiscordWebhookUrl,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    internal static Uri CreateBarkNotificationUri(Uri pushUrl, string title, string message)
    {
        var builder = new UriBuilder(pushUrl);
        builder.Path = string.Concat(
            builder.Path.TrimEnd('/'),
            "/",
            Uri.EscapeDataString(title),
            "/",
            Uri.EscapeDataString(message));
        var group = "group=" + Uri.EscapeDataString("豆瓣同步");
        builder.Query = string.IsNullOrWhiteSpace(builder.Query)
            ? group
            : builder.Query.TrimStart('?') + "&" + group;
        return builder.Uri;
    }

    private async Task<bool> TrySendBarkAsync(
        string protectedUrl,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var pushUrl = new Uri(_protector.Unprotect(protectedUrl), UriKind.Absolute);
            var uri = CreateBarkNotificationUri(pushUrl, NotificationTitle, message);
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogDeliveryFailed(_logger, ex, "Bark");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDeliveryFailed(_logger, ex, "Bark");
            return false;
        }
    }

    private async Task<bool> TrySendDiscordAsync(
        string protectedUrl,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var webhookUrl = new Uri(_protector.Unprotect(protectedUrl), UriKind.Absolute);
            using var response = await _httpClient.PostAsJsonAsync(
                webhookUrl,
                new
                {
                    content = $"**{NotificationTitle}**\n{message}",
                    allowed_mentions = new { parse = Array.Empty<string>() }
                },
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogDeliveryFailed(_logger, ex, "Discord Webhook");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDeliveryFailed(_logger, ex, "Discord Webhook");
            return false;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Failed to deliver invalid Douban Cookie notification through {Channel}")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string channel);
}
