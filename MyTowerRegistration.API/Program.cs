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

using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.Data;
using MyTowerRegistration.Data.Repositories;

// TODO: Uncomment these once you've implemented the GraphQL classes:
using MyTowerRegistration.API.GraphQL.Queries;
using MyTowerRegistration.API.GraphQL.Mutations;
using MyTowerRegistration.API.GraphQL.Types;
using MyTowerRegistration.API.GraphQL.DataLoaders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// =============================================================================
// SERVICE REGISTRATION (Dependency Injection Container)
// =============================================================================

// --- EF Core + PostgreSQL ---------------------------------------------------
// TODO 1: Register AppDbContext with PostgreSQL
//
//   builder.Services.AddDbContext<AppDbContext>(options =>
//       options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
//
//   What this does:
//   - Registers AppDbContext as a "scoped" service (one instance per HTTP request)
//   - Configures it to use PostgreSQL via the Npgsql provider
//   - Reads the connection string from appsettings.json → ConnectionStrings.DefaultConnection
//
//   Compare to Node.js:
//     const pool = new Pool({ connectionString: process.env.DATABASE_URL });
//   But here it's managed by DI — you never manually create/dispose connections.
builder.Services.AddDbContext<AppDbContext>((DbContextOptionsBuilder options) => {
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

// --- Repository Layer -------------------------------------------------------
// TODO 2: Register the UserRepository
//
//   builder.Services.AddScoped<IUserRepository, UserRepository>();
//
//   What this does:
//   - Maps the interface IUserRepository → concrete class UserRepository
//   - "Scoped" means one instance per HTTP request (matches DbContext lifetime)
//   - When Hot Chocolate resolvers ask for IUserRepository, DI creates a UserRepository
//     and injects the AppDbContext into it automatically
//
//   Compare to Node.js:
//     // In Apollo, you'd set up dataSources or inject via context
//     context: () => ({ userRepo: new UserRepository(pool) })
//
//   Three DI lifetimes in .NET:
//     Transient → new instance every time (like calling `new` each time)
//     Scoped    → one instance per request (what we want for DB access)
//     Singleton → one instance for the app's lifetime (for stateless services)
builder.Services.AddScoped<IUserRepository, UserRepository>();

// --- Hot Chocolate GraphQL --------------------------------------------------
// TODO 3: Register the GraphQL server
//
//   builder.Services
//       .AddGraphQLServer()
//       .AddQueryType<UserQueries>()
//       .AddMutationType<UserMutations>()
//       .AddType<UserType>()
//       .AddDataLoader<UserBatchDataLoader>();
//
//   Compare to Apollo Server:
//     const server = new ApolloServer({
//       typeDefs,
//       resolvers,
//       dataSources: () => ({ ... })
//     });
//
//   Hot Chocolate v15 NOTE: With [QueryType] and [MutationType] attributes
//   on your classes, you can alternatively use .AddTypes() to auto-discover
//   all annotated types. But explicit registration is clearer for learning.
builder.Services
    .AddGraphQLServer()
    .AddQueryType<UserQueries>()
    .AddMutationType<UserMutations>()
    .AddType<UserType>()
    .AddDataLoader<UserBatchDataLoader>();


// --- CORS ------------------------------------------------------------------
// CORS (Cross-Origin Resource Sharing) is a browser security mechanism.
// When the Blazor Admin app (localhost:5273) calls this API (localhost:5026),
// the browser considers it a cross-origin request and blocks it by default.
// We opt in by declaring exactly which origins are allowed.
//
// The allowed origins come from configuration (appsettings.Development.json
// in dev, appsettings.json in prod) so they can differ per environment
// without code changes. In production this will be https://admin.mytower.dev.
//
// AllowAnyHeader / AllowAnyMethod: GraphQL uses Content-Type: application/json
// and POST — these are standard, so permitting all headers/methods is fine here.
// For a public API you'd be more restrictive.
//
// IMPORTANT: UseCors() must be called BEFORE MapGraphQL() in the middleware
// pipeline. Middleware order is significant — requests flow top to bottom.
string[] allowedOrigins = builder.Configuration
    .GetSection("AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
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

// TODO 4: Map the GraphQL endpoint
//
//   app.MapGraphQL();
//
//   This maps the /graphql endpoint and enables:
//   - POST /graphql      → query/mutation execution
//   - GET  /graphql      → serves the Nitro GraphQL IDE (like GraphiQL/Sandbox)
//   - WebSocket /graphql → subscriptions (not used here)
//
//   Compare to Apollo: app.use('/graphql', expressMiddleware(server));
//   Default path is /graphql. Customize with: app.MapGraphQL("/api/graphql");

// Apply the CORS policy before mapping endpoints. The browser sends a preflight
// OPTIONS request before the real POST — UseCors handles that response.
app.UseCors("AdminPolicy");

app.MapGraphQL("/api/graphql");

app.Run();
