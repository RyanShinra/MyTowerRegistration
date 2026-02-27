// =============================================================================
// BONUS TESTS — Integration-style tests using EF Core's InMemory provider.
//
// These test the actual UserRepository against a real (in-memory) DbContext.
// No mocking needed — you're testing real EF Core queries.
//
// Compare to TypeScript:
//   In TS, you might use an in-memory SQLite DB for integration tests.
//   EF Core's InMemory provider is similar — fast, disposable, no setup.
//
// CAVEAT: InMemory provider doesn't enforce unique constraints or foreign keys.
// For testing uniqueness, you'd need SQLite provider. InMemory is good enough
// for basic CRUD tests.
// =============================================================================

using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.Data;
using MyTowerRegistration.Data.Models;
using MyTowerRegistration.Data.Repositories;

namespace MyTowerRegistration.Tests;

public class UserRepositoryTests
{
    /// <summary>
    /// Helper: creates a fresh in-memory DbContext for each test.
    /// Each test gets an isolated database (the unique name guarantees it).
    /// </summary>
    private static AppDbContext CreateInMemoryContext()
    {
        // TODO: Create DbContextOptions with InMemoryDatabase
           var options = new DbContextOptionsBuilder<AppDbContext>()
               .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
               .Options;
        return new AppDbContext(options);
    }

    // -------------------------------------------------------------------------
    // TEST 1: AddAsync persists and returns the user
    // -------------------------------------------------------------------------
    // TODO: Implement
    //
    [Fact]
    public async Task AddAsync_SavesUserAndAssignsId()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var user = new User { Username = "test", Email = "t@t.com", PasswordHash = "hash" };
        var result = await repo.AddAsync(user, CancellationToken.None);

        Assert.True(result.Id > 0);  // InMemory auto-generates IDs
        Assert.Equal("test", result.Username);
    }

    // -------------------------------------------------------------------------
    // TEST 2: GetByIdAsync retrieves a saved user
    // -------------------------------------------------------------------------
    // TODO: Implement
    //
    [Fact]
    public async Task GetByIdAsync_ExistingUser_ReturnsUser()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var user = new User { Username = "find_me", Email = "f@m.com", PasswordHash = "hash" };
        await repo.AddAsync(user, CancellationToken.None);

        var found = await repo.GetByIdAsync(user.Id);

        Assert.NotNull(found);
        Assert.Equal("find_me", found!.Username);
    }

    // -------------------------------------------------------------------------
    // TEST 3: GetByIdAsync returns null for missing user
    // -------------------------------------------------------------------------
    // TODO: Implement
    //
    [Fact]
    public async Task GetByIdAsync_NonExistentUser_ReturnsNull()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var found = await repo.GetByIdAsync(999);

        Assert.Null(found);
    }

    // -------------------------------------------------------------------------
    // TEST 4: UsernameExistsAsync returns true for existing username
    // -------------------------------------------------------------------------
    // TODO: Implement
    //
    [Fact]
    public async Task UsernameExistsAsync_ExistingUsername_ReturnsTrue()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        await repo.AddAsync(new User { Username = "exists", Email = "e@e.com", PasswordHash = "h" }, CancellationToken.None);

        Assert.True(await repo.UsernameExistsAsync("exists", CancellationToken.None));
        Assert.False(await repo.UsernameExistsAsync("nope", CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // TEST 5: GetByIdsAsync batch-fetches correctly
    // -------------------------------------------------------------------------
    // TODO: Implement
    //
    [Fact]
    public async Task GetByIdsAsync_ReturnsMatchingUsers()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var u1 = await repo.AddAsync(new User { Username = "a", Email = "a@a.com", PasswordHash = "h" }, CancellationToken.None);
        var u2 = await repo.AddAsync(new User { Username = "b", Email = "b@b.com", PasswordHash = "h" }, CancellationToken.None);
        await repo.AddAsync(new User { Username = "c", Email = "c@c.com", PasswordHash = "h" }, CancellationToken.None);

        var result = await repo.GetByIdsAsync([u1.Id, u2.Id], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(u1.Id, result.Keys);
        Assert.Contains(u2.Id, result.Keys);
    }
}
