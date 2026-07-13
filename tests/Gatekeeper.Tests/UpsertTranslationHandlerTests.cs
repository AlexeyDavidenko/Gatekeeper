using Gatekeeper.Application;
using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>Tests for UpsertTranslationHandler's find-or-create logic — the same handler serves
/// both a brand-new key (from the seed step's gaps) and an edit to an existing value (from the
/// /translations page).</summary>
public class UpsertTranslationHandlerTests
{
    [Fact]
    public async Task HandleAsync_Creates_New_Row_When_Key_Does_Not_Exist()
    {
        var translations = new FakeTranslationRepository();
        var handler = new UpsertTranslationHandler(translations);

        await handler.HandleAsync(new UpsertTranslationCommand("web.dashboard.title", "en", "Dashboard"));

        var row = Assert.Single(translations.Store);
        Assert.Equal("web.dashboard.title", row.Key);
        Assert.Equal("en", row.LanguageCode);
        Assert.Equal("Dashboard", row.Value);
    }

    [Fact]
    public async Task HandleAsync_Updates_In_Place_When_Key_Already_Exists()
    {
        var translations = new FakeTranslationRepository();
        translations.Store.Add(Translation.Create("web.dashboard.title", "en", "Old Title"));

        var handler = new UpsertTranslationHandler(translations);
        await handler.HandleAsync(new UpsertTranslationCommand("web.dashboard.title", "en", "New Title"));

        var row = Assert.Single(translations.Store);
        Assert.Equal("New Title", row.Value);
    }

    [Fact]
    public async Task HandleAsync_Treats_Same_Key_Different_Language_As_Separate_Row()
    {
        var translations = new FakeTranslationRepository();
        translations.Store.Add(Translation.Create("web.dashboard.title", "ru", "Дашборд"));

        var handler = new UpsertTranslationHandler(translations);
        await handler.HandleAsync(new UpsertTranslationCommand("web.dashboard.title", "en", "Dashboard"));

        Assert.Equal(2, translations.Store.Count);
    }
}
