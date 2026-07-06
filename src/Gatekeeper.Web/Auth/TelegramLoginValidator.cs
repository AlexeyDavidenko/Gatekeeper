namespace Gatekeeper.Web.Auth;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public sealed record TelegramLoginData(
    long Id, string? Username, string? FirstName, string? LastName, string? PhotoUrl, DateTimeOffset AuthDate);

/// <summary>
/// Verifies a Telegram Login Widget payload per the official algorithm:
///   secret       = SHA256(bot_token)
///   data_check   = fields except "hash", sorted by key, joined "key=value" with '\n'
///   valid        <=> HMAC-SHA256(secret, data_check) == hash  (and auth_date is fresh)
/// Never trust the client — this runs server-side only.
/// </summary>
public static class TelegramLoginValidator
{
    public static bool TryValidate(
        IReadOnlyDictionary<string, string> fields, string botToken, TimeSpan maxAge, out TelegramLoginData? data)
    {
        data = null;

        if (!fields.TryGetValue("hash", out var hash) || string.IsNullOrEmpty(hash))
            return false;

        var dataCheckString = string.Join('\n', fields
            .Where(kv => kv.Key != "hash")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(botToken));
        var computed = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHex = Convert.ToHexStringLower(computed);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedHex),
                Encoding.ASCII.GetBytes(hash.ToLowerInvariant())))
            return false;

        if (!fields.TryGetValue("auth_date", out var authRaw) ||
            !long.TryParse(authRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authUnix))
            return false;

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authUnix);
        if (DateTimeOffset.UtcNow - authDate > maxAge)
            return false;

        if (!fields.TryGetValue("id", out var idRaw) || !long.TryParse(idRaw, out var id))
            return false;

        data = new TelegramLoginData(
            id,
            fields.GetValueOrDefault("username"),
            fields.GetValueOrDefault("first_name"),
            fields.GetValueOrDefault("last_name"),
            fields.GetValueOrDefault("photo_url"),
            authDate);
        return true;
    }
}
