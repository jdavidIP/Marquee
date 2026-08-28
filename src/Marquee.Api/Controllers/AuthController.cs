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

    /// <summary>
    /// What the confirm-email link in the notification actually opens (issue #29). GET, because a
    /// mailto link is clicked, not submitted — the token is the credential, not the method.
    ///
    /// Anonymous and rate-limited by IP for the same reason register/login are: whoever holds a
    /// token proves themselves by presenting it, not by being signed in already, and an attacker
    /// guessing at tokens has no account of their own to throttle instead.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpGet("confirm-email")]
    public async Task<ActionResult<AuthResponse>> ConfirmEmail([FromQuery] string token, CancellationToken ct)
    {
        var result = await auth.ConfirmEmailAsync(token, ct);
        return result is null
            ? BadRequest(new { error = "This confirmation link is invalid or has expired." })
            : Ok(result);
    }

    /// <summary>
    /// Always the same response whether or not <paramref name="request"/>'s address is registered
    /// (issue #31). A response that differed would turn this into a user-enumeration oracle — and
    /// that matters more here than it might elsewhere, because usernames are already publicly
    /// searchable, so confirming which *emails* are registered would be a real leak on top of that.
    /// AuthService does the divergent work internally rather than this method short-circuiting, so
    /// the one thing this endpoint must never do stays true regardless of how the two paths are
    /// timed, not just how they're worded.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await auth.RequestPasswordResetAsync(request.Email, ct);
        return Ok(new { message = "If that address is registered, a reset link has been sent." });
    }

    /// <summary>
    /// What the reset link points at, once a frontend exists to collect the new password and POST
    /// here (issue #47 tracks the confirm-email page; a reset page belongs alongside it) — unlike
    /// confirm-email, this can never be the link itself, since setting a password needs a form.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        try
        {
            var ok = await auth.ResetPasswordAsync(request.Token, request.NewPassword, request.ConfirmPassword, ct);
            return ok
                ? Ok(new { message = "Password reset. Sign in with your new password." })
                : BadRequest(new { error = "This reset link is invalid, expired, or already used." });
        }
        catch (PasswordRejectedException ex)
        {
            return BadRequest(new { error = ex.Message, problems = ex.Problems });
        }
    }

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
