// =============================================================================
// IMPLEMENT FIRST — This is the foundation everything else depends on.
//
// This is an EF Core *entity class* (not a record). Entities are mutable
// because EF Core's change tracker needs to set properties during
// materialization from the database.
//
// Compare to TypeScript:
//   interface User { id: number; username: string; email: string; ... }
// but here EF Core uses the class definition to *generate* the DB schema too.
// =============================================================================

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyTowerRegistration.Data.Models;

/// <summary>
/// Represents a registered user in the MyTower system.
/// EF Core maps this class to the "Users" table in PostgreSQL.
/// </summary>
[Table("Users")]
public class User
{
    // TODO 1: Add primary key property
    //   - Name it "Id", type int
    //   - Decorate with [Key] and [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    //   - In EF Core, [Key] marks the PK (like @PrimaryGeneratedColumn() in TypeORM)
    //   - DatabaseGeneratedOption.Identity means PostgreSQL auto-increments it
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    // TODO 2: Add Username property
    //   - Type: string (non-nullable since we have <Nullable>enable</Nullable>)
    //   - Decorate with [Required] and [MaxLength(50)]
    //   - These data annotations drive both validation AND the DB column constraints
    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = null!; // null-forgiving since it's required

    // TODO 3: Add Email property
    //   - Type: string, [Required], [MaxLength(200)]
    //   - Consider: [EmailAddress] attribute for built-in format validation
    [Required]
    [MaxLength(200)]
    [EmailAddress]
    public string Email { get; set; } = null!;

    // TODO 4: Add PasswordHash property
    //   - Type: string, [Required]
    //   - IMPORTANT: Never store plaintext passwords! We'll hash before saving.
    //   - For this demo we'll use a simple hash; production would use BCrypt/Argon2
    [Required]
    [MaxLength(128)]
    public string PasswordHash { get; set; } = null!;

    // TODO 5: Add CreatedAt property
    //   - Type: DateTime
    //   - Default to DateTime.UtcNow in the constructor or via EF Core HasDefaultValueSql
    //   - Always use UTC in backends (same advice in any language)

    public DateTime CreatedAt { get; set; }

    // TODO 6: Add a parameterless constructor
    //   - EF Core needs this to materialize entities from the database
    //   - You can set default values here (e.g., CreatedAt = DateTime.UtcNow)
    //   - In TypeScript you'd do this in the constructor or with default values
    public User()
    {
        CreatedAt = DateTime.UtcNow;
    }
}
