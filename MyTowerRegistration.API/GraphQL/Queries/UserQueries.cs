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

public class UserQueries
{
    public async Task<User?> GetUserAsync(int id, UserBatchDataLoader loader, CancellationToken ct)
    {
        return await loader.LoadAsync(id, ct);
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync([Service] IUserRepository userRepo, CancellationToken ct)
    {
        return await userRepo.GetAllAsync(ct);
    }
}
