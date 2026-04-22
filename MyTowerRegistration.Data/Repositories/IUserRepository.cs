// =============================================================================
// IMPLEMENT THIRD — Define the contract before the implementation.
//
// The repository pattern puts a clean interface between your business logic
// (GraphQL resolvers) and data access (EF Core). This is the same idea as
// creating a data access layer in Node.js that your resolvers call instead
// of hitting Prisma/Knex directly.
//
// Why bother? Two reasons:
//   1. Testability — you can mock IUserRepository in unit tests
//   2. Flexibility — swap EF Core for Dapper or raw SQL without touching resolvers
// =============================================================================

using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.Data.Repositories;

/// <summary>
/// Defines the data access contract for User operations.
/// All methods are async — EF Core queries hit the network (PostgreSQL),
/// so they should never block the thread.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct);

    // Batch-fetch by IDs — powers the DataLoader to avoid N+1 queries.
    // Returns a dictionary so each ID maps directly to its User.
    Task<UserByIdDictionary> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct);

    // Returns the inserted user with the DB-generated Id populated.
    Task<User> AddAsync(User user, CancellationToken ct);

    // Application-level uniqueness checks — the DB unique index is a safety net,
    // but these give callers a structured error code rather than a DB exception.
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);

    // Returns the deleted User if found and deleted, or null if no such user existed.
    // Returning User? (rather than bool) lets the caller include the deleted entity
    // in the response payload without a separate GetByIdAsync call — which would
    // introduce a TOCTOU (= Time Of Check, Time Of Use) race if another request deleted the same row in between.
    Task<User?> DeleteAsync(int id, CancellationToken ct);
}
