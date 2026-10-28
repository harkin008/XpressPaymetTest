using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace XpressTest.AuthService.Authorization;

/// <summary>
/// API keys are long-lived credentials a user exchanges at /connect/token for a short-lived
/// access token (grant type <see cref="GrantType"/>). Only their SHA-256 hash is stored.
/// </summary>
public static class ApiKeyDefaults
{
    public const string KeyPrefix = "xpk_";
    public const string GrantType = "urn:xpress:grant-type:api-key";

    public static string Generate() => KeyPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string DisplayPrefix(string key) => key[..Math.Min(12, key.Length)];
}
