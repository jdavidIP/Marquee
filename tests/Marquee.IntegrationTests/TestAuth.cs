using System.Net.Http.Headers;
using System.Net.Http.Json;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.IntegrationTests;

/// <summary>
/// Confirms a just-registered test account (issue #29) and signs it in again. Confirmation still
/// decides two things the database reads directly — whether the account counts toward
/// `totalRegisteredUsers`, and whether it can send or receive friend requests — so helpers that need a
/// full registered participant put this in the mix.
///
/// Goes straight to the database rather than through the emailed link: what these callers need is an
/// account in the confirmed state, not another exercise of the confirmation flow. The fresh sign-in is
/// no longer load-bearing (nothing reads confirmation from the token since #109); it stays because the
/// old login flow these tests drive goes away whole in #112.
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
