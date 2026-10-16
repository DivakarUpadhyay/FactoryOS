using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Divakar.FactoryOS.Application.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace Divakar.FactoryOS.Infrastructure.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "divakar-factoryos";
    public string Audience { get; set; } = "divakar-factoryos";

    /// <summary>At least 32 characters. Comes from user secrets or an environment variable, never from source.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
}

public static class ClaimNames
{
    public const string Tenant = "tid";
    public const string SuperAdmin = "sa";
    public const string Permission = "perm";
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (versioned format, so the cost can rise later).</summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<object> Inner = new();
    private static readonly object User = new();

    public string Hash(string password) => Inner.HashPassword(User, password);

    public bool Verify(string hash, string password)
    {
        try
        {
            return Inner.VerifyHashedPassword(User, hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class JwtTokenService(JwtOptions options, IClock clock) : ITokenService
{
    public AccessToken CreateAccessToken(Guid userId, Guid? tenantId, bool isSuperAdmin, string email, string displayName,
        IEnumerable<string> permissions)
    {
        var expires = clock.UtcNow.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Name, displayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimNames.SuperAdmin, isSuperAdmin ? "1" : "0"),
        };
        if (tenantId is { } t)
        {
            claims.Add(new Claim(ClaimNames.Tenant, t.ToString()));
        }
        claims.AddRange(permissions.Select(p => new Claim(ClaimNames.Permission, p)));
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, clock.UtcNow, expires,
            new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string Token, string Hash) CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        return (token, HashRefreshToken(token));
    }

    public string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static SymmetricSecurityKey SigningKey(JwtOptions options)
    {
        if (options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters (set it in user secrets or FACTORYOS_Jwt__SigningKey).");
        }
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }
}
