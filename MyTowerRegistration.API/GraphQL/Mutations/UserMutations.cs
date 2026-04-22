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

using UEC = MyTowerRegistration.API.GraphQL.Types.CreateUserErrorCode;
using RPayload = MyTowerRegistration.API.GraphQL.Types.RegisterUserPayload;
namespace MyTowerRegistration.API.GraphQL.Mutations;

/// <summary>
/// GraphQL mutation resolvers for User operations.
/// Each public method becomes a field on the Mutation type.
/// </summary>

public class UserMutations
{
    public async Task<RPayload> RegisterUser(
        RegisterUserInput input, 
        [Service] IUserRepository userRepository,
        CancellationToken ct)
    {
        RPayload ErrorPayload(string message, UEC code)
            => new(null, [new CreateUserError(message, code)]);

        bool TryCreateEmail() => System.Net.Mail.MailAddress.TryCreate(input.Email, out _);

        RPayload? ValidateUsername()
        {
            if (string.IsNullOrWhiteSpace(input.Username)) 
                return ErrorPayload("Invalid Empty Username", UEC.InvalidUsername);

            if (input.Username.Length < 3 || input.Username.Length > 20)
                return ErrorPayload("Username must be between 3 and 20 characters", UEC.InvalidUsername);

            return null;
        }

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
        if (ValidateUsername() is { } badUsernameError) return badUsernameError;
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

    // SHA-256 for demo only — NOT production-safe (no salt, too fast). Use BCrypt or Argon2.
    private static string HashPassword(string password)
    {
        byte[] pwBytes = Encoding.UTF8.GetBytes(password);
        var hashBytes = SHA256.HashData(pwBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task<DeleteUserPayload> DeleteUser(int id, [Service] IUserRepository userRepository, CancellationToken ct)
    {
        User? justDeleted = await userRepository.DeleteAsync(id, ct);
        if (justDeleted is null) {
            return new DeleteUserPayload(null, [new DeleteUserError("User Not Found", DeleteUserErrorCode.UserNotFound)]);
        }
        return new DeleteUserPayload(justDeleted, null);
    }
}
