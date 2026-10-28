using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using XpressTest.AuthService.Authorization;
using XpressTest.AuthService.Data;
using XpressTest.AuthService.Models;
using XpressTest.Shared.Auth;

namespace XpressTest.AuthService.Tokens;

public record TokenResult(string AccessToken, int ExpiresIn, string? RefreshToken, string? Scope);

public enum RefreshFailure
{
    None,
    Invalid,
    Reused,
    Expired,
    UserInvalid
}

/// <summary>Issues access tokens (JWT) and manages refresh tokens.</summary>
public sealed class TokenService(
    ApplicationDbContext db,
    SigningKeyProvider keys,
    IOptions<JwtOptions> options,
    TimeProvider clock)
{
    private static readonly JsonWebTokenHandler Handler = new();

    private JwtOptions Options => options.Value;

    /// <summary>Access token (+ refresh token when <paramref name="withRefreshToken"/>) for a user.</summary>
    public async Task<TokenResult> IssueForUserAsync(
        ApplicationUser user, IEnumerable<string> roles, bool withRefreshToken, long? apiKeyId = null, CancellationToken ct = default)
    {
        var claims = new Dictionary<string, object>
        {
            [XpressClaims.Subject] = user.Id.ToString(),
            [XpressClaims.Email] = user.Email ?? string.Empty,
            [XpressClaims.Name] = string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? user.Id.ToString() : user.FullName,
            [XpressClaims.Role] = roles.ToArray(),
            [JwtRegisteredClaimNames.Aud] = Options.UserAudiences.ToArray()
        };
        if (apiKeyId is not null) claims[XpressClaims.ApiKeyId] = apiKeyId.Value.ToString();

        var accessToken = CreateAccessToken(claims, Options.AccessTokenLifetime);
        var refreshToken = withRefreshToken ? await CreateRefreshTokenAsync(user, Guid.NewGuid(), ct) : null;
        return new TokenResult(accessToken, (int)Options.AccessTokenLifetime.TotalSeconds, refreshToken, null);
    }

    /// <summary>Access token for a service (client_credentials). Audience = the services owning the granted scopes.</summary>
    public TokenResult IssueForClient(ClientDefinition client, IReadOnlyCollection<string> scopes)
    {
        var scope = string.Join(' ', scopes);
        var claims = new Dictionary<string, object>
        {
            [XpressClaims.Subject] = client.ClientId,
            [XpressClaims.ClientId] = client.ClientId,
            [XpressClaims.Scope] = scope,
            [JwtRegisteredClaimNames.Aud] = scopes.Select(s => Options.ApiScopes[s]).Distinct().ToArray()
        };
        return new TokenResult(CreateAccessToken(claims, Options.ClientTokenLifetime),
            (int)Options.ClientTokenLifetime.TotalSeconds, null, scope);
    }

    /// <summary>
    /// Validates and rotates a refresh token. On success returns the user and a new refresh token
    /// in the same family; the caller issues the matching access token.
    /// </summary>
    public async Task<(RefreshFailure Failure, ApplicationUser? User, string? NewRefreshToken)> RotateRefreshTokenAsync(
        string refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = ApiKeyDefaults.Hash(refreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return (RefreshFailure.Invalid, null, null);

        if (stored.RevokedAtUtc is not null)
        {
            // A rotated token was replayed: assume it was stolen and end the whole session family.
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return (RefreshFailure.Reused, null, null);
        }
        if (stored.ExpiresAtUtc <= now) return (RefreshFailure.Expired, null, null);

        var user = stored.User;
        if (user is null || !user.IsActive || user.SecurityStamp != stored.SecurityStamp || user.LockoutEnd > clock.GetUtcNow())
        {
            stored.RevokedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return (RefreshFailure.UserInvalid, null, null);
        }

        stored.RevokedAtUtc = now;
        var next = await CreateRefreshTokenAsync(user, stored.FamilyId, ct); // saves both changes
        return (RefreshFailure.None, user, next);
    }

    /// <summary>RFC 7009 revocation (logout): ends the token's whole family. Unknown tokens are ignored.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = ApiKeyDefaults.Hash(refreshToken);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (familyId is { } id) await RevokeFamilyAsync(id, ct);
    }

    private string CreateAccessToken(Dictionary<string, object> claims, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N");
        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Options.Issuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            TokenType = XpressTokenTypes.AccessToken,
            SigningCredentials = keys.SigningCredentials
        });
    }

    private async Task<string> CreateRefreshTokenAsync(ApplicationUser user, Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = ApiKeyDefaults.Hash(token),
            FamilyId = familyId,
            SecurityStamp = user.SecurityStamp ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAtUtc = now + Options.RefreshTokenLifetime
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var family = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var token in family) token.RevokedAtUtc = now;
        await db.SaveChangesAsync(ct);
    }
}
