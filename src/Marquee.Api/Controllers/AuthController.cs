using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Security;
using Marquee.Api.Services;
using Marquee.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth, MarqueeDbContext db) : ControllerBase
{
    // Register and login are partitioned by IP rather than by participant: an attacker working
    // through a credential list has no identity of their own to throttle, and the account they are
    // guessing at is the victim's, not theirs.
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        try
        {
            var result = await auth.RegisterAsync(request, ct);
            return Ok(result);
        }
        catch (PasswordRejectedException ex)
        {
            // Both shapes on purpose: `error` is the single line every Marquee failure carries and
            // the web client already reads, `problems` is the same content itemised for a form that
            // wants to mark each rule separately.
            return BadRequest(new { error = ex.Message, problems = ex.Problems });
        }
        catch (RegistrationConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// What a password has to satisfy, so the registration form can say so before anyone submits.
    /// Anonymous, and deliberately so — it is a description of the front door, needed by people who
    /// have not come through it yet, and it reveals nothing an attempted registration would not.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("password-rules")]
    public ActionResult<PasswordRulesDto> PasswordRules() => Ok(auth.DescribePasswordRules());

    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, ct);
        if (result is null)
            return Unauthorized(new { error = "Invalid credentials." });
        return Ok(result);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? NotFound() : Ok(UserDto.From(user));
    }
}
