using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DoubanSync.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether periodic synchronization is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Douban interest is private.
    /// </summary>
    public bool MarkPrivate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether watched marks are shared to the Douban broadcast.
    /// </summary>
    public bool ShareToBroadcast { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum delay between requests to Douban.
    /// </summary>
    public int RequestIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets encrypted Douban credentials mapped to Jellyfin users.
    /// </summary>
    public DoubanAccountConfiguration[] Accounts { get; set; } = [];
}

/// <summary>
/// Encrypted Douban account configuration for one Jellyfin user.
/// </summary>
public sealed class DoubanAccountConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyfin user identifier.
    /// </summary>
    public Guid JellyfinUserId { get; set; }

    /// <summary>
    /// Gets or sets the encrypted browser Cookie header.
    /// </summary>
    public string ProtectedCookie { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name read from Douban.
    /// </summary>
    public string DoubanDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the cookie was last replaced.
    /// </summary>
    public DateTime CookieUpdatedAtUtc { get; set; }
}
