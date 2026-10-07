using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Controllers;

/// <summary>
/// Sign-up, sign-in, confirmation and password reset are the user pool's: the browser calls Cognito
/// directly and presents the access token it gets back (DEPLOYMENT.md § Phase 2). What is left here is
/// what only the API can answer.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(MarqueeDbContext db, IOptions<PasswordPolicyOptions> passwordPolicy) : ControllerBase
{
    /// <summary>
    /// What a password has to satisfy, so the registration form can say so before anyone submits.
    /// Anonymous, and deliberately so — it is a description of the front door, needed by people who
    /// have not come through it yet, and it reveals nothing an attempted sign-up would not.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("password-rules")]
    public ActionResult<PasswordRulesDto> PasswordRules() =>
        Ok(new PasswordRulesDto(passwordPolicy.Value.MinLength, passwordPolicy.Value.RequireDigit));

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
