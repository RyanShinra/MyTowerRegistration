// =============================================================================
// IMPLEMENT FOURTH — Concrete implementation of the repository interface.
//
// This is where EF Core actually gets used. Each method translates your
// intent into a LINQ query, which EF Core converts to SQL for PostgreSQL.
//
// Key EF Core concepts you'll use here:
//   - _context.Users          → DbSet (the table)
//   - .FindAsync(id)          → SELECT ... WHERE Id = @id (uses PK index)
//   - .ToListAsync()          → SELECT * FROM "Users"
//   - .AnyAsync(predicate)    → SELECT EXISTS(...)
//   - .AddAsync(entity)       → Stages an INSERT (doesn't hit DB yet)
//   - .SaveChangesAsync()     → Flushes all staged changes to the DB in one transaction
//
// Compare to Prisma:
//   _context.Users.FindAsync(id)  ≈  prisma.user.findUnique({ where: { id } })
//   _context.SaveChangesAsync()   ≈  (implicit in Prisma, explicit in EF Core)
// =============================================================================

using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/>.
/// Injected as a scoped service (one instance per HTTP request).
/// </summary>
public class UserRepository : IUserRepository
{
    // TODO 1: Add a private readonly field: AppDbContext _context
    //   - Injected via constructor (dependency injection)
    //   - "Scoped" lifetime means one DbContext per request — important because
    //     DbContext is NOT thread-safe
    private readonly AppDbContext _context;

    // TODO 2: Add constructor that accepts AppDbContext and stores it in _context
    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

    // TODO 3: Implement GetByIdAsync(int id)
    //   - Use: await _context.Users.FindAsync(id)
    //   - FindAsync checks the local cache first, then hits the DB if needed
    public async Task<User?> GetByIdAsync(int id, CancellationToken ct)
    {
        // GOTCHA: FindAsync([id], ct) — the square brackets are required, not style.
        //
        // FindAsync has two overloads:
        //   (a) FindAsync(params object?[]  keyValues)          ← the trap
        //   (b) FindAsync(object?[] keyValues, CancellationToken ct)  ← what we want
        //
        // Writing FindAsync(id, ct) looks correct but silently picks overload (a):
        // the compiler can't match an int to object?[] for overload (b)'s first
        // parameter, so it falls back to params and boxes both id AND ct into the
        // key-values array. EF Core then searches for an entity whose primary key
        // equals [id, ct] — finds nothing — and returns null with no error.
        //
        // The fix is the collection expression [id], which produces an object?[]
        // and satisfies overload (b)'s first parameter, letting ct bind correctly.
        // Alternatively: FirstOrDefaultAsync(u => u.Id == id, ct) sidesteps this
        // entirely and is what most experienced EF Core devs reach for instead.
        return await _context.Users.FindAsync([id], ct);
    }

    // TODO 4: Implement GetAllAsync()
    //   - Use: await _context.Users.ToListAsync()
    //   - For production, add pagination! This is fine for a demo.
    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct)
    {
        return await _context.Users.ToListAsync(ct);
    }

    // TODO 5: Implement GetByIdsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    //   - Use: await _context.Users
    //              .Where(u => ids.Contains(u.Id))
    //              .ToDictionaryAsync(u => u.Id, ct)
    //   - The .Contains() translates to SQL: WHERE "Id" IN (1, 2, 3, ...)
    //   - This is the batch query that makes DataLoader efficient
    public async Task<UserByIdDictionary> GetByIdsAsync(IReadOnlyList<int> searchIds, CancellationToken ct)
    {
        return await _context.Users
            .Where(user => searchIds.Contains(user.Id))
            .ToDictionaryAsync(keySelector: (User user) => {
                // selects the field from `user` to be the key in the Dictionary
                return user.Id;
            }, ct);
    }

    // TODO 6: Implement AddAsync(User user)
    //   - Use: _context.Users.Add(user);    ← stages the INSERT
    //          await _context.SaveChangesAsync(); ← executes it
    //          return user;                  ← Id is now populated by PostgreSQL
    //   - Note: .Add() is sync (just stages), SaveChangesAsync() is the async part
    public async Task<User> AddAsync(User user, CancellationToken ct)
    {
        _context.Users.Add(user);
        await _context.SaveChangesAsync(ct);
        return user;
    }

    // TODO 7: Implement UsernameExistsAsync(string username)
    //   - Use: await _context.Users.AnyAsync(u => u.Username == username)
    //   - AnyAsync is more efficient than CountAsync — it short-circuits
    public async Task<bool> UsernameExistsAsync(string searchUsername, CancellationToken ct)
    {
        return await _context.Users.AnyAsync(user => user.Username == searchUsername, ct);
    }

    // TODO 8: Implement EmailExistsAsync(string email)
    //   - Same pattern, check u.Email == email
    public async Task<bool> EmailExistsAsync(string searchEmail, CancellationToken ct)
    {
        return await _context.Users.AnyAsync(user => user.Email == searchEmail, ct);
    }

    public async Task<User?> DeleteAsync(int id, CancellationToken ct)
    {
        User? deleteTgt = await _context.Users.FindAsync([id], ct); // [id] not id — see GetByIdAsync for the full explanation
        if (deleteTgt is null) return null;

        _context.Users.Remove(deleteTgt);
        await _context.SaveChangesAsync(ct);
        return deleteTgt;
    }
}
