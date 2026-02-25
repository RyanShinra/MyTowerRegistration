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
    // TODO 1: Add a private readonly field for IUserRepository
    private readonly IUserRepository _repository;

    // TODO 2: Add constructor:
    //   public UserBatchDataLoader(
    //       IUserRepository repository,
    //       IBatchScheduler batchScheduler,
    //       DataLoaderOptions? options = null)
    //       : base(batchScheduler, options)
    //   {
    //       _repository = repository;
    //   }

    //
    //   IBatchScheduler is injected by Hot Chocolate — it controls WHEN
    //   the batch fires (after all resolvers at the current "level" have
    //   enqueued their requests).

    public UserBatchDataLoader(
        IUserRepository repository, 
        IBatchScheduler batchScheduler, 
        DataLoaderOptions? options = null) 
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _repository = repository;
    }

    //public UserBatchDataLoader(
    //    IUserRepository repository, 
    //    IBatchScheduler batchScheduler)
    //    : this(repository, batchScheduler, new DataLoaderOptions()) 
    //{ }




    // TODO 3: Override LoadBatchAsync:
    //   protected override async Task<IReadOnlyDictionary<int, User>> LoadBatchAsync(
    //       IReadOnlyList<int> keys,
    //       CancellationToken ct)
    //   {
    //       // Delegate to the repository's batch method
    //       var users = await _repository.GetByIdsAsync(keys, ct);
    //       return users.AsReadOnly();  // or cast as needed
    //   }
    //
    //   "keys" contains ALL the IDs that were requested during this execution
    //   level. Instead of 100 individual queries, you get ONE:
    //     SELECT * FROM "Users" WHERE "Id" IN (1, 2, 3, ...)

    protected override async Task<IReadOnlyDictionary<int, User>> LoadBatchAsync(IReadOnlyList<int> userIds, CancellationToken ct)
    {
        IDictionary<int, User> users = await _repository.GetByIdsAsync(userIds, ct);
        return (IReadOnlyDictionary<int, User>) users;
    }
}
