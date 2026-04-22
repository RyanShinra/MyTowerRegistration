// =============================================================================
// IMPLEMENT TWELFTH — This is the "composition root" that wires everything.
//
// Program.cs in modern .NET (since .NET 6) uses "top-level statements" —
// no Main() method, no Program class. The file IS the entry point.
//
// Compare to Express/Apollo Server (TypeScript):
//   const app = express();
//   const server = new ApolloServer({ typeDefs, resolvers });
//   await server.start();
//   app.use('/graphql', expressMiddleware(server));
//   app.listen(4000);
//
// Same idea here: create builder → register services → build → map endpoints → run.
//
// The order matters:
//   1. Service registration (DI container setup)    — builder.Services.Add*(...)
//   2. Build the app                                — builder.Build()
//   3. Middleware pipeline (request processing)      — app.Use*(...), app.Map*(...)
//   4. Run                                          — app.Run()
// =============================================================================

using System.Threading.RateLimiting;
using HotChocolate.Execution;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.API;
using MyTowerRegistration.Data;
using MyTowerRegistration.Data.Repositories;

using MyTowerRegistration.API.GraphQL.Queries;
using MyTowerRegistration.API.GraphQL.Mutations;
using MyTowerRegistration.API.GraphQL.Types;
using MyTowerRegistration.API.GraphQL.DataLoaders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// =============================================================================
// SERVICE REGISTRATION (Dependency Injection Container)
// =============================================================================

// --- EF Core + PostgreSQL ---------------------------------------------------
builder.Services.AddDbContext<AppDbContext>((DbContextOptionsBuilder options) => {
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

// --- Repository Layer -------------------------------------------------------
builder.Services.AddScoped<IUserRepository, UserRepository>();

// --- Hot Chocolate GraphQL --------------------------------------------------
builder.Services
    .AddGraphQLServer()
    .AddQueryType<UserQueries>()
    .AddMutationType<UserMutations>()
    .AddType<UserType>()
    .AddDataLoader<UserBatchDataLoader>();


// --- CORS ------------------------------------------------------------------
// CORS (Cross-Origin Resource Sharing) is a browser security mechanism.
// When the Blazor Admin app (e.g. https://xxx.cloudfront.net) calls this API
// (https://alb-dns/api/graphql), the browser considers it a cross-origin
// request and blocks it by default. We opt in by declaring which origins are
// allowed.
//
// The allowed origins come from configuration so they can differ per environment
// without code changes:
//   - Dev: appsettings.Development.json → localhost ports
//   - Prod: AllowedOrigins__0 env var in ECS task definition → CloudFront URL
//
// AllowAnyHeader/AllowAnyMethod: GraphQL uses Content-Type: application/json
// and POST, both standard — permitting all headers/methods is fine here.
//
// IMPORTANT: UseCors("AdminPolicy") must be called BEFORE MapGraphQL() in the
// middleware pipeline below. The browser sends a preflight OPTIONS request
// before the real POST — UseCors handles that response.
string[] allowedOrigins = builder.Configuration
    .GetSection("AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        // If AllowedOrigins is empty (e.g. no env var set in ECS yet), the
        // policy allows nothing — no CORS headers are emitted, browser blocks
        // cross-origin calls. Safe default: no frontend configured, no access.
    });
});

// --- Rate Limiting ----------------------------------------------------------
// ASP.NET Core's built-in rate limiter (System.Threading.RateLimiting, .NET 7+).
// No NuGet package needed — it ships with the framework.
//
// WHY per-IP fixed-window:
//   - There is no authentication yet, so IP address is the only available key.
//   - Fixed window is the simplest policy to reason about: each window resets
//     after WindowSeconds seconds. A sliding window would smooth bursts but
//     is harder to explain and the difference is minor at these limits.
//   - QueueLimit = 0: excess requests are rejected immediately (HTTP 429).
//     Queuing makes sense for paid APIs where you'd rather wait than drop;
//     for an open registration endpoint, queueing just delays abuse, not stops it.
//
// HOW partitioning works:
//   RateLimitPartition.GetFixedWindowLimiter creates a separate counter per
//   partition key (here: the client IP). Two different IPs each get their own
//   independent PermitLimit budget. A single shared global counter would be
//   exhausted by one heavy client and deny everyone else.
//
// CONFIG: see RateLimitingOptions.cs — defaults (30 req / 60 s) live there.
//   Override in ECS via env vars: RateLimiting__PermitLimit, RateLimiting__WindowSeconds
//   Both values validated at startup (≥ 1): app refuses to start if either is zero or negative.
builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection("RateLimiting"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

RateLimitingOptions rateLimitCfg =
    builder.Configuration.GetSection("RateLimiting").Get<RateLimitingOptions>()
    ?? new RateLimitingOptions();

// Shared between AddPolicy and RequireRateLimiting — one string, no typo risk.
const string GraphQLRateLimitPolicy = "GraphQL";

builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.AddPolicy(GraphQLRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // LIMITATION: all requests that arrive with no RemoteIpAddress (can
            // happen in tests and behind some proxies) fall into one shared
            // "unknown" bucket — a single counter for every such client.
            // Once an ALB or reverse proxy sits in front, the balancer's IP
            // becomes every client's RemoteIpAddress and this bucket is
            // exhausted for everyone at once.
            // Fix: call app.UseForwardedHeaders() (or configure
            // ForwardedHeadersOptions) before UseRateLimiter so the
            // X-Forwarded-For header is unwrapped into RemoteIpAddress
            // before the limiter reads it.
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitCfg.PermitLimit,
                Window      = TimeSpan.FromSeconds(rateLimitCfg.WindowSeconds),
                QueueLimit  = 0,
            }
        )
    );

    // OnRejected owns the response — status code, headers, and observability.
    // Setting RejectionStatusCode instead would not let us add Retry-After or log.
    rateLimiterOptions.OnRejected = (context, _) =>
    {
        // RFC 6585: 429 responses SHOULD include Retry-After so clients know
        // when to retry rather than hammering the endpoint.
        // FixedWindowRateLimiter populates MetadataName.RetryAfter with the
        // time remaining in the current window when it rejects a lease.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        ILogger logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyTowerRegistration.RateLimiting");
        logger.LogWarning("Rate limit exceeded for {IP}",
            context.HttpContext.Connection.RemoteIpAddress);

        return ValueTask.CompletedTask;
    };
});

// Keep OpenAPI support from the template
builder.Services.AddOpenApi();

// =============================================================================
// BUILD THE APP
// =============================================================================

var app = builder.Build();

// =============================================================================
// SCHEMA EXPORT (AfterBuild hook — see ExportSchema target in .csproj)
// =============================================================================
//
// When invoked with `--export-schema`, writes schema.graphql to the repo root
// and exits immediately. Kestrel never starts, no port is opened, no DB is touched.
//
// NOTE: builder.Build() (above) does not open a DB connection — EF Core's
// AddDbContext is lazy. However, if appsettings.json contains an unparseable
// connection string (e.g. a malformed placeholder), EF Core may throw during
// service resolution before reaching this guard. If --export-schema fails with
// an EF Core exception, check the connection string placeholder value first.
//
// Hot Chocolate builds the schema entirely from service registrations —
// the HTTP pipeline is irrelevant for schema construction. This is why we can
// return before the middleware section below ever runs.
//
// The MSBuild AfterBuild target calls this automatically on every Debug build,
// so schema.graphql always reflects the current C# types. StrawberryShake
// (in the Admin project) reads schema.graphql at its own build time to
// generate strongly-typed C# client classes.
if (args.Contains("--export-schema"))
{
    IRequestExecutorResolver executorResolver =
        app.Services.GetRequiredService<IRequestExecutorResolver>();
    IRequestExecutor executor = await executorResolver.GetRequestExecutorAsync();

    string header = """
        # GraphQL Schema — MyTowerRegistration (auto-generated — DO NOT EDIT)
        # Regenerated on every Debug build via the ExportSchema MSBuild target.
        # Schema is defined by the C# classes in MyTowerRegistration.API/GraphQL/
        #
        # To regenerate manually:
        #   dotnet run --project MyTowerRegistration.API -- --export-schema

        """;

    string schemaPath = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(Directory.GetCurrentDirectory(), "..", "schema.graphql"));

    Console.WriteLine($"[ExportSchema] Building schema from registered C# types...");
    await File.WriteAllTextAsync(schemaPath, header + executor.Schema.ToString());
    Console.WriteLine($"[ExportSchema] Written to: {schemaPath}");
    return;
}

// =============================================================================
// MIDDLEWARE PIPELINE (order matters — requests flow top to bottom)
// =============================================================================

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Only redirect to HTTPS in environments where TLS is terminated by the app itself.
// In ECS (and Docker Compose), TLS termination is handled externally (or not at all
// for smoke testing), so redirecting would send clients to an unconfigured HTTPS port.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Apply the CORS policy before mapping endpoints. The browser sends a preflight
// OPTIONS request before the real POST — UseCors handles that response.
app.UseCors("AdminPolicy");

// UseRateLimiter must come AFTER UseCors so that CORS preflight OPTIONS
// responses are sent correctly before the rate limiter has a chance to reject
// the request. It must come BEFORE MapGraphQL so the limiter runs on every
// inbound GraphQL operation.
app.UseRateLimiter();

app.MapGraphQL("/api/graphql").RequireRateLimiting(GraphQLRateLimitPolicy);

app.Run();

// =============================================================================
// TEST HOOK — WebApplicationFactory visibility
// =============================================================================
//
// Top-level statements in .NET 6+ generate an *internal* implicit Program class.
// WebApplicationFactory<Program> in the test project can't see internal types, so
// it fails to find the entry point. This partial class declaration makes Program
// public without changing any behavior at runtime.
//
// The "partial" keyword just means "this is part of the same class defined by
// the top-level statements above." Adding it here is the idiomatic .NET pattern;
// the alternative is [assembly: InternalsVisibleTo("...")] in the API csproj.
public partial class Program { }
