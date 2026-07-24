using Marquee.Api.Auth;
using Marquee.Api.Services;

namespace Marquee.Api;

public static class ApiServiceRegistration
{
    public static IServiceCollection AddMarqueeApiServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPremiereService, PremiereService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
        return services;
    }
}
