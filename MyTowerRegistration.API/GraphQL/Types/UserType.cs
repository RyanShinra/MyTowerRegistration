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
  
    protected override void Configure(IObjectTypeDescriptor<User> descriptor)
    {
        base.Configure(descriptor);
        descriptor.Name("User");
        descriptor.Description("A registered user");
        descriptor.Field((User user) => user.Id).Type<NonNullType<IntType>>();
        descriptor.Field((User user) => user.Username).Type<NonNullType<StringType>>();
        descriptor.Field((User user) => user.Email).Type<NonNullType<StringType>>();  // Fixed: was IntType
        descriptor.Field((User user) => user.CreatedAt).Type<NonNullType<DateTimeType>>();

        // Security: never expose password hash
        descriptor.Field(u => u.PasswordHash).Ignore();
    }
}
