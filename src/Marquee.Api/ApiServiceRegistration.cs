using Marquee.Api.Auth;
using Marquee.Api.Realtime;
using Marquee.Api.Services;

namespace Marquee.Api;

public static class ApiServiceRegistration
{
    public static IServiceCollection AddMarqueeApiServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPremiereService, PremiereService>();
        services.AddScoped<IPremiereFactory, PremiereFactory>();
        services.AddScoped<IPremiereOpener, PremiereOpener>();
        services.AddScoped<IPremiereScheduleService, PremiereScheduleService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();

        // --- Real-time (Iteration 3) ---
        services.Configure<RealtimeOptions>(configuration.GetSection(RealtimeOptions.SectionName));
        // The dirty set and the broadcast loop are process-wide: the clap path (scoped) writes into
        // them, the loop drains them. IHubContext is a singleton, so the broadcaster can be one too.
        services.AddSingleton<IClapBroadcastQueue, ClapBroadcastQueue>();
        services.AddSingleton<IPremiereBroadcaster, SignalRPremiereBroadcaster>();
        services.AddHostedService<ClapBroadcastService>();

        return services;
    }
}
