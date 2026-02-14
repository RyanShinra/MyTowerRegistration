// =============================================================================
// IMPLEMENT EIGHTH — The payload (return type) for the registerUser mutation.
//
// Hot Chocolate's mutation convention: mutations return a "payload" type
// that contains BOTH the result AND any errors. This gives clients a
// predictable structure:
//
//   type RegisterUserPayload {
//     user: User          ← null if registration failed
//     errors: [UserError!] ← null/empty if registration succeeded
//   }
//
// This is better than throwing exceptions because:
//   1. Clients can handle errors in their normal data flow
//   2. Partial success is possible (not applicable here, but in batch ops)
//   3. The schema documents all possible errors
//
// Compare to Apollo Server:
//   In TS you might return { user?: User; errors?: UserError[] }
//   Same idea, but Hot Chocolate enforces the convention structurally.
// =============================================================================

using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.API.GraphQL.Types;

// TODO 1: Create a C# record named RegisterUserPayload with two properties:
//   - User? User          (nullable — null when registration fails)
//   - IReadOnlyList<UserError>? Errors  (nullable — null when no errors)
//
//   Syntax: public record RegisterUserPayload(User? User, IReadOnlyList<UserError>? Errors);
    public record RegisterUserPayload(User? User, IReadOnlyList<UserError>? Errors);
//   You'll construct it in the mutation like:
//     Success: new RegisterUserPayload(user, null)
//     Failure: new RegisterUserPayload(null, [new UserError("msg", "CODE")])
//
//   NOTE: The User property will be projected through UserType, which hides
//   the PasswordHash field. So even though the C# record holds the full User
//   entity, the GraphQL response won't include the hash.
