using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BelgeRag.Api.Auth;

public sealed record DemoUser
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Role { get; init; } = Roles.User;
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public sealed record JwtOptions
{
    public const string Section = "Auth";
    public string Issuer { get; init; } = "belge-rag";
    public string Audience { get; init; } = "belge-rag-clients";

    /// <summary>HMAC-SHA256 anahtarı; en az 32 bayt. Üretimde ortam değişkeni/secret store'dan gelmeli.</summary>
    public string SigningKey { get; init; } = "";

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Demo kullanıcılar. Gerçek bir kimlik sağlayıcısının (Keycloak, Entra ID) yerini tutar.</summary>
    public List<DemoUser> Users { get; init; } = [];

    public SymmetricSecurityKey Key()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Auth:SigningKey en az 32 bayt olmalı.");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt, string Username, string Role);

/// <summary>Singleton: durumsuz; JsonWebTokenHandler iş parçacığı güvenli.</summary>
public sealed class JwtTokenService(JwtOptions options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken? Login(string username, string password)
    {
        var user = options.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal));
        // Kullanıcı yoksa da karşılaştırma yap: yanıt süresi kullanıcı adının varlığını sızdırmasın.
        var expected = Encoding.UTF8.GetBytes(user?.Password ?? "\0no-such-user\0");
        var given = Encoding.UTF8.GetBytes(password);
        var ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(given));
        if (user is null || !ok) return null;
        return Issue(user.Username, user.Role);
    }

    public IssuedToken Issue(string username, string role)
    {
        var now = clock.GetUtcNow();
        var expires = now + options.Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(options.Key(), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, expires, username, role);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = options.Key(),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
    };
}
