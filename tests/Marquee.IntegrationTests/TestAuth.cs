using System.Net.Http.Headers;
using System.Net.Http.Json;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Confirms a just-registered test account (issue #29) and swaps the client's bearer token for one
/// that reflects it — registering alone no longer makes an account a full registered participant, and
/// every existing helper that assumed otherwise needs this in the mix to keep acting as one.
///
/// Goes straight to the database rather than through the emailed link: what these callers need is an
/// account in the confirmed state, not another exercise of the confirmation flow itself, which
/// RegistrationConfirmationTests covers directly. Logging back in afterward is what actually matters
/// here — it is the same "re-authenticate to pick up a state change" story a real client follows, and
/// it is what makes the bearer token this method hands back carry EmailConfirmed=true.
/// </summary>
public static class TestAuth
{
    private sealed record LoginResponse(string Token);

    public static async Task ConfirmAsync(
        MarqueeAppFactory factory, HttpClient client, string username, string password)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
            await db.Users
                .Where(u => u.Username == username)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmedAt, DateTime.UtcNow));
        }

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { usernameOrEmail = username, password });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
    }
}
