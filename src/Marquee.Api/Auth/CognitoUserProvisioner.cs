using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Marquee.Api.Auth;

/// <summary>
/// Creates the Postgres <see cref="User"/> row for a Cognito account the first time it makes an
/// authenticated request (DEPLOYMENT.md § Phase 2, decision 2). Cognito only issues tokens to
/// confirmed accounts, so every row made here is confirmed by construction.
///
/// The row's id is the token's <c>sub</c> and its username the token's <c>username</c>. Access tokens
/// carry no email (decision 9), so it comes from one <c>GetUser</c> call made with the same token —
/// unsigned, so no AWS credentials — and only here, once per account.
/// </summary>
public sealed class CognitoUserProvisioner(
    HttpClient http,
    MarqueeDbContext db,
    IOptions<CognitoOptions> options,
    ILogger<CognitoUserProvisioner> logger)
{
    /// <summary>
    /// The new account's access, or null when it is refused: the token's username or email already
    /// belongs to a different row (refused rather than merged, because nothing proves the two are the
    /// same person), or is longer than its column — Cognito allows longer of both, and the browser
    /// calls Cognito directly, so no frontend limit is enforceable.
    ///
    /// A refusal is not cached, so a refused account repeats this work on every request. Accepted:
    /// once production holds only rows Cognito created, Cognito's own uniqueness makes a refusal
    /// practically unreachable, and caching one would need a third state in the access cache.
    /// </summary>
    public async Task<UserAccess?> ProvisionAsync(HttpContext context, Guid userId, CancellationToken ct)
    {
        var username = context.User.FindFirstValue("username")
            ?? throw new InvalidOperationException("Cognito access token has no username claim.");
        var token = await context.GetTokenAsync("access_token")
            ?? throw new InvalidOperationException("Cognito access token was not saved on the request.");
        var email = (await GetEmailAsync(token, ct)).ToLowerInvariant();

        if (username.Length > User.UsernameMaxLength || email.Length > User.EmailMaxLength)
        {
            logger.LogWarning(
                "Refused Cognito account {UserId}: username or email is longer than the users table allows.", userId);
            return null;
        }

        db.Users.Add(new User
        {
            Id = userId,
            Username = username,
            Email = email,
            // Cognito holds the password; the column goes with the old auth (#112). An empty hash
            // never verifies, so the old login endpoint cannot sign in as this account.
            PasswordHash = "",
            EmailConfirmedAt = DateTime.UtcNow,
            Role = UserRole.User,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created user {UserId} ({Username}) on first Cognito sign-in.", userId, username);
            return new UserAccess(IsBlocked: false, UserRole.User);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A page load fires several first requests at once, so losing this race is normal: if the
            // row that won is this account's, use it.
            db.ChangeTracker.Clear();
            var existing = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => new UserAccess(u.IsBlocked, u.Role))
                .FirstOrDefaultAsync(ct);
            if (existing is null)
                logger.LogWarning(
                    "Refused Cognito account {UserId}: username {Username} or its email already belongs to another user.",
                    userId, username);
            return existing;
        }
    }

    private async Task<string> GetEmailAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.ServiceUrl)
        {
            // A dictionary, not an anonymous object: JsonContent camel-cases property names, and
            // Cognito's are case-sensitive PascalCase.
            Content = JsonContent.Create(new Dictionary<string, string> { ["AccessToken"] = accessToken },
                new MediaTypeHeaderValue("application/x-amz-json-1.1")),
        };
        request.Headers.Add("X-Amz-Target", "AWSCognitoIdentityProviderService.GetUser");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<GetUserResponse>(ct);
        return user?.UserAttributes.FirstOrDefault(a => a.Name == "email")?.Value
            ?? throw new InvalidOperationException("Cognito GetUser returned no email.");
    }

    // Read case-insensitively (web defaults), so the PascalCase response binds as-is.
    private sealed record GetUserResponse(List<Attribute> UserAttributes);

    private sealed record Attribute(string Name, string Value);
}
