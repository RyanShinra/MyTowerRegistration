// =============================================================================
// IMPLEMENT NINTH — After the repository interface and types are done.
//
// DataLoaders solve the N+1 problem in GraphQL. Here's the scenario:
//
//   query { users { id, username } }   ← returns 100 users
//
// Without DataLoader, if each user had related data (say, "posts"), fetching
// would be: 1 query for users + 100 queries for posts = 101 queries (N+1).
//
// DataLoader batches all those individual fetches into ONE batch query.
// Hot Chocolate integrates DataLoaders natively.
//
// Compare to Apollo Server:
//   In TS, you'd use the 'dataloader' npm package:
//     new DataLoader<number, User>(ids => batchGetUsers(ids))
//   Hot Chocolate's approach is similar but uses a base class.
//
// Hot Chocolate v15 uses BatchDataLoader<TKey, TValue> as the base class.
// You override LoadBatchAsync() to provide the batch-fetching logic.
// =============================================================================

using MyTowerRegistration.Data.Models;
using MyTowerRegistration.Data.Repositories;

namespace MyTowerRegistration.API.GraphQL.DataLoaders;

/// <summary>
/// Batches individual GetById calls into a single SQL query.
/// Scoped per-request by Hot Chocolate automatically.
/// </summary>
public class UserBatchDataLoader : BatchDataLoader<int, User>
{
    private readonly IUserRepository _repository;

    public UserBatchDataLoader(
        IUserRepository repository, 
        IBatchScheduler batchScheduler) 
        : base(batchScheduler, new DataLoaderOptions())
    {
        _repository = repository;
    }


    protected override async Task<IReadOnlyDictionary<int, User>> LoadBatchAsync(IReadOnlyList<int> userIds, CancellationToken ct)
    {
        return await _repository.GetByIdsAsync(userIds, ct);
    }
}
