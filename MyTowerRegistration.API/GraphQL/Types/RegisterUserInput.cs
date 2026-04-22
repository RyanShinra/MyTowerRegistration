// =============================================================================
// IMPLEMENT SEVENTH — The input type for the registerUser mutation.
//
// In GraphQL, "input types" are special types used only as mutation/query
// arguments. They're separate from regular "object types" in the schema.
//
// Hot Chocolate maps C# records to GraphQL input types when they're used
// as parameters in mutations. You just define the record.
//
// Compare to Apollo Server (TypeScript):
//   input RegisterUserInput {
//     username: String!
//     email: String!
//     password: String!
//   }
//   ...then you'd manually parse args.input in your resolver.
//   Here, Hot Chocolate deserializes the input JSON into this record for you.
//
// Why a record?
//   - Input types are immutable (you receive data, you don't modify it)
//   - Records give you structural equality and clean ToString() for free
//   - Positional records (constructor syntax) keep it concise
// =============================================================================

namespace MyTowerRegistration.API.GraphQL.Types;

public record RegisterUserInput(string Username, string Email, string Password);
