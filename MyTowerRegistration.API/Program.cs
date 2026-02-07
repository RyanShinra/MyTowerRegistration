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

using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.Data;
using MyTowerRegistration.Data.Repositories;

// TODO: Uncomment these once you've implemented the GraphQL classes:
// using MyTowerRegistration.API.GraphQL.Queries;
// using MyTowerRegistration.API.GraphQL.Mutations;
// using MyTowerRegistration.API.GraphQL.Types;
// using MyTowerRegistration.API.GraphQL.DataLoaders;

var builder = WebApplication.CreateBuilder(args);

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

// Keep OpenAPI support from the template
builder.Services.AddOpenApi();

// =============================================================================
// BUILD THE APP
// =============================================================================

var app = builder.Build();

// =============================================================================
// MIDDLEWARE PIPELINE (order matters — requests flow top to bottom)
// =============================================================================

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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

app.Run();
