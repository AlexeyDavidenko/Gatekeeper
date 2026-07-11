using System.Net;
using Gatekeeper.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the shared client-IP resolution used by both the HTTP-tracked and Blazor-circuit
/// halves of site visit logging (see SiteVisitMiddleware / CircuitVisitContext).
/// </summary>
public class SiteVisitClientIpTests
{
    [Fact]
    public void Resolve_Prefers_CfConnectingIp()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["CF-Connecting-IP"] = "1.1.1.1";
        ctx.Request.Headers["X-Forwarded-For"] = "2.2.2.2";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("3.3.3.3");

        Assert.Equal("1.1.1.1", SiteVisitClientIp.Resolve(ctx));
    }

    [Fact]
    public void Resolve_Falls_Back_To_First_XForwardedFor_Entry()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Forwarded-For"] = "2.2.2.2, 9.9.9.9";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("3.3.3.3");

        Assert.Equal("2.2.2.2", SiteVisitClientIp.Resolve(ctx));
    }

    [Fact]
    public void Resolve_Falls_Back_To_RemoteIpAddress()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("3.3.3.3");

        Assert.Equal("3.3.3.3", SiteVisitClientIp.Resolve(ctx));
    }

    [Fact]
    public void Resolve_Returns_Null_When_Nothing_Available() =>
        Assert.Null(SiteVisitClientIp.Resolve(new DefaultHttpContext()));
}
