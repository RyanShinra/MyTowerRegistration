// =============================================================================
// IMPLEMENT ELEVENTH — The write side of your GraphQL API.
//
// Mutations follow the same pattern as queries, but with [MutationType].
// The interesting part here is the validation + error handling pattern.
//
// Compare to Apollo Server:
//   const resolvers = {
//     Mutation: {
//       registerUser: async (_, { input }) => {
//         if (await usernameExists(input.username))
//           return { user: null, errors: [{ message: '...', code: 'USERNAME_TAKEN' }] };
//         const user = await createUser(input);
//         return { user, errors: null };
//       }
//     }
//   };
//
// Same pattern! The main difference is C#'s type safety and Hot Chocolate's
// DI-based parameter injection.
// =============================================================================

using MyTowerRegistration.API.GraphQL.Types;
using MyTowerRegistration.Data.Models;
using MyTowerRegistration.Data.Repositories;
using System.Security.Cryptography;
using System.Text;

namespace MyTowerRegistration.API.GraphQL.Mutations;

/// <summary>
/// GraphQL mutation resolvers for User operations.
/// Each public method becomes a field on the Mutation type.
/// </summary>
[MutationType]
public class UserMutations
{
    // TODO 1: Implement RegisterUser
    
    //   public async Task<RegisterUserPayload> RegisterUser(
    //       RegisterUserInput input,
    //       [Service] IUserRepository repository)
    //   {
    //       // Step A: Validate email format
    //       //   - Use a simple check or System.Net.Mail.MailAddress.TryCreate()
    //       //   - If invalid, return: new RegisterUserPayload(null, [new UserError(...)])
    //       //   - Error code: "INVALID_EMAIL"
    //
    //       // Step B: Check if username is taken
    //       //   - await repository.UsernameExistsAsync(input.Username)
    //       //   - If taken, return payload with error code: "USERNAME_TAKEN"
    //
    //       // Step C: Check if email is taken
    //       //   - await repository.EmailExistsAsync(input.Email)
    //       //   - If taken, return payload with error code: "EMAIL_TAKEN"
    //
    //       // Step D: Hash the password
    //       //   - For this demo, use SHA256 (see helper below)
    //       //   - PRODUCTION NOTE: use BCrypt or Argon2 instead!
    //       //   - var hash = HashPassword(input.Password);
    //
    //       // Step E: Create the User entity
    //       //   var user = new User
    //       //   {
    //       //       Username = input.Username,
    //       //       Email = input.Email,
    //       //       PasswordHash = hash,
    //       //       CreatedAt = DateTime.UtcNow,
    //       //   };
    //
    //       // Step F: Save via repository
    //       //   var created = await repository.AddAsync(user);
    //
    //       // Step G: Return success payload
    //       //   return new RegisterUserPayload(created, null);
    //   }

    // TODO 2: Add a private static helper for password hashing
    //
    //   private static string HashPassword(string password)
    //   {
    //       // SHA256 for demo only — NOT production-safe (no salt, too fast)
    //       var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    //       return Convert.ToHexString(bytes).ToLowerInvariant();
    //   }
    //
    //   Why static? It doesn't access instance state — pure function.
    //   Why private? Internal implementation detail, not part of the API.
    //   Compare to TS: const hashPassword = (pw: string): string => { ... }
}
