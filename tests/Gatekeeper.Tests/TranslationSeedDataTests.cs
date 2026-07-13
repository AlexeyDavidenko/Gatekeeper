using Gatekeeper.Infrastructure;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Guards the localization seed data itself — a key present for one language but not the other
/// would silently render as a "[key]" placeholder in prod instead of failing loudly here.
/// </summary>
public class TranslationSeedDataTests
{
    [Fact]
    public void Every_Key_Has_Both_Russian_And_English()
    {
        var byKey = TranslationSeedData.Entries
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.Select(e => e.LanguageCode).ToHashSet());

        var incomplete = byKey.Where(kv => !kv.Value.SetEquals(["ru", "en"])).Select(kv => kv.Key).ToList();

        Assert.True(incomplete.Count == 0, $"Keys missing a ru or en counterpart: {string.Join(", ", incomplete)}");
    }

    [Fact]
    public void No_Duplicate_Key_Language_Pairs()
    {
        var duplicates = TranslationSeedData.Entries
            .GroupBy(e => (e.Key, e.LanguageCode))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Key}/{g.Key.LanguageCode}")
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate (key, language) pairs: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void No_Blank_Values()
    {
        var blanks = TranslationSeedData.Entries
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => $"{e.Key}/{e.LanguageCode}")
            .ToList();

        Assert.True(blanks.Count == 0, $"Blank seed values: {string.Join(", ", blanks)}");
    }
}
