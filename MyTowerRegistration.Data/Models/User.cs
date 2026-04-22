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
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = null!; // null-forgiving since it's required

    [Required]
    [MaxLength(200)]
    [EmailAddress]
    public string Email { get; set; } = null!;

    // Stores a hash of the password — never the plaintext value.
    // This demo uses SHA-256; production should use BCrypt or Argon2 (salted, slow).
    [Required]
    [MaxLength(128)]
    public string PasswordHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    // EF Core requires a parameterless constructor to materialize entities from query results.
    public User()
    {
        CreatedAt = DateTime.UtcNow;
    }
}
