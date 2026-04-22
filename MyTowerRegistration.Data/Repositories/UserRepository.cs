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
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

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

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct)
    {
        return await _context.Users.ToListAsync(ct);
    }

    public async Task<UserByIdDictionary> GetByIdsAsync(IReadOnlyList<int> searchIds, CancellationToken ct)
    {
        return await _context.Users
            .Where(user => searchIds.Contains(user.Id))
            .ToDictionaryAsync(keySelector: (User user) => {
                // selects the field from `user` to be the key in the Dictionary
                return user.Id;
            }, ct);
    }

    public async Task<User> AddAsync(User user, CancellationToken ct)
    {
        _context.Users.Add(user);
        await _context.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> UsernameExistsAsync(string searchUsername, CancellationToken ct)
    {
        return await _context.Users.AnyAsync(user => user.Username == searchUsername, ct);
    }

    public async Task<bool> EmailExistsAsync(string searchEmail, CancellationToken ct)
    {
        return await _context.Users.AnyAsync(user => user.Email == searchEmail, ct);
    }

    public async Task<User?> DeleteAsync(int id, CancellationToken ct)
    {
        User? deleteTgt = await _context.Users.FindAsync([id], ct); // [id] not id — see GetByIdAsync for the full explanation
        if (deleteTgt is null) return null;

        _context.Users.Remove(deleteTgt);

        try
        {
            await _context.SaveChangesAsync(ct);
            return deleteTgt;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request deleted this row between our FindAsync and SaveChangesAsync.
            // EF expected 1 row affected; got 0. Detach the entity so the change tracker
            // doesn't hold stale state, then return null to match the "user not found" contract.
            _context.Entry(deleteTgt).State = EntityState.Detached;
            return null;
        }
    }
}
