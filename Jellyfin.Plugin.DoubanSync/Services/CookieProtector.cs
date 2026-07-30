using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Protects browser cookies before they are stored in plugin configuration.
/// </summary>
public interface ICookieProtector
{
    /// <summary>
    /// Encrypts and authenticates a cookie string.
    /// </summary>
    /// <param name="cookie">Raw Cookie request header.</param>
    /// <returns>Protected configuration value.</returns>
    string Protect(string cookie);

    /// <summary>
    /// Decrypts and verifies a protected cookie.
    /// </summary>
    /// <param name="protectedCookie">Protected configuration value.</param>
    /// <returns>Raw Cookie request header.</returns>
    string Unprotect(string protectedCookie);
}

/// <inheritdoc />
public sealed class CookieProtector : ICookieProtector
{
    private readonly IDataProtector _protector;

    /// <summary>
    /// Initializes a new instance of the <see cref="CookieProtector"/> class.
    /// </summary>
    /// <param name="provider">ASP.NET Core data-protection provider.</param>
    public CookieProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("douban-browser-cookie", "v1");
    }

    /// <inheritdoc />
    public string Protect(string cookie)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        return _protector.Protect(cookie);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedCookie)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCookie);
        try
        {
            return _protector.Unprotect(protectedCookie);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "无法解密豆瓣 Cookie；数据保护密钥可能已丢失，请重新导入 Cookie。",
                ex);
        }
    }
}
