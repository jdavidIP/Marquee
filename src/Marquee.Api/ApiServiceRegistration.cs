using Marquee.Api.Auth;
using Marquee.Api.Realtime;
using Marquee.Api.Security;
using Marquee.Api.Services;
using Marquee.Domain.Options;
using Marquee.Infrastructure.Messaging;

namespace Marquee.Api;

public static class ApiServiceRegistration
{
    public static IServiceCollection AddMarqueeApiServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPremiereService, PremiereService>();
        services.AddScoped<IPremiereFactory, PremiereFactory>();
        services.AddScoped<IMovieCatalog, MovieCatalog>();
        services.AddScoped<IPremiereOpener, PremiereOpener>();
        services.AddScoped<IPremiereScheduleService, PremiereScheduleService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<IPremiereHistoryService, PremiereHistoryService>();
        services.Configure<CognitoOptions>(configuration.GetSection(CognitoOptions.SectionName));
        // Short, because it runs inside a user's first request; the default is 100 seconds. A timeout
        // fails that request and creates nothing, so the next one simply tries again.
        services.AddHttpClient<CognitoUserProvisioner>(http => http.Timeout = TimeSpan.FromSeconds(5));

        // Registered here rather than alongside MarqueeRulesOptions in Infrastructure: the Worker
        // shares that registration and has no auth surface to describe a password for.
        services.Configure<PasswordPolicyOptions>(
            configuration.GetSection(PasswordPolicyOptions.SectionName));

        // --- Security and social (Iteration 5) ---
        services.Configure<AnonymousSessionOptions>(
            configuration.GetSection(AnonymousSessionOptions.SectionName));
        services.Configure<ClapGuardOptions>(configuration.GetSection(ClapGuardOptions.SectionName));
        services.AddSingleton<IAnonymousSessionService, AnonymousSessionService>();

        // Scoped: it caches its answer in HttpContext.Items, so its lifetime is the request's.
        services.AddScoped<IParticipantResolver, ParticipantResolver>();
        services.AddScoped<IFriendshipService, FriendshipService>();
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<IAdminService, AdminService>();

        // --- Dashboard metrics (Iteration 6) ---
        // The connection tracker is a singleton because it is a process-wide count; the queue reader
        // gets a typed HttpClient so it takes part in the shared handler pool rather than opening a
        // fresh socket on every dashboard poll.
        services.AddSingleton<IHubConnectionTracker, HubConnectionTracker>();
        services.AddHttpClient<IQueueDepthReader, RabbitMqQueueDepthReader>();
        services.AddScoped<IAdminMetricsService, AdminMetricsService>();

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
