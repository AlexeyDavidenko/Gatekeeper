using System.Security.Cryptography;
using System.Text;
using Gatekeeper.Web.Auth;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the Telegram Login Widget signature validation, ensuring cryptographic checks work correctly.
/// </summary>
public class TelegramLoginValidatorTests
{
    private const string TestBotToken = "123456:FAKE-BOT-TOKEN-FOR-TESTS";

    /// <summary>
    /// Helper method to sign fields the way Telegram Login Widget does.
    /// Computes the SHA256-then-HMACSHA256 hash and adds it to the fields dictionary.
    /// </summary>
    private static Dictionary<string, string> SignFields(Dictionary<string, string> fields, string botToken)
    {
        var fieldsToSign = new Dictionary<string, string>(fields);
        fieldsToSign.Remove("hash");

        var dataCheckString = string.Join('\n', fieldsToSign
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(botToken));
        var computed = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHex = Convert.ToHexStringLower(computed);

        fieldsToSign["hash"] = computedHex;
        return fieldsToSign;
    }

    [Fact]
    public void TryValidate_Returns_True_For_Correctly_Signed_Payload()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);
        var maxAge = TimeSpan.FromMinutes(10);

        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.True(result);
        Assert.NotNull(data);
        Assert.Equal(987654321L, data.Id);
        Assert.Equal("johndoe", data.Username);
        Assert.Equal("John", data.FirstName);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Hash_Is_Tampered()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);

        // Tamper with one character of the hash
        var originalHash = signedFields["hash"];
        var tamperedHash = originalHash.Length > 0
            ? originalHash[0] == 'a'
                ? 'b' + originalHash.Substring(1)
                : 'a' + originalHash.Substring(1)
            : originalHash;
        signedFields["hash"] = tamperedHash;

        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Field_Is_Tampered()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);

        // Tamper with the first_name field after signing
        signedFields["first_name"] = "Jane";

        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Auth_Date_Is_Stale()
    {
        var staleCutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", staleCutoff.ToUnixTimeSeconds().ToString() }
        };

        var signedFields = SignFields(fields, TestBotToken);

        // Verify against a maxAge of only 10 minutes
        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Hash_Key_Missing()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        // Don't sign it - just leave it unsigned
        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(fields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Verified_Against_Wrong_Bot_Token()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);

        // Try to validate with a different bot token
        const string wrongBotToken = "999999:WRONG-TOKEN";
        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(signedFields, wrongBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Extracts_All_Available_Fields()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "last_name", "Doe" },
            { "username", "johndoe" },
            { "photo_url", "https://example.com/photo.jpg" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);
        var maxAge = TimeSpan.FromMinutes(10);

        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.True(result);
        Assert.NotNull(data);
        Assert.Equal(987654321L, data.Id);
        Assert.Equal("John", data.FirstName);
        Assert.Equal("Doe", data.LastName);
        Assert.Equal("johndoe", data.Username);
        Assert.Equal("https://example.com/photo.jpg", data.PhotoUrl);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Id_Is_Missing()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);
        var maxAge = TimeSpan.FromMinutes(10);

        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Returns_False_When_Auth_Date_Is_Missing()
    {
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" }
        };

        var signedFields = SignFields(fields, TestBotToken);
        var maxAge = TimeSpan.FromMinutes(10);

        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }

    [Fact]
    public void TryValidate_Handles_Minimal_Payload()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "auth_date", authDate }
        };

        var signedFields = SignFields(fields, TestBotToken);
        var maxAge = TimeSpan.FromMinutes(10);

        var result = TelegramLoginValidator.TryValidate(signedFields, TestBotToken, maxAge, out var data);

        Assert.True(result);
        Assert.NotNull(data);
        Assert.Equal(987654321L, data.Id);
        Assert.Null(data.Username);
        Assert.Null(data.FirstName);
    }

    [Fact]
    public void TryValidate_Returns_False_For_Empty_Hash()
    {
        var authDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var fields = new Dictionary<string, string>
        {
            { "id", "987654321" },
            { "first_name", "John" },
            { "username", "johndoe" },
            { "auth_date", authDate },
            { "hash", "" }
        };

        var maxAge = TimeSpan.FromMinutes(10);
        var result = TelegramLoginValidator.TryValidate(fields, TestBotToken, maxAge, out var data);

        Assert.False(result);
        Assert.Null(data);
    }
}
