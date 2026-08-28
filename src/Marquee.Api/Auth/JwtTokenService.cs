using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Marquee.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Marquee.Api.Auth;

public interface IJwtTokenService
{
    string CreateToken(User user);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _opts = options.Value;

    public string CreateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // Issue #29: whether this account was confirmed at issue time — see IsEmailConfirmed's
            // doc comment for why staleness in this one direction is acceptable.
            new(ClaimsPrincipalExtensions.EmailConfirmedClaimType, (user.EmailConfirmedAt != null).ToString())
        };

        // Permissions are stamped into the token from the role at issue time, so authorisation is a
        // claim check with no database round trip. The cost is that a permission change only takes
        // effect on the holder's next login — acceptable for capabilities that change rarely, and
        // deliberately not how blocking works: that has to bite immediately, so it is checked per
        // request against Redis instead (see BlockedUserMiddleware).
        claims.AddRange(RolePermissions.For(user.Role)
            .Select(permission => new Claim(MarqueePermissions.ClaimType, permission)));

        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_opts.ExpiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
