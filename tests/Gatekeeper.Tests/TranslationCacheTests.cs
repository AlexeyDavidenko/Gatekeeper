using Gatekeeper.Contracts;
using Gatekeeper.Web;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for Gatekeeper.Web's TranslationCache — a plain in-memory lookup with no I/O, so a direct
/// unit test is simplest. Gatekeeper.Bot ships a byte-for-byte identical copy (Bot has zero unit
/// test coverage by established convention in this repo — manual dry-run only), so these cases
/// cover both.
/// </summary>
public class TranslationCacheTests
{
    [Fact]
    public void Get_Returns_Exact_Match_When_Present()
    {
        var cache = new TranslationCache();
        cache.Load([new TranslationDto(1, "web.dashboard.title", "ru", "Дашборд"), new TranslationDto(2, "web.dashboard.title", "en", "Dashboard")]);

        Assert.Equal("Dashboard", cache.Get("web.dashboard.title", "en"));
        Assert.Equal("Дашборд", cache.Get("web.dashboard.title", "ru"));
    }

    [Fact]
    public void Get_Falls_Back_To_Russian_When_Requested_Language_Missing()
    {
        var cache = new TranslationCache();
        cache.Load([new TranslationDto(1, "web.dashboard.title", "ru", "Дашборд")]);

        Assert.Equal("Дашборд", cache.Get("web.dashboard.title", "en"));
    }

    [Fact]
    public void Get_Falls_Back_To_Placeholder_When_Key_Does_Not_Exist()
    {
        var cache = new TranslationCache();
        cache.Load([]);

        Assert.Equal("[web.nonexistent.key]", cache.Get("web.nonexistent.key", "en"));
    }

    [Fact]
    public void Load_Replaces_Stale_Entries()
    {
        var cache = new TranslationCache();
        cache.Load([new TranslationDto(1, "web.dashboard.title", "en", "Old Title")]);
        Assert.Equal("Old Title", cache.Get("web.dashboard.title", "en"));

        cache.Load([new TranslationDto(1, "web.dashboard.title", "en", "New Title")]);
        Assert.Equal("New Title", cache.Get("web.dashboard.title", "en"));
    }
}
