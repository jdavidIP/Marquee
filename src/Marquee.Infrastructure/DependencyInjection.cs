using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Marquee.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMarqueeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Tunable domain rules (CLAUDE.md §7 — no magic numbers in code).
        services.Configure<MarqueeRulesOptions>(configuration.GetSection(MarqueeRulesOptions.SectionName));
        services.Configure<TmdbOptions>(configuration.GetSection(TmdbOptions.SectionName));

        // Randomness abstraction used by the formula "draw" layer and TMDB selection.
        services.AddSingleton<IRandomSource, SystemRandomSource>();

        services.AddDbContext<MarqueeDbContext>(opt =>
            opt.UseNpgsql(configuration.GetConnectionString("Postgres")));

        var tmdbOpts = configuration.GetSection(TmdbOptions.SectionName).Get<TmdbOptions>() ?? new TmdbOptions();
        if (string.IsNullOrWhiteSpace(tmdbOpts.ApiKey))
        {
            // No key -> offline stub so the app runs without a secret (see StubTmdbClient).
            services.AddSingleton<ITmdbClient, StubTmdbClient>();
        }
        else
        {
            services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
            {
                var baseUrl = tmdbOpts.BaseUrl.EndsWith('/') ? tmdbOpts.BaseUrl : tmdbOpts.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(15);
            });
        }

        return services;
    }
}
