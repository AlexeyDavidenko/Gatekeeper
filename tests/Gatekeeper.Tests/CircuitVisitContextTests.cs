using System.Security.Claims;
using Gatekeeper.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the per-circuit snapshot of identity/IP/UA/session-id (see SiteVisitLogging.cs) —
/// captured once from the HTTP request that opened a Blazor circuit, since none of this is
/// reliably available for the rest of the circuit's lifetime.
/// </summary>
public class CircuitVisitContextTests
{
    [Fact]
    public void Captures_User_Ip_And_SessionId_From_HttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["User-Agent"] = "TestBrowser/1.0";
        ctx.Request.Headers["CF-Connecting-IP"] = "9.9.9.9";
        ctx.Items["gk_sid"] = "abc123";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Name, "Alex")], "TestAuth"));

        var subject = new CircuitVisitContext(new HttpContextAccessor { HttpContext = ctx });

        Assert.Equal(42, subject.UserId);
        Assert.Equal("Alex", subject.UserName);
        Assert.Equal("abc123", subject.SessionId);
        Assert.Equal("9.9.9.9", subject.IpAddress);
        Assert.Equal("TestBrowser/1.0", subject.UserAgent);
    }

    [Fact]
    public void Falls_Back_To_Cookie_When_Items_Missing_Session()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = "gk_sid=from-cookie";

        var subject = new CircuitVisitContext(new HttpContextAccessor { HttpContext = ctx });

        Assert.Equal("from-cookie", subject.SessionId);
    }

    // Covers the /login (anonymous) path through MainLayout: ctx.User is a valid, unauthenticated
    // ClaimsPrincipal, not null — this must never throw, matching SiteVisitMiddleware's existing
    // handling of anonymous requests.
    [Fact]
    public void Handles_Anonymous_And_Missing_HttpContext_Without_Throwing()
    {
        var subject = new CircuitVisitContext(new HttpContextAccessor { HttpContext = null });

        Assert.Null(subject.UserId);
        Assert.Null(subject.UserName);
        Assert.Null(subject.IpAddress);
        Assert.Null(subject.UserAgent);
        Assert.False(string.IsNullOrEmpty(subject.SessionId));
    }
}
