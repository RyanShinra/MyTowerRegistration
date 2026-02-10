// =============================================================================
// IMPLEMENT SECOND — After the User entity exists.
//
// DbContext is EF Core's "unit of work" — it's the bridge between your C#
// classes and the database. Think of it like a Prisma client or a Knex
// instance, but with built-in change tracking.
//
// In the Node.js world, you'd configure your ORM connection in one place
// (like prisma.schema or knex config). DbContext serves that same role.
// =============================================================================

using Microsoft.EntityFrameworkCore;
using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.Data;

/// <summary>
/// Entity Framework Core database context for the MyTower application.
/// Configured to use PostgreSQL via Npgsql.
/// </summary>
public class AppDbContext : DbContext
{
    // TODO 1: Add constructor that accepts DbContextOptions<AppDbContext>
    //   - Pass options to the base class: base(options)
    //   - This is how ASP.NET Core's DI injects the connection string
    //   - Similar pattern to passing config to a Prisma/Knex client
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        
    }

    // TODO 2: Add DbSet<User> property named "Users"
    //   - DbSet<T> is like a "table reference" — it's your query entry point
    //   - Usage: await _context.Users.FindAsync(id)
    //   - Compare to Prisma: prisma.user.findUnique(...)
    DbSet<User> Users = null!;

    // TODO 3: Override OnModelCreating(ModelBuilder modelBuilder)
    //   - This is the "Fluent API" — an alternative to data annotations for
    //     configuring the schema. Use it for things annotations can't express.
    //   - Add a unique index on Username:
    //       modelBuilder.Entity<User>()
    //           .HasIndex(u => u.Username)
    //           .IsUnique();
    //   - Add a unique index on Email (same pattern)
    //   - These create UNIQUE constraints in PostgreSQL, enforced at the DB level
    //   - Compare to Prisma: @@unique([username]) in schema.prisma
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex((User u) => u.Username)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex((User u) => u.Email)
            .IsUnique();
    }
}
