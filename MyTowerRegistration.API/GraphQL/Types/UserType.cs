// =============================================================================
// IMPLEMENT FIFTH — After the Data layer compiles.
//
// Hot Chocolate "Type" classes control how your C# entities appear in the
// GraphQL schema. This is the "code-first" approach — you write C# and
// Hot Chocolate generates the SDL (schema definition language) from it.
//
// Compare to Apollo Server (TypeScript):
//   - In Apollo, you write SDL strings and then resolvers separately
//   - In Hot Chocolate code-first, the C# class IS the schema definition
//   - ObjectType<T> is like a type definition + field resolvers combined
//
// Why use ObjectType<User> instead of just exposing User directly?
//   - Control which fields appear in the schema (hide PasswordHash!)
//   - Add computed/virtual fields that don't exist on the entity
//   - Customize field names, descriptions, and nullability
// =============================================================================

using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.API.GraphQL.Types;

/// <summary>
/// GraphQL type definition for the User entity.
/// Maps to: type User { id: Int!, username: String!, email: String!, createdAt: DateTime! }
/// </summary>
public class UserType : ObjectType<User>
{
    // TODO 1: Override Configure(IObjectTypeDescriptor<User> descriptor)
    //   - Set the GraphQL type name: descriptor.Name("User")
    //   - (Optional) Add a description: descriptor.Description("A registered user")

    // TODO 2: Expose the fields you want in the schema:
    //   descriptor.Field(u => u.Id).Type<NonNullType<IntType>>();
    //   descriptor.Field(u => u.Username).Type<NonNullType<StringType>>();
    //   descriptor.Field(u => u.Email).Type<NonNullType<StringType>>();
    //   descriptor.Field(u => u.CreatedAt).Type<NonNullType<DateTimeType>>();
    //
    //   NOTE: Hot Chocolate can also infer types from C# types. The explicit
    //   .Type<>() calls are optional but make the schema self-documenting.

    // TODO 3: IMPORTANT — Ignore the PasswordHash field!
    //   descriptor.Field(u => u.PasswordHash).Ignore();
    //   This prevents PasswordHash from ever appearing in the GraphQL schema.
    //   Security rule: never expose sensitive data through your API.
}
