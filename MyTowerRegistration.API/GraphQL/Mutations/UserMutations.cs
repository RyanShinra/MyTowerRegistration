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
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RPayload = MyTowerRegistration.API.GraphQL.Types.RegisterUserPayload;
using UEC = MyTowerRegistration.API.GraphQL.Types.CreateUserErrorCode;
namespace MyTowerRegistration.API.GraphQL.Mutations;

/// <summary>
/// GraphQL mutation resolvers for User operations.
/// Each public method becomes a field on the Mutation type.
/// </summary>

public class UserMutations
{
    private enum UserFieldValidationError
    {
        InvalidUsername,
        InvalidEmail,
        InvalidPassword,
        UsernameTaken,
        EmailTaken
    }

    private record UserValidationError
    (
        string Message,
        UserFieldValidationError ErrorCode
    );

    private static async Task<UserValidationError?> ValidateUserFields(
        string? username, string? email, string? password, IUserRepository userRepository, CancellationToken ct)
    {
        if (username is not null) {
            if (string.IsNullOrWhiteSpace(username))
                return new UserValidationError("Invalid Empty Username", UserFieldValidationError.InvalidUsername);

            if (username.Length < 3 || username.Length > 20)
                return new UserValidationError("Username must be between 3 and 20 characters", UserFieldValidationError.InvalidUsername);

            if (await userRepository.UsernameExistsAsync(username, ct))
                return new UserValidationError("Username already in use", UserFieldValidationError.UsernameTaken);
        }

        if (email is not null) {
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
                return new UserValidationError("Invalid e-mail address", UserFieldValidationError.InvalidEmail);

            if (await userRepository.EmailExistsAsync(email, ct))
                return new UserValidationError("Email already in use", UserFieldValidationError.EmailTaken);
        }

        if (password is not null) {
            // TODO: Consider password validation rules
        }

        return null;
    }
    public async Task<RPayload> RegisterUser(
        RegisterUserInput input,
        [Service] IUserRepository userRepository,
        CancellationToken ct)
    {
        static RPayload ErrorPayload(string message, UEC code)
            => new(null, [new CreateUserError(message, code)]);

        if (await ValidateUserFields(input.Username, input.Email, input.Password, userRepository, ct) is { } failedField) {
            return failedField.ErrorCode switch {
                UserFieldValidationError.InvalidUsername => ErrorPayload(failedField.Message, UEC.InvalidUsername),
                UserFieldValidationError.InvalidEmail => ErrorPayload(failedField.Message, UEC.InvalidEmail),
                UserFieldValidationError.InvalidPassword => ErrorPayload(failedField.Message, UEC.InvalidPassword),
                UserFieldValidationError.UsernameTaken => ErrorPayload(failedField.Message, UEC.UsernameTaken),
                UserFieldValidationError.EmailTaken => ErrorPayload(failedField.Message, UEC.EmailTaken),
                _ => throw new UnreachableException($"Unknown UserFieldValidationError: {failedField.ErrorCode}")
            };

        }

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

    public async Task<UpdateUserPayload> UpdateUser(
        UpdateUserInput input,
        [Service] IUserRepository userRepository,
        CancellationToken ct)
    {
        static UpdateUserPayload ErrorPayload(string message, UpdateUserErrorCode code)
            => new(null, [new UpdateUserError(message, code)]);

        if (await ValidateUserFields(input.Username, input.Email, input.Password, userRepository, ct) is { } failedField) {
            return failedField.ErrorCode switch {
                UserFieldValidationError.InvalidPassword => ErrorPayload(failedField.Message, UpdateUserErrorCode.InvalidPassword),
                UserFieldValidationError.InvalidEmail => ErrorPayload(failedField.Message, UpdateUserErrorCode.InvalidEmail),
                UserFieldValidationError.InvalidUsername => ErrorPayload(failedField.Message, UpdateUserErrorCode.InvalidUsername),
                UserFieldValidationError.UsernameTaken => ErrorPayload(failedField.Message, UpdateUserErrorCode.UsernameTaken),
                UserFieldValidationError.EmailTaken => ErrorPayload(failedField.Message, UpdateUserErrorCode.EmailTaken),
                _ => throw new UnreachableException($"Unknown UserFieldValidationError: {failedField.ErrorCode}")
            };
        }
        User? updatedUser = await userRepository.UpdateAsync(input.Id,
            input.Username, input.Email, input.Password is not null ? HashPassword(input.Password) : null, ct);

        if (updatedUser is null)
            return ErrorPayload("User not found", UpdateUserErrorCode.UserNotFound);

        return new UpdateUserPayload(updatedUser, null);   
    }
}
