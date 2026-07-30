using Marquee.Domain.Options;
using Marquee.Domain.Rules;
using Marquee.Infrastructure.Persistence;
using Marquee.Infrastructure.Redis;
using Marquee.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

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

        // --- Redis: the hot path for clap counting (Iteration 2). ---
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
        var redisOpts = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();
        var redisConfig = ConfigurationOptions.Parse(redisOpts.ConnectionString);
        // Keep retrying instead of throwing at startup if Redis isn't up yet (health checks land in Iteration 6).
        redisConfig.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfig));
        services.AddSingleton<IClapCounters, RedisClapCounters>();
        services.AddSingleton<IPremiereCache, RedisPremiereCache>();

        // --- Anti-abuse and social caches (Iteration 5). Singletons for the same reason as the
        // counters: they hold no per-request state, only a multiplexed Redis connection. ---
        services.AddSingleton<IClapGuards, RedisClapGuards>();
        services.AddSingleton<IFriendGraphCache, RedisFriendGraphCache>();
        services.AddSingleton<IUserBlockCache, RedisUserBlockCache>();
        services.AddSingleton<IClapRateTracker, RedisClapRateTracker>();

        var tmdbOpts = configuration.GetSection(TmdbOptions.SectionName).Get<TmdbOptions>() ?? new TmdbOptions();
        if (string.IsNullOrWhiteSpace(tmdbOpts.ApiKey))
        {
            // No key -> offline stub so the app runs without a secret (see StubTmdbClient).
            services.AddSingleton<ITmdbClient, StubTmdbClient>();
        }
        else
        {
            var resilience = tmdbOpts.Resilience;

            services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
            {
                var baseUrl = tmdbOpts.BaseUrl.EndsWith('/') ? tmdbOpts.BaseUrl : tmdbOpts.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);

                // No HttpClient.Timeout: the resilience pipeline below owns timeouts now. Leaving it
                // set would cut attempts off with a TaskCanceledException the retry strategy does not
                // recognise as transient, so a slow TMDB would fail instead of being retried.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            // Retry then break, in that order: retry rides out the blip that one request hit, and
            // the breaker notices when retrying has stopped being worth it and stops sending calls
            // into a service that is plainly down (§4.6 movie selection only runs at Premiere
            // creation, so failing fast there costs one scheduling attempt, not a live Premiere).
            .AddResilienceHandler("tmdb", pipeline =>
            {
                pipeline.AddTimeout(TimeSpan.FromSeconds(resilience.TotalTimeoutSeconds));

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = resilience.RetryCount,
                    Delay = TimeSpan.FromMilliseconds(resilience.BaseDelayMs),
                    BackoffType = DelayBackoffType.Exponential,
                    // Jitter, because the daily generation job creates several Premieres in a loop;
                    // without it their retries would line up and hit TMDB in synchronised waves.
                    UseJitter = true,
                });

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = resilience.BreakerFailureRatio,
                    MinimumThroughput = resilience.BreakerMinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(resilience.BreakerSamplingSeconds),
                    BreakDuration = TimeSpan.FromSeconds(resilience.BreakerDurationSeconds),
                });

                // Innermost, so it bounds a single attempt rather than the whole pipeline.
                pipeline.AddTimeout(TimeSpan.FromSeconds(resilience.AttemptTimeoutSeconds));
            });
        }

        return services;
    }
}
