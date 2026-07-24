using Marquee.Api.Auth;
using Marquee.Api.Dtos;
using Marquee.Api.Services;
using Marquee.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Marquee.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth, MarqueeDbContext db) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        try
        {
            var result = await auth.RegisterAsync(request, ct);
            return Ok(result);
        }
        catch (RegistrationConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

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
