using Jellyfin.Plugin.DoubanSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DoubanSync;

/// <summary>
/// Registers plugin services with Jellyfin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ICookieProtector>(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<IApplicationPaths>();
            var keyDirectory = Path.Combine(paths.PluginConfigurationsPath, "DoubanSyncKeys");
            Directory.CreateDirectory(keyDirectory);
            FilePermissionHelper.RestrictDirectoryToCurrentUser(keyDirectory);

            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory),
                builder => builder.SetApplicationName("Jellyfin.Plugin.DoubanSync"));
            return new CookieProtector(provider);
        });

        serviceCollection.AddHttpClient<IDoubanClient, DoubanClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                + "AppleWebKit/537.36 (KHTML, like Gecko) "
                + "Chrome/126.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://movie.douban.com/");
        });

        serviceCollection.AddHttpClient<INotificationService, NotificationService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        serviceCollection.AddSingleton<IDoubanRequestGate, DoubanRequestGate>();
        serviceCollection.AddSingleton<IMovieResolver, MovieResolver>();
        serviceCollection.AddSingleton<ISyncQueue, SyncQueue>();
        serviceCollection.AddHostedService<SyncWorker>();
    }
}
