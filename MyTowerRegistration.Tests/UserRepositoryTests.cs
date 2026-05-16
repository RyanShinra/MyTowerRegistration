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
        var options = new DbContextOptionsBuilder<AppDbContext>()
               .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
               .Options;
        return new AppDbContext(options);
    }

    // -------------------------------------------------------------------------
    // TEST 1: AddAsync persists and returns the user
    // -------------------------------------------------------------------------
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
    [Fact]
    public async Task GetByIdAsync_ExistingUser_ReturnsUser()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var user = new User { Username = "find_me", Email = "f@m.com", PasswordHash = "hash" };
        await repo.AddAsync(user, CancellationToken.None);

        var found = await repo.GetByIdAsync(user.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("find_me", found!.Username);
    }

    // -------------------------------------------------------------------------
    // TEST 3: GetByIdAsync returns null for missing user
    // -------------------------------------------------------------------------
    [Fact]
    public async Task GetByIdAsync_NonExistentUser_ReturnsNull()
    {
        using var context = CreateInMemoryContext();
        var repo = new UserRepository(context);

        var found = await repo.GetByIdAsync(999, CancellationToken.None);

        Assert.Null(found);
    }

    // -------------------------------------------------------------------------
    // TEST 4: UsernameExistsAsync returns true for existing username
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // TEST 6: DeleteAsync removes and returns the user
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DeleteAsync_ExistingUser_RemovesAndReturnsUser()
    {
        using var ctx = CreateInMemoryContext();
        var repo = new UserRepository(ctx);

        User added = await repo.AddAsync(
            new User { Username = "delete_me", Email = "d@d.com", PasswordHash = "h" },
            CancellationToken.None);

        User? result = await repo.DeleteAsync(added.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Same(added, result);  // repo returned the entity it operated on, not a fresh fetch
        Assert.Null(await repo.GetByIdAsync(added.Id, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // TEST 7: DeleteAsync returns null for a non-existent user
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DeleteAsync_NonExistentUser_ReturnsNull()
    {
        using var ctx = CreateInMemoryContext();
        var repo = new UserRepository(ctx);

        User? result = await repo.DeleteAsync(999, CancellationToken.None);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // TEST 8: DeleteAsync catches DbUpdateConcurrencyException, detaches the
    // entity from the change tracker, and returns null rather than throwing.
    //
    // Uses ThrowOnSaveContext (below) to simulate a concurrent delete that races
    // between FindAsync and SaveChangesAsync — not reproducible with InMemory
    // alone since it doesn't enforce row versioning.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DeleteAsync_ConcurrentDelete_ReturnsNullAndDetachesEntity()
    {
        const string dbName = "delete_concurrency_test";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName).Options;

        // Seed using a short-lived context so it's flushed before the throwing context opens
        User seeded;
        using (var freshCtx = new AppDbContext(options))
        {
            seeded = await new UserRepository(freshCtx).AddAsync(
                new User { Username = "race", Email = "r@r.com", PasswordHash = "h" },
                CancellationToken.None);
        }

        using var throwingCtx = new ThrowOnSaveContext(options);
        var throwingRepo = new UserRepository(throwingCtx);

        // If the exception escapes the catch block, Record.ExceptionAsync captures it
        // so the test fails with a clear message rather than an unhandled exception crash
        Exception? escaped = await Record.ExceptionAsync(
            () => throwingRepo.DeleteAsync(seeded.Id, CancellationToken.None));

        Assert.Null(escaped);

        // The entity must be evicted from the change tracker so the context isn't
        // left holding a stale Deleted entry that could confuse subsequent operations
        Assert.Empty(throwingCtx.ChangeTracker.Entries());
    }

    // -------------------------------------------------------------------------
    // TEST 9: UpdateAsync updates only the fields that are non-null
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateAsync_ExistingUser_UpdatesOnlyNonNullFields()
    {
        using var ctx = CreateInMemoryContext();
        var repo = new UserRepository(ctx);

        User added = await repo.AddAsync(
            new User { Username = "original", Email = "original@test.com", PasswordHash = "oldhash" },
            CancellationToken.None);

        User? result = await repo.UpdateAsync(added.Id, "updated", null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("updated",              result!.Username);
        Assert.Equal("original@test.com",    result.Email);      // null arg — must not be overwritten
        Assert.Equal("oldhash",              result.PasswordHash); // null arg — must not be overwritten
    }

    // -------------------------------------------------------------------------
    // TEST 10: UpdateAsync returns null for a non-existent user
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateAsync_NonExistentUser_ReturnsNull()
    {
        using var ctx = CreateInMemoryContext();
        var repo = new UserRepository(ctx);

        User? result = await repo.UpdateAsync(999, "newname", null, null, CancellationToken.None);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // TEST 11: UpdateAsync with all-null fields returns the user unchanged.
    //
    // EF Core's change tracker sees no modified properties and SaveChangesAsync
    // issues no SQL UPDATE — the DB round-trip is the FindAsync only.
    // See the comment in UserRepository.UpdateAsync for the full explanation.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateAsync_AllNullFields_ReturnsUserUnchanged()
    {
        using var ctx = CreateInMemoryContext();
        var repo = new UserRepository(ctx);

        User added = await repo.AddAsync(
            new User { Username = "stays", Email = "stays@test.com", PasswordHash = "stayshash" },
            CancellationToken.None);

        User? result = await repo.UpdateAsync(added.Id, null, null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("stays",           result!.Username);
        Assert.Equal("stays@test.com",  result.Email);
        Assert.Equal("stayshash",       result.PasswordHash);
    }

    // -------------------------------------------------------------------------
    // TEST 12: UpdateAsync catches DbUpdateConcurrencyException, detaches the
    // entity from the change tracker, and returns null rather than throwing.
    //
    // Extra risk vs DeleteAsync: UpdateAsync mutates the entity in memory before
    // SaveChangesAsync. Without Detach, those stale mutations would remain on the
    // context and could be accidentally re-saved by a later SaveChangesAsync call.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateAsync_ConcurrentDelete_ReturnsNullAndDetachesEntity()
    {
        const string dbName = "update_concurrency_test";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName).Options;

        User seeded;
        using (var freshCtx = new AppDbContext(options))
        {
            seeded = await new UserRepository(freshCtx).AddAsync(
                new User { Username = "race", Email = "r@r.com", PasswordHash = "h" },
                CancellationToken.None);
        }

        using var throwingCtx = new ThrowOnSaveContext(options);
        var throwingRepo = new UserRepository(throwingCtx);

        Exception? escaped = await Record.ExceptionAsync(
            () => throwingRepo.UpdateAsync(seeded.Id, "newname", null, null, CancellationToken.None));

        Assert.Null(escaped);

        // Entity must be evicted — it was mutated in memory before save failed,
        // so leaving it tracked would risk those changes leaking into a future save
        Assert.Empty(throwingCtx.ChangeTracker.Entries());
    }

    // -------------------------------------------------------------------------
    // Helper: subclass that overrides SaveChangesAsync to throw
    // DbUpdateConcurrencyException, simulating a concurrent row deletion between
    // FindAsync and SaveChangesAsync without needing a real database.
    // -------------------------------------------------------------------------
    private class ThrowOnSaveContext : AppDbContext
    {
        public ThrowOnSaveContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
            => throw new DbUpdateConcurrencyException("simulated concurrent modification");
    }
}
