using System.Text;
using Marquee.Api;
using Marquee.Api.Auth;
using Marquee.Api.Messaging;
using Marquee.Api.Realtime;
using Marquee.Api.Scheduling;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure;
using Marquee.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "MarqueeSpa";

// --- Options ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be configured and at least 32 characters.");

// --- Infrastructure + API services ---
builder.Services.AddMarqueeInfrastructure(builder.Configuration);
builder.Services.AddMarqueeApiServices(builder.Configuration);
builder.Services.AddMarqueeScheduling(builder.Configuration);
builder.Services.AddMarqueeApiMessaging(builder.Configuration);

// --- Auth ---
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
        };

        // WebSocket and server-sent-event connections cannot carry an Authorization header, so the
        // SignalR client passes the token as a query string parameter on the hub URL. Accept it
        // only for hub paths — everywhere else the header remains the only way in.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments(HubRoutes.Premieres))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options => options.AddMarqueePolicies());

// --- Web ---
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins("http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()
    // SignalR's browser client sends credentials on its negotiate request; with an explicit
    // origin list this is safe (and Allow-Credentials is incompatible with a wildcard origin).
    .AllowCredentials()));

var app = builder.Build();

// --- Migrate + seed (dev convenience) ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MarqueeDbContext>();
    await db.Database.MigrateAsync();
    await SeedAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<PremiereHub>(HubRoutes.Premieres);

app.Run();

// Seeds a single admin so Premieres can be created out of the box in dev.
static async Task SeedAdminAsync(IServiceProvider sp, IConfiguration config, ILogger logger)
{
    var db = sp.GetRequiredService<MarqueeDbContext>();
    var hasher = sp.GetRequiredService<IPasswordHasherService>();

    var username = config["Admin:Username"] ?? "admin";
    var email = (config["Admin:Email"] ?? "admin@marquee.local").ToLowerInvariant();
    var password = config["Admin:Password"] ?? "admin12345";

    if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
        return;

    var admin = new User { Username = username, Email = email, Role = UserRole.Admin };
    admin.PasswordHash = hasher.Hash(admin, password);
    db.Users.Add(admin);
    await db.SaveChangesAsync();
    logger.LogInformation("Seeded admin user '{Username}' (password from config or default).", username);
}

// Exposed so the integration test project can drive the API with WebApplicationFactory.
public partial class Program;
