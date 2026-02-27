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

public record RegisterUserPayload(User? User, IReadOnlyList<UserError>? Errors);
