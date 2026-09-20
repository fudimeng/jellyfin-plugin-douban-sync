using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.DoubanSync.Configuration;
using Jellyfin.Plugin.DoubanSync.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void BarkUri_AppendsEncodedTitleMessageAndGroup()
    {
        var result = NotificationService.CreateBarkNotificationUri(
            new Uri("https://api.day.app/device-key?icon=https%3A%2F%2Fexample.com%2Ficon.png"),
            "Cookie 已失效",
            "请重新导入 Cookie");

        Assert.StartsWith(
            "https://api.day.app/device-key/Cookie%20%E5%B7%B2%E5%A4%B1%E6%95%88/",
            result.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Contains("icon=https%3A%2F%2Fexample.com%2Ficon.png", result.Query, StringComparison.Ordinal);
        Assert.Contains("group=%E8%B1%86%E7%93%A3%E5%90%8C%E6%AD%A5", result.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyCookieInvalid_SendsBarkAndDiscordRequests()
    {
        var handler = new RecordingHandler();
        var service = new NotificationService(
            new HttpClient(handler),
            new PassThroughProtector(),
            NullLogger<NotificationService>.Instance);
        var configuration = new PluginConfiguration
        {
            ProtectedBarkUrl = "https://api.day.app/device-key",
            ProtectedDiscordWebhookUrl = "https://discord.com/api/webhooks/id/token"
        };
        var account = new DoubanAccountConfiguration { DoubanDisplayName = "测试账号" };

        var delivered = await service.NotifyCookieInvalidAsync(
            configuration,
            account,
            "登录已过期。",
            CancellationToken.None);

        Assert.True(delivered);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("api.day.app", request.Uri.Host);
                Assert.Contains("device-key", request.Uri.AbsolutePath, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("discord.com", request.Uri.Host);
                using var document = JsonDocument.Parse(request.Body);
                var content = document.RootElement.GetProperty("content").GetString();
                Assert.NotNull(content);
                Assert.Contains("测试账号", content, StringComparison.Ordinal);
                Assert.Contains("登录已过期", content, StringComparison.Ordinal);
            });
    }

    private sealed class PassThroughProtector : ICookieProtector
    {
        public string Protect(string cookie) => cookie;

        public string Unprotect(string protectedCookie) => protectedCookie;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."),
                body));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);
}
