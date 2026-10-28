using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace XpressTest.AuthService.Tokens;

/// <summary>
/// Holds the RS256 signing key. Other services only ever see the public half, via the JWKS endpoint.
/// </summary>
public sealed class SigningKeyProvider : IDisposable
{
    private readonly RSA _rsa;

    public SigningKeyProvider(IOptions<JwtOptions> options, IHostEnvironment env, ILogger<SigningKeyProvider> logger)
    {
        _rsa = RSA.Create();
        var path = options.Value.SigningKeyPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _rsa.ImportFromPem(File.ReadAllText(Path.GetFullPath(path, env.ContentRootPath)));
        }
        else
        {
            _rsa.KeySize = 2048;
            if (!env.IsDevelopment())
                logger.LogWarning("Jwt:SigningKeyPath is not set; using a per-process signing key. Tokens become invalid on restart.");
        }

        var publicParameters = _rsa.ExportParameters(includePrivateParameters: false);
        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(publicParameters.Modulus!))[..16];

        SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        // Built from public parameters only, so the private key can never leak into JWKS.
        PublicKey = new RsaSecurityKey(publicParameters) { KeyId = keyId };
        PublicJwk = new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = keyId,
            ["n"] = Base64UrlEncoder.Encode(publicParameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(publicParameters.Exponent)
        };
    }

    public SigningCredentials SigningCredentials { get; }
    public SecurityKey PublicKey { get; }
    public IReadOnlyDictionary<string, string> PublicJwk { get; }

    public void Dispose() => _rsa.Dispose();
}
