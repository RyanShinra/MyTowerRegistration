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
//   type UserError { message: String!, code: UserErrorCode! }
//   enum UserErrorCode { INVALID_EMAIL, USERNAME_TAKEN, EMAIL_TAKEN, ... }
// =============================================================================

namespace MyTowerRegistration.API.GraphQL.Types;

// TODO 1: Create a C# record named UserError with two properties:
//   - string Message
//   - UserErrorCode Code   ← enum, not string; HC maps this to a GraphQL enum type
//
//   Use a record (not a class) because errors are immutable value objects.
//   Records in C# are like TypeScript's `readonly` types + structural equality.
//   Syntax: public record UserError(string Message, string Code);
//
//   Hot Chocolate auto-maps records to GraphQL types — no ObjectType<T>
//   descriptor needed for simple DTOs like this. The record properties
//   become fields automatically.

public enum UserErrorCode
{
    InvalidEmail,
    InvalidUsername,
    UsernameTaken,
    EmailTaken,
    InvalidPassword,
    UserNotFound
}

public record UserError(string Message, UserErrorCode Code);
