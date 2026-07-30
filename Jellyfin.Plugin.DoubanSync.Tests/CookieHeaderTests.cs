using Jellyfin.Plugin.DoubanSync.Services;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class CookieHeaderTests
{
    [Fact]
    public void Normalize_PreservesValuesContainingEquals()
    {
        var normalized = CookieHeader.Normalize(" bid=abc== ; dbcl2=\"user:token\"; ck=Ab_1 ");

        Assert.Equal("bid=abc==; dbcl2=\"user:token\"; ck=Ab_1", normalized);
        Assert.Equal("Ab_1", CookieHeader.GetCsrfToken(normalized));
    }

    [Theory]
    [InlineData("dbcl2=x")]
    [InlineData("ck=x")]
    [InlineData("dbcl2=x; ck=x\r\nX-Test: injected")]
    public void Normalize_RejectsIncompleteOrUnsafeCookie(string cookie)
    {
        Assert.Throws<ArgumentException>(() => CookieHeader.Normalize(cookie));
    }
}
