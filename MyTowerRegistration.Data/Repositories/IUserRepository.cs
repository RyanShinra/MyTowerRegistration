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
    // TODO 1: Task<User?> GetByIdAsync(int id)
    //   - Returns null if not found (that's what the ? means)
    //   - Compare to: async getById(id: number): Promise<User | null>
    Task<User?> GetByIdAsync(int id, CancellationToken ct);

    // TODO 2: Task<IReadOnlyList<User>> GetAllAsync()
    //   - Returns all users; IReadOnlyList signals "you get a snapshot, don't modify it"
    //   - Compare to: async getAll(): Promise<readonly User[]>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct);

    // TODO 3: Task<IDictionary<int, User>> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    //   - Batch-fetch by IDs — this powers the DataLoader (N+1 prevention)
    //   - Returns a dictionary so the DataLoader can map each ID to its User
    //   - CancellationToken lets ASP.NET Core cancel if the client disconnects
    //   - Compare to: async getByIds(ids: number[]): Promise<Map<number, User>>
    Task<UserByIdDictionary> GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct);

    // TODO 4: Task<User> AddAsync(User user)
    //   - Inserts a new user and returns it (with the DB-generated Id populated)
    Task<User> AddAsync(User user, CancellationToken ct);

    // TODO 5: Task<bool> UsernameExistsAsync(string username)
    //   - Check for uniqueness before inserting (application-level validation)
    //   - The DB unique index is a safety net; this gives better error messages
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct);

    // TODO 6: Task<bool> EmailExistsAsync(string email)
    //   - Same pattern as UsernameExistsAsync
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);

    // Added during Admin Gui tutorial
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}
