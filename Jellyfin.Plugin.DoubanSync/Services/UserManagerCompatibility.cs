using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Bridges the user enumeration API renamed between Jellyfin 10.11 patch releases.
/// </summary>
internal static class UserManagerCompatibility
{
    /// <summary>
    /// Gets all Jellyfin users through either the legacy Users property or GetUsers method.
    /// </summary>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <returns>Current users.</returns>
    public static IReadOnlyList<User> GetUsers(IUserManager userManager)
    {
        var interfaceType = typeof(IUserManager);
        var getUsersMethod = interfaceType.GetMethod(
            "GetUsers",
            BindingFlags.Instance | BindingFlags.Public,
            Type.EmptyTypes);
        if (getUsersMethod?.Invoke(userManager, null) is IEnumerable<User> methodUsers)
        {
            return methodUsers.ToArray();
        }

        var usersProperty = interfaceType.GetProperty(
            "Users",
            BindingFlags.Instance | BindingFlags.Public);
        if (usersProperty?.GetValue(userManager) is IEnumerable<User> propertyUsers)
        {
            return propertyUsers.ToArray();
        }

        throw new NotSupportedException("当前 Jellyfin 版本不提供可识别的用户枚举接口。");
    }
}
