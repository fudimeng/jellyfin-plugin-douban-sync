using System.Globalization;
using Jellyfin.Plugin.DoubanSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DoubanSync;

/// <summary>
/// Jellyfin to Douban watched-state synchronization plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("58f521f4-ff96-4ac4-b6ac-14fdc730659a");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <inheritdoc />
    public override string Name => "豆瓣同步";

    /// <inheritdoc />
    public override string Description => "将 Jellyfin 中已观看的电影和完整看完的剧集季同步为豆瓣“看过”。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var rootNamespace = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = "douban-sync",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.html",
                    rootNamespace)
            },
            new PluginPageInfo
            {
                Name = "douban-sync-js",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.config.js",
                    rootNamespace)
            }
        ];
    }
}
