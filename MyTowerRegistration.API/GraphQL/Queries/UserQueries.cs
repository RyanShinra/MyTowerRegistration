// =============================================================================
// IMPLEMENT TENTH — The read side of your GraphQL API.
//
// In Hot Chocolate, "Query" classes are just regular C# classes whose public
// methods become query fields in the schema. The [QueryType] attribute (HC 15)
// tells Hot Chocolate "register all public methods as Query fields."
//
// Compare to Apollo Server:
//   const resolvers = {
//     Query: {
//       user: (_, { id }) => getUserById(id),
//       users: () => getAllUsers(),
//     }
//   };
//
// In Hot Chocolate, method parameters are resolved via dependency injection
// or mapped from GraphQL arguments automatically. So if your method takes
// an `int id`, Hot Chocolate exposes `user(id: Int!)` in the schema.
//
// The [QueryType] attribute (introduced in HC 13+) is the modern way.
// Older tutorials may show descriptor.Field(...) registration — skip that.
// =============================================================================

using MyTowerRegistration.API.GraphQL.DataLoaders;
using MyTowerRegistration.Data.Models;
using MyTowerRegistration.Data.Repositories;

namespace MyTowerRegistration.API.GraphQL.Queries;

/// <summary>
/// GraphQL query resolvers for User operations.
/// Each public method becomes a field on the Query type.
/// </summary>
[QueryType]
public class UserQueries
{
    // TODO 1: Implement GetUser — single user by ID via DataLoader
    //
    //   public async Task<User?> GetUser(int id, UserBatchDataLoader loader)
    //   {
    //       return await loader.LoadAsync(id);
    //   }
    //
    //   Key points:
    //   - Hot Chocolate injects the DataLoader automatically (it's a DI service)
    //   - loader.LoadAsync(id) doesn't fire immediately — it batches
    //   - Returns null if not found (matches User? return type → nullable in schema)
    //   - Method name "GetUser" → schema field name "user" (HC strips "Get" prefix)
    //
    //   Compare to Apollo: same as calling dataLoader.load(id) in a resolver

    // TODO 2: Implement GetUsers — list all users
    //
    //   public async Task<IReadOnlyList<User>> GetUsers(
    //       [Service] IUserRepository repository)
    //   {
    //       return await repository.GetAllAsync();
    //   }
    //
    //   Key points:
    //   - [Service] attribute tells Hot Chocolate to inject from DI container
    //   - This is like using @Inject() in NestJS or accessing context.dataSources in Apollo
    //   - Returns non-nullable list of non-nullable users: [User!]!
    //   - For production, you'd add pagination (Hot Chocolate has [UsePaging])
}
