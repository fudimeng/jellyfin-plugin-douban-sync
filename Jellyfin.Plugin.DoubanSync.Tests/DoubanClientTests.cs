using Jellyfin.Plugin.DoubanSync.Services;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class DoubanClientTests
{
    [Fact]
    public void InterestForm_AddsBroadcastFieldWhenEnabled()
    {
        var values = DoubanClient.CreateInterestFormValues(
            "bid=test; dbcl2=account; ck=abcd",
            markPrivate: false,
            shareToBroadcast: true);

        Assert.Contains(
            values,
            value => value.Key == "share-shuo" && value.Value == "douban");
        Assert.DoesNotContain(values, value => value.Key == "private");
    }

    [Fact]
    public void InterestForm_OmitsBroadcastFieldWhenDisabled()
    {
        var values = DoubanClient.CreateInterestFormValues(
            "bid=test; dbcl2=account; ck=abcd",
            markPrivate: true,
            shareToBroadcast: false);

        Assert.DoesNotContain(values, value => value.Key == "share-shuo");
        Assert.Contains(
            values,
            value => value.Key == "private" && value.Value == "on");
    }

    [Theory]
    [InlineData("""{"interest_status":"collect"}""", true)]
    [InlineData("""{"interest_status":"wish"}""", false)]
    [InlineData("""{"interest_status":null}""", false)]
    [InlineData("""{"html":"form"}""", false)]
    public void InterestStatus_DetectsAlreadyWatched(string responseBody, bool expected)
    {
        Assert.Equal(expected, DoubanClient.IsAlreadyWatchedResponse(responseBody));
    }

    [Fact]
    public void ImdbSearch_ExtractsSingleExactResult()
    {
        const string Response = """
            <script>
            window.__DATA__ = {"items":[{"id":36576765}],"text":"tt38067963","total":1};
            </script>
            """;

        Assert.Equal(
            "36576765",
            DoubanClient.ExtractImdbSearchCandidateId(Response, "tt38067963"));
    }

    [Fact]
    public void SubjectPage_VerifiesExactImdbId()
    {
        const string Response = """<span class="pl">IMDb:</span> tt38067963<br>""";

        Assert.True(DoubanClient.SubjectMatchesImdbId(Response, "tt38067963"));
        Assert.False(DoubanClient.SubjectMatchesImdbId(Response, "tt00000001"));
    }
}
