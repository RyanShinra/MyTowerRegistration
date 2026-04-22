// =============================================================================
// IMPLEMENT SIXTH — Small type, quick win.
//
// This represents a structured error in GraphQL responses. Instead of
// throwing exceptions (which become top-level "errors" in the GraphQL
// response), we return errors as DATA. This is a Hot Chocolate best practice
// called the "mutation convention" or "error as data" pattern.
//
// Compare to Apollo Server:
//   - Apollo often uses union types or custom error types in the schema
//   - Hot Chocolate provides this pattern out of the box with payload types
//
// The GraphQL schema will show:
//   type CreateUserError { message: String!, code: CreateUserErrorCode! }
//   enum CreateUserErrorCode { INVALID_EMAIL, USERNAME_TAKEN, EMAIL_TAKEN, ... }
// =============================================================================

namespace MyTowerRegistration.API.GraphQL.Types;

public enum CreateUserErrorCode
{
    InvalidEmail,
    InvalidUsername,
    UsernameTaken,
    EmailTaken,
    InvalidPassword,
    UserNotFound
}
