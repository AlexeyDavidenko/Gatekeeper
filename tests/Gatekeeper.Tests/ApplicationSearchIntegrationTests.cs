using Gatekeeper.Domain;
using Gatekeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainApplication = Gatekeeper.Domain.Application;

namespace Gatekeeper.Tests;

/// <summary>
/// Integration tests for the /applications/search endpoint's combined ILIKE + trigram
/// word_similarity query. Query is intentionally duplicated from Endpoints.cs rather than extracted
/// into a shared method, per project convention for read queries (see docs/conventions.md
/// "Как добавить чтение (query)").
/// </summary>
[Collection("Postgres")]
public sealed class ApplicationSearchIntegrationTests(PostgresFixture fixture)
{
    private const double FuzzyNameSimilarityThreshold = 0.4;

    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Func<string?, string?, string> SimpleNormalize =
        (first, last) => $"{first} {last}".Trim().ToLowerInvariant();

    [Fact]
    public async Task Search_Is_Case_Insensitive_And_Substring_Based()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        // Seed three users with different usernames
        var user1 = TelegramUser.FirstSighting(
            1001, isBot: false, isPremium: false,
            username: "JohnDoe", firstName: "John", lastName: "Doe",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);
        var user2 = TelegramUser.FirstSighting(
            1002, isBot: false, isPremium: false,
            username: "janedoe", firstName: "Jane", lastName: "Doe",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);
        var user3 = TelegramUser.FirstSighting(
            1003, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: "Smith",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user1);
        db.Users.Add(user2);
        db.Users.Add(user3);
        await db.SaveChangesAsync();

        // Execute the exact query from Endpoints.cs /applications/search
        var q = "doe";
        var pattern = $"%{q.Trim()}%";
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern))
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        // Assert: both John and Jane match (case-insensitive "doe"), Bob doesn't
        Assert.Contains(1001L, matchingUserIds);
        Assert.Contains(1002L, matchingUserIds);
        Assert.DoesNotContain(1003L, matchingUserIds);
    }

    [Fact]
    public async Task Search_Matches_On_FirstName_And_LastName_Not_Just_Username()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        // Seed a user with username that won't match but first name that will
        var user = TelegramUser.FirstSighting(
            2001, isBot: false, isPremium: false,
            username: "xyz123", firstName: "Alexander", lastName: "Smith",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Search for "alex" (lowercase, won't match username "xyz123" or last name "smith")
        var q = "alex";
        var pattern = $"%{q.Trim()}%";
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern))
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        // Assert: user is found via first name match
        Assert.Contains(2001L, matchingUserIds);
    }

    [Fact]
    public async Task Search_With_No_Matches_Returns_Empty_List()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        // Seed one user
        var user = TelegramUser.FirstSighting(
            3001, isBot: false, isPremium: false,
            username: "testuser", firstName: "Test", lastName: "User",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Search for a string that matches nothing
        var q = "nonexistent";
        var pattern = $"%{q.Trim()}%";
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern))
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        // Assert: empty result
        Assert.Empty(matchingUserIds);
    }

    [Fact]
    public async Task Search_With_Null_Fields_Does_Not_Break_Query()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        // Seed a user with null username and firstName to exercise the null-coalescing
        var user = TelegramUser.FirstSighting(
            4001, isBot: false, isPremium: false,
            username: null, firstName: null, lastName: "Johnson",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Search for "john" (lowercase) — should match via the lastName field
        var q = "john";
        var pattern = $"%{q.Trim()}%";
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern))
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        // Assert: user is found via lastName, null fields didn't cause false match or error
        Assert.Contains(4001L, matchingUserIds);
    }

    [Fact]
    public async Task Search_Fuzzy_Matches_On_Slightly_Misspelled_Name()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var user = TelegramUser.FirstSighting(
            5001, isBot: false, isPremium: false,
            username: "asmith", firstName: "Alexander", lastName: "Smith",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // "Aleksander" — plausible transliteration/typo of "Alexander". No ILIKE arm would catch
        // this (it's not a substring of "Alexander" or "asmith"/"Smith") — only the new trigram
        // word_similarity clause can.
        var q = "Aleksander";
        var pattern = $"%{q.Trim()}%";
        var normalizedQuery = q.Trim().ToLowerInvariant();
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern) ||
                        EF.Functions.TrigramsWordSimilarity(normalizedQuery, u.NameNormalized) >= FuzzyNameSimilarityThreshold)
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        Assert.Contains(5001L, matchingUserIds);
    }

    [Fact]
    public async Task Search_Fuzzy_Does_Not_Match_Unrelated_Name()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var user = TelegramUser.FirstSighting(
            5002, isBot: false, isPremium: false,
            username: "bobz", firstName: "Bob", lastName: "Zhukovsky",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Same misspelled query as the previous test — proves the threshold isn't so loose that it
        // admits a name with no real resemblance.
        var q = "Aleksander";
        var pattern = $"%{q.Trim()}%";
        var normalizedQuery = q.Trim().ToLowerInvariant();
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern) ||
                        EF.Functions.TrigramsWordSimilarity(normalizedQuery, u.NameNormalized) >= FuzzyNameSimilarityThreshold)
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        Assert.DoesNotContain(5002L, matchingUserIds);
    }

    [Fact]
    public async Task Search_Exact_Substring_Match_Still_Works_Unchanged()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        // Same seed/scenario as Search_Is_Case_Insensitive_And_Substring_Based, re-run through the
        // new combined (ILIKE + trigram) query — proves the fuzzy addition is additive-only.
        var user1 = TelegramUser.FirstSighting(
            5003, isBot: false, isPremium: false,
            username: "JohnDoe", firstName: "John", lastName: "Doe",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);
        var user2 = TelegramUser.FirstSighting(
            5004, isBot: false, isPremium: false,
            username: "janedoe", firstName: "Jane", lastName: "Doe",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);
        var user3 = TelegramUser.FirstSighting(
            5005, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: "Smith",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user1);
        db.Users.Add(user2);
        db.Users.Add(user3);
        await db.SaveChangesAsync();

        var q = "doe";
        var pattern = $"%{q.Trim()}%";
        var normalizedQuery = q.Trim().ToLowerInvariant();
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern) ||
                        EF.Functions.TrigramsWordSimilarity(normalizedQuery, u.NameNormalized) >= FuzzyNameSimilarityThreshold)
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        Assert.Contains(5003L, matchingUserIds);
        Assert.Contains(5004L, matchingUserIds);
        Assert.DoesNotContain(5005L, matchingUserIds);
    }

    [Fact]
    public async Task Search_Fuzzy_Is_Case_Insensitive()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var user = TelegramUser.FirstSighting(
            5006, isBot: false, isPremium: false,
            username: "asmith2", firstName: "Alexander", lastName: "Smith",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Wrong case AND misspelled — proves normalizedQuery (not raw q) is actually wired into the
        // trigram call; a case mismatch alone would otherwise tank the trigram overlap.
        var q = "ALEKSANDER";
        var pattern = $"%{q.Trim()}%";
        var normalizedQuery = q.Trim().ToLowerInvariant();
        var matchingUserIds = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.Username ?? "", pattern) ||
                        EF.Functions.ILike(u.FirstName ?? "", pattern) ||
                        EF.Functions.ILike(u.LastName ?? "", pattern) ||
                        EF.Functions.TrigramsWordSimilarity(normalizedQuery, u.NameNormalized) >= FuzzyNameSimilarityThreshold)
            .Select(u => u.TelegramUserId)
            .ToListAsync();

        Assert.Contains(5006L, matchingUserIds);
    }
}
