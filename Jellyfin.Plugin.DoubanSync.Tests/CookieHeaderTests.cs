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

    [Fact]
    public void MergeResponseCookies_UpdatesExistingTokensAndIgnoresUnrelatedCookies()
    {
        var merged = CookieHeader.MergeResponseCookies(
            "bid=old-bid; dbcl2=old-session; ck=old-token",
            [
                "ck=new-token; Path=/; Secure",
                "dbcl2=new-session; Domain=.douban.com; HttpOnly",
                "unrelated=value; Path=/"
            ]);

        Assert.Equal("bid=old-bid; dbcl2=new-session; ck=new-token", merged);
    }

    [Fact]
    public void MergeResponseCookies_RejectsRemovalOfRequiredToken()
    {
        Assert.Throws<ArgumentException>(
            () => CookieHeader.MergeResponseCookies(
                "bid=test; dbcl2=session; ck=token",
                ["ck=; Max-Age=0; Path=/"]));
    }
}
