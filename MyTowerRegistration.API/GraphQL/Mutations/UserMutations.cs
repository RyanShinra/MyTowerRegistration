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

using UEC = MyTowerRegistration.API.GraphQL.Types.UserErrorCode;
using RPayload = MyTowerRegistration.API.GraphQL.Types.RegisterUserPayload;
namespace MyTowerRegistration.API.GraphQL.Mutations;

/// <summary>
/// GraphQL mutation resolvers for User operations.
/// Each public method becomes a field on the Mutation type.
/// </summary>
//[MutationType]
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

    public async Task<RPayload> RegisterUser(
        RegisterUserInput input, 
        [Service] IUserRepository userRepository,
        CancellationToken ct)
    {
        RPayload ErrorPayload(string message, UEC code)
            => new(null, [new UserError(message, code)]);

        bool TryCreateEmail() => System.Net.Mail.MailAddress.TryCreate(input.Email, out _);

        RPayload? ValidateEmail() => !TryCreateEmail()
            ? ErrorPayload("Invalid e-mail address", UEC.InvalidEmail) 
            : null;

        async Task<RPayload?> ValidateAvailableUsername() => await userRepository.UsernameExistsAsync(input.Username, ct)
            ? ErrorPayload("Username already in use", UEC.UsernameTaken)
            : null;

        async Task<RPayload?> ValidateAvailableEmail() => await userRepository.EmailExistsAsync(input.Email, ct)
            ? ErrorPayload("Email already in use", UEC.EmailTaken)
            : null;

        if (ValidateEmail() is { } badEmailError) return badEmailError;
        if (await ValidateAvailableUsername() is { } takenUsernameError) return takenUsernameError;
        if (await ValidateAvailableEmail() is { } takenEmailError) return takenEmailError;

        User newUser = new() {
            Username = input.Username,
            Email = input.Email,
            PasswordHash = HashPassword(input.Password),
            CreatedAt = DateTime.UtcNow,
        };

        User createdUser = await userRepository.AddAsync(newUser, ct);
        return new RPayload(createdUser, null);
    }

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
    private static string HashPassword(string password)
    {
        byte[] pwBytes = Encoding.UTF8.GetBytes(password);
        var hashBytes = SHA256.HashData(pwBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
