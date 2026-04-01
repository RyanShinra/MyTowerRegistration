# Learning Background

This project was built as a deliberate learning exercise in the modern .NET ecosystem,
specifically targeting C#, ASP.NET Core, Hot Chocolate (GraphQL), and Entity Framework Core.
It serves as the backend registration service for a larger tower defence game project.

---

## Why These Technologies?

### C# and .NET 10

Coming from a TypeScript/Python background, the goal was hands-on experience with
statically-typed, compiled .NET — specifically how dependency injection, async/await,
and type-safe patterns differ from a dynamic language ecosystem.

.NET 10 was chosen over the then-current established LTS (.NET 8) to take advantage
of newer platform features and tooling while still targeting an LTS release.

### Hot Chocolate (GraphQL)

Three .NET GraphQL libraries were evaluated:

| Library | Approach | Verdict |
|---|---|---|
| **Hot Chocolate** | Code-first or schema-first, full-featured | Chosen |
| **GraphQL for .NET** | Schema-first, lower-level | Requires more manual wiring |
| **Strawberry Shake** | Client-side library | Wrong side of the stack |

Hot Chocolate 15 was chosen because:
- **Code-first** approach keeps C# as the single source of schema truth
- Built-in **DataLoader** support (N+1 prevention out of the box)
- **Mutation convention** (error-as-data pattern) is idiomatic and well-documented
- Active development and strong .NET community adoption

### Code-First vs Schema-First GraphQL

A deliberate choice was made to use **code-first** rather than schema-first SDL.

| Aspect | Code-First | Schema-First |
|---|---|---|
| Schema defined in | C# classes and attributes | `.graphql` SDL files |
| Type safety | Compile-time | Runtime (SDL parsing) |
| Refactoring | IDE rename support | Manual SDL + resolver sync |
| Entry point | C# → schema generated | SDL → resolvers written to match |

For a C# project, code-first means the schema is defined entirely by the C# type system.
Breaking schema changes are compiler errors, not runtime surprises.

### Entity Framework Core (over Dapper or raw ADO.NET)

| Library | Style | Trade-off |
|---|---|---|
| **EF Core** | ORM (LINQ → SQL) | Chosen — migrations, change tracking, testable |
| **Dapper** | Micro-ORM (raw SQL + mapping) | More control, less abstraction |
| **ADO.NET** | Raw SQL | Maximum control, maximum boilerplate |

EF Core was chosen to learn:
- **Code-first migrations** (schema evolution as committed code)
- The **repository pattern** with EF Core as the backing implementation
- **InMemory provider** for unit testing without a real database

### Repository Pattern

The data layer exposes `IUserRepository` / `UserRepository` rather than allowing
resolvers to call `AppDbContext` directly. This pattern:

- Enables **unit testing with mocks** (Moq replaces the repository in tests)
- Keeps **GraphQL resolvers decoupled** from database implementation details
- Makes a future switch from EF Core to Dapper (or another ORM) a contained change

### Error-as-Data Pattern

Mutations return a **payload type** containing both the result and any errors,
rather than throwing GraphQL exceptions:

```graphql
type RegisterUserPayload {
  user: User          # null on failure
  errors: [UserError!] # null on success
}
```

This gives API clients a predictable response shape regardless of outcome, and
keeps error handling in the normal data flow rather than in catch blocks.

---

## Implementation Roadmap

The project was implemented in phases, each building on the last.
Each source file contains numbered TODO comments with detailed guidance.

### Phase 1 — Data Layer

| Step | File | What was implemented |
|------|------|----------------------|
| 1 | `MyTowerRegistration.Data/Models/User.cs` | Entity: Id, Username, Email, PasswordHash, CreatedAt |
| 2 | `MyTowerRegistration.Data/AppDbContext.cs` | DbContext with unique indexes on Username and Email |
| 3 | `MyTowerRegistration.Data/Repositories/IUserRepository.cs` | Interface: 6 method signatures |
| 4 | `MyTowerRegistration.Data/Repositories/UserRepository.cs` | EF Core LINQ implementation |

**Checkpoint:** `dotnet build MyTowerRegistration.Data`

### Phase 2 — GraphQL Types

| Step | File | What was implemented |
|------|------|----------------------|
| 5 | `MyTowerRegistration.API/GraphQL/Types/UserType.cs` | ObjectType descriptor — exposes fields, hides PasswordHash |
| 6 | `MyTowerRegistration.API/GraphQL/Types/UserErrorType.cs` | UserError record + UserErrorCode enum |
| 7 | `MyTowerRegistration.API/GraphQL/Types/RegisterUserInput.cs` | Input record (Username, Email, Password) |
| 8 | `MyTowerRegistration.API/GraphQL/Types/RegisterUserPayload.cs` | Payload record (User?, Errors?) |

### Phase 3 — DataLoader and Resolvers

| Step | File | What was implemented |
|------|------|----------------------|
| 9  | `MyTowerRegistration.API/GraphQL/DataLoaders/UserDataLoader.cs` | BatchDataLoader — LoadBatchAsync |
| 10 | `MyTowerRegistration.API/GraphQL/Queries/UserQueries.cs` | GetUser (DataLoader), GetUsers (repository) |
| 11 | `MyTowerRegistration.API/GraphQL/Mutations/UserMutations.cs` | RegisterUser — validate, hash, save, return payload |

### Phase 4 — Wiring

| Step | File | What was implemented |
|------|------|----------------------|
| 12 | `MyTowerRegistration.API/Program.cs` | AddDbContext, AddScoped, AddGraphQLServer, MapGraphQL |

**Checkpoint:** `dotnet build` succeeds, `dotnet run` starts the server.

### Phase 5 — Database

```bash
dotnet tool install --global dotnet-ef

dotnet ef migrations add InitialCreate \
  --project MyTowerRegistration.Data \
  --startup-project MyTowerRegistration.API

dotnet ef database update \
  --project MyTowerRegistration.Data \
  --startup-project MyTowerRegistration.API
```

### Phase 6 — Tests

| File | What to implement |
|------|-------------------|
| `MyTowerRegistration.Tests/UserMutationTests.cs` | 5 mutation tests with mocked repository |
| `MyTowerRegistration.Tests/UserRepositoryTests.cs` | Repository tests using EF Core InMemory provider |

```bash
dotnet test
```

---

## Key Concepts for Interview Discussion

| Concept | Implementation |
|---|---|
| **Code-First GraphQL** | C# classes define the schema; Hot Chocolate generates SDL |
| **DataLoader Pattern** | `UserBatchDataLoader` batches N+1 queries into single SQL calls |
| **Repository Pattern** | `IUserRepository` / `UserRepository` — EF Core behind an interface |
| **DI Lifetimes** | Scoped for DbContext/repos (one per HTTP request), managed by ASP.NET Core |
| **Error-as-Data** | `RegisterUserPayload` returns errors in the payload, not as exceptions |
| **EF Core Migrations** | Code-first schema evolution — migration files committed alongside source |
| **Password Hashing** | SHA-256 for demo; production would use BCrypt or Argon2 with salt |
