using System.Text;
using Marquee.Api;
using Marquee.Api.Auth;
using Marquee.Api.Messaging;
using Marquee.Api.Observability;
using Marquee.Api.Realtime;
using Marquee.Api.Scheduling;
using Marquee.Api.Security;
using Marquee.Domain.Entities;
using Marquee.Domain.Enums;
using Marquee.Infrastructure;
using Marquee.Infrastructure.Observability;
using Marquee.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// A bootstrap logger, replaced by the configured one as soon as the host is built. Without it, any
// failure before that point — bad connection string, unreadable config, a missing Jwt:Key — would be
// written by whatever default logger happened to exist, or not at all. Startup is exactly when you
// most need the log to work.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: MarqueeLogging.OutputTemplate)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "MarqueeSpa";

// --- Logging (Iteration 6) ---
// ReadFrom.Configuration keeps levels and overrides in appsettings (CLAUDE.md §7); the enrichers are
// added in code because they are structural, not tunable. FromLogContext is what makes
// LogContext.PushProperty work; the correlation enricher is what carries an id across the queue hop.
builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.With<CorrelationIdEnricher>()
    .Enrich.WithProperty(MarqueeLogging.ServiceProperty, MarqueeLogging.ApiServiceName));

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
builder.Services.AddMarqueeRateLimiting(builder.Configuration);

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

// First in the pipeline on purpose: everything after this point, including the request-logging
// middleware immediately below and any exception handler, should be able to name the journey it is
// talking about.
app.UseCorrelationId();

// One structured summary line per request instead of ASP.NET Core's several. Placed after the
// correlation middleware so that line carries the id, and before the rest so it still times — and
// still reports — requests that are rejected by the rate limiter or the block check.
app.UseSerilogRequestLogging();

app.UseCors(CorsPolicy);
// Explicit, because the order of the next four is load-bearing. Authentication first so the rate
// limiter and the block check both know who is calling; the block check before the rate limiter so
// a blocked account cannot spend a bucket; the rate limiter after routing so it can see the
// per-endpoint [EnableRateLimiting] metadata.
app.UseRouting();
app.UseAuthentication();
app.UseBlockedUserCheck();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapHub<PremiereHub>(HubRoutes.Premieres);

app.Run();

// Seeds a single admin so Premieres can be created out of the box in dev.
// ILogger is qualified because `using Serilog` brings a second, unrelated ILogger into scope here.
static async Task SeedAdminAsync(
    IServiceProvider sp, IConfiguration config, Microsoft.Extensions.Logging.ILogger logger)
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
