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
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        
    }

    public DbSet<User> Users { get; set; } = null!;

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
