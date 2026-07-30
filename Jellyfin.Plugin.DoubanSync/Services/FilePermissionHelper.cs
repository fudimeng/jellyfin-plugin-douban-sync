namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Applies best-effort restrictive file-system permissions.
/// </summary>
internal static class FilePermissionHelper
{
    /// <summary>
    /// Restricts a directory to the Jellyfin service account on Unix-like systems.
    /// </summary>
    /// <param name="path">Existing directory path.</param>
    public static void RestrictDirectoryToCurrentUser(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
