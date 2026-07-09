using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the TelegramUser aggregate, covering identity history tracking and language preferences.
/// </summary>
public class TelegramUserTests
{
    private const long TestTelegramUserId = 123456789L;
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Func<string?, string?, string> SimpleNormalize =
        (first, last) => $"{first} {last}".Trim().ToLowerInvariant();

    [Fact]
    public void FirstSighting_Creates_User_With_One_History_Entry()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        Assert.Single(user.History);
        Assert.Equal("john doe", user.NameNormalized);
        Assert.Equal("testuser", user.Username);
        Assert.Equal("John", user.FirstName);
        Assert.Equal("Doe", user.LastName);
        Assert.Equal("Test bio", user.Bio);
        Assert.Equal("photo123", user.PhotoFileId);
    }

    [Fact]
    public void RecordSighting_With_Identical_Fields_Does_Not_Append_History()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        var initialCount = user.History.Count;

        user.RecordSighting(
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            bio: "Test bio",
            photoFileId: "photo123",
            source: "update",
            SimpleNormalize,
            TestNow);

        Assert.Equal(initialCount, user.History.Count);
    }

    [Fact]
    public void RecordSighting_With_Different_Username_Appends_History()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "oldusername",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        var initialCount = user.History.Count;

        user.RecordSighting(
            username: "newusername",
            firstName: "John",
            lastName: "Doe",
            bio: "Test bio",
            photoFileId: "photo123",
            source: "update",
            SimpleNormalize,
            TestNow);

        Assert.Equal(initialCount + 1, user.History.Count);
        Assert.Equal("newusername", user.Username);
    }

    [Fact]
    public void RecordSighting_With_Different_FirstName_Appends_History()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        var initialCount = user.History.Count;

        user.RecordSighting(
            username: "testuser",
            firstName: "Jane",
            lastName: "Doe",
            bio: "Test bio",
            photoFileId: "photo123",
            source: "update",
            SimpleNormalize,
            TestNow);

        Assert.Equal(initialCount + 1, user.History.Count);
        Assert.Equal("Jane", user.FirstName);
    }

    [Fact]
    public void PhotoFileId_Is_Sticky_Once_Set()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "originalphoto",
            SimpleNormalize,
            TestNow);

        Assert.Equal("originalphoto", user.PhotoFileId);

        // Record sighting with null photoFileId - should NOT clear the existing PhotoFileId
        user.RecordSighting(
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            bio: "Updated bio",
            photoFileId: null,
            source: "update",
            SimpleNormalize,
            TestNow);

        Assert.Equal("originalphoto", user.PhotoFileId);
    }

    [Fact]
    public void PhotoFileId_Can_Be_Updated_To_New_Value()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        user.RecordSighting(
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            bio: "Test bio",
            photoFileId: "photo456",
            source: "update",
            SimpleNormalize,
            TestNow);

        Assert.Equal("photo456", user.PhotoFileId);
    }

    [Fact]
    public void SetLanguagePreference_Updates_LanguageCode()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        Assert.Equal("en-US", user.LanguageCode);

        user.SetLanguagePreference("ru");

        Assert.Equal("ru", user.LanguageCode);
    }

    [Fact]
    public void SetLanguagePreference_Overwrites_Previous_Value()
    {
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId,
            isBot: false,
            isPremium: false,
            username: "testuser",
            firstName: "John",
            lastName: "Doe",
            languageCode: "en-US",
            bio: "Test bio",
            photoFileId: "photo123",
            SimpleNormalize,
            TestNow);

        user.SetLanguagePreference("ru");
        Assert.Equal("ru", user.LanguageCode);

        user.SetLanguagePreference("en");
        Assert.Equal("en", user.LanguageCode);
    }
}
