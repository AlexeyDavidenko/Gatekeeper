using Gatekeeper.Web;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the scripted-client filter that keeps health-check/CLI traffic out of the site
/// visitor analytics (see SiteVisitMiddleware).
/// </summary>
public class NonBrowserUserAgentDetectorTests
{
    [Theory]
    [InlineData("curl/8.5.0")]
    [InlineData("Wget/1.21.3")]
    [InlineData("python-requests/2.31.0")]
    [InlineData("python-httpx/0.27.0")]
    [InlineData("Go-http-client/1.1")]
    [InlineData("okhttp/4.12.0")]
    [InlineData("PostmanRuntime/7.36.0")]
    [InlineData("Java/17.0.2")]
    [InlineData("Apache-HttpClient/4.5.13")]
    [InlineData("axios/1.6.0")]
    [InlineData("node-fetch/3.3.2")]
    public void IsBot_Returns_True_For_Known_Scripted_Clients(string userAgent) =>
        Assert.True(NonBrowserUserAgentDetector.IsBot(userAgent));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsBot_Returns_True_For_Missing_UserAgent(string? userAgent) =>
        Assert.True(NonBrowserUserAgentDetector.IsBot(userAgent));

    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.5 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1")]
    public void IsBot_Returns_False_For_Real_Browsers(string userAgent) =>
        Assert.False(NonBrowserUserAgentDetector.IsBot(userAgent));

    [Fact]
    public void IsBot_Match_Is_Case_Insensitive()
    {
        Assert.True(NonBrowserUserAgentDetector.IsBot("CURL/8.5.0"));
        Assert.True(NonBrowserUserAgentDetector.IsBot("WGET/1.21.3"));
    }
}
