using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Auth;

public sealed class JwtTokenService
{
    private readonly AuthOptions _options;

    public JwtTokenService(IOptions<AuthOptions> options) => _options = options.Value;

    public string IssueToken(OperatorUser user, out DateTime expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(_options.JwtSecret))
            throw new InvalidOperationException("Auth:JwtSecret is missing. The bootstrapper should have generated one.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        expiresUtc = DateTime.UtcNow.AddHours(Math.Max(1, _options.TokenLifetimeHours));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresUtc,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generates a fresh 256-bit secret. Used by the bootstrapper if the configured
    /// JwtSecret is blank, so first-run installs work without manual setup.
    /// </summary>
    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }
}
