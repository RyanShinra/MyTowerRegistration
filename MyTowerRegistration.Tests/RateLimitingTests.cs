// =============================================================================
// INTEGRATION TESTS — Rate Limiting
//
// These tests verify the rate-limiting middleware end-to-end: a real HTTP
// request flows through the full ASP.NET Core pipeline and we assert on the
// HTTP status code returned.
//
// This is a different kind of test from UserMutationTests.cs:
//
//   UserMutationTests.cs — UNIT tests
//     - Calls the C# mutation method directly.
//     - Mocks the repository with Moq.
//     - Fast, no network, no HTTP stack.
//     - Tests business logic in isolation.
//
//   RateLimitingTests.cs — INTEGRATION tests
//     - Spins up the full ASP.NET Core app in-process via WebApplicationFactory.
//     - Sends real HTTP requests through the middleware pipeline.
//     - Uses an in-memory EF Core database (no PostgreSQL needed).
//     - Tests that middleware wiring is correct — something unit tests can't cover.
//
// Compare to Node.js / supertest:
//   const app = express(); /* ... */
//   const res = await request(app).post('/graphql').send({ query: '{ __typename }' });
//   expect(res.status).toBe(200);
//
// WebApplicationFactory is the .NET equivalent of supertest: it hosts the app
// in-process and gives you an HttpClient wired to it.
//
// WHY { __typename }:
//   GraphQL's __typename is a built-in meta-field that returns the type name
//   ("Query"). It requires zero resolver execution and no database access,
//   so it's the lightest possible valid GraphQL request — perfect for
//   exercising the middleware layer without side effects.
//
// HOW tests read PermitLimit:
//   The rate limiter reads PermitLimit from configuration at startup, before
//   WebApplicationFactory's ConfigureAppConfiguration hook fires for minimal-
//   API apps — so we can't inject a test-only value. Instead, each test reads
//   the limit back out of the factory's already-loaded IConfiguration:
//
//     factory.Services.GetRequiredService<IConfiguration>()
//                      .GetValue<int>("RateLimiting:PermitLimit", 30)
//
//   This means tests automatically stay in sync if appsettings.json changes —
//   no separate constant to update.
// =============================================================================

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTowerRegistration.Data;
using System.Net;
using System.Text;

namespace MyTowerRegistration.Tests;

public class RateLimitingTests
{
    // -------------------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------------------

    // Creates a fresh WebApplicationFactory per test so each starts with an
    // empty rate-limit counter. Sharing a factory via IClassFixture would let
    // one test's requests exhaust another test's budget — order-dependent failures.
    //
    // ConfigureServices: swaps the real PostgreSQL DbContext for in-memory so
    // the server starts cleanly without a running database. The rate limiter
    // fires before any resolver runs, so the DB is never touched in the
    // "expect 429" case — but we still swap it so the "expect non-429" cases
    // can complete a full request cycle without error.
    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove the real PostgreSQL options registration and replace
                // it with an in-memory database so tests don't need a server.
                ServiceDescriptor? postgresDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (postgresDescriptor is not null)
                    services.Remove(postgresDescriptor);

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("RateLimitTestDb"));
            });
        });
    }

    // HttpContent is disposed after a PostAsync call, so create a fresh instance
    // per request. The { __typename } body is the lightest valid GraphQL document.
    private static StringContent MakeRequest() =>
        new("""{"query":"{ __typename }"}""", Encoding.UTF8, "application/json");

    // Reads the production permit limit from the factory's loaded configuration —
    // stays in sync with appsettings.json automatically, no separate constant to maintain.
    private static int ReadPermitLimit(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetRequiredService<IConfiguration>()
            .GetValue<int>("RateLimiting:PermitLimit", 30);

    // -------------------------------------------------------------------------
    // TESTS
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GraphQL_RequestsUpToPermitLimit_ReturnNon429()
    {
        // Arrange
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        int permitLimit = ReadPermitLimit(factory);

        // Act + Assert — every request within the window should pass through.
        // "Non-429" means the rate limiter allowed the request; the response body
        // may contain GraphQL errors (e.g. no such field) but that's irrelevant here.
        for (int requestNumber = 1; requestNumber <= permitLimit; requestNumber++)
        {
            HttpResponseMessage response = await client.PostAsync("/api/graphql", MakeRequest());

            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode,
                $"Request {requestNumber} of {permitLimit} was unexpectedly rate-limited.");
        }
    }

    [Fact]
    public async Task GraphQL_FirstRequestExceedingPermitLimit_Returns429()
    {
        // Arrange
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        int permitLimit = ReadPermitLimit(factory);

        // Exhaust the full budget — assert each warmup request passes so that
        // a misconfigured factory (e.g. rate limiter not wired) fails here with a
        // clear message rather than a confusing false-positive on the final assertion.
        for (int requestNumber = 1; requestNumber <= permitLimit; requestNumber++)
        {
            HttpResponseMessage warmup = await client.PostAsync("/api/graphql", MakeRequest());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, warmup.StatusCode,
                $"Warmup request {requestNumber} of {permitLimit} was unexpectedly rate-limited.");
        }

        // Act — this is the (permitLimit + 1)th request in the same window
        HttpResponseMessage limitedResponse = await client.PostAsync("/api/graphql", MakeRequest());

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
    }

    [Fact]
    public async Task GraphQL_RequestExceedingLimit_ResponseBodyIsEmpty()
    {
        // WHAT this tests and WHY it matters:
        //   The rate limiter short-circuits before Hot Chocolate runs, so ASP.NET
        //   Core returns a bare 429 with no body — not the GraphQL error envelope
        //   { "errors": [{ "message": "..." }] } that Hot Chocolate would produce.
        //
        //   A Blazor (or any other) client that tries to deserialize the 429 body
        //   as a GraphQL response will get null/empty fields with no exception.
        //   The silent failure looks like a successful empty response rather than
        //   an error. Clients MUST branch on the HTTP status code first:
        //
        //     if (response.StatusCode == HttpStatusCode.TooManyRequests) { ... }
        //     // only then attempt to read the body as GraphQL JSON
        //
        // WHY this test is fragile (and why we keep it anyway):
        //   Assert.Empty(body) relies on ASP.NET Core's rate limiter never writing
        //   a body on 429 — which is true today but is an undocumented
        //   implementation detail. A future middleware (e.g. a problem-details
        //   formatter added to the pipeline) could legally add a body to 429
        //   responses, and this test would fail for the wrong reason.
        //
        //   We keep the test because it documents a real client contract: do not
        //   parse the 429 body. If it ever fails due to a body being added, the
        //   right fix is to update both the test and the client-side 429 handler
        //   together — not just delete the assertion.

        // Arrange
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        int permitLimit = ReadPermitLimit(factory);

        for (int requestNumber = 1; requestNumber <= permitLimit; requestNumber++)
        {
            HttpResponseMessage warmup = await client.PostAsync("/api/graphql", MakeRequest());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, warmup.StatusCode,
                $"Warmup request {requestNumber} of {permitLimit} was unexpectedly rate-limited.");
        }

        // Act
        HttpResponseMessage limitedResponse = await client.PostAsync("/api/graphql", MakeRequest());
        string body = await limitedResponse.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedResponse.StatusCode);
        Assert.Empty(body);
    }
}
