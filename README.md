# MyTowerRegistration

A user registration GraphQL API built with **Hot Chocolate 15**, **ASP.NET Core**, **EF Core 10**, and **PostgreSQL 18**.

Demo project for learning modern .NET GraphQL development.

## Tech Stack

| Component     | Technology                              |
|---------------|-----------------------------------------|
| Runtime       | .NET 10 (LTS)                           |
| GraphQL       | Hot Chocolate 15.1.12 (code-first)      |
| ORM           | Entity Framework Core 10.0.0            |
| Database      | PostgreSQL 18 (Npgsql provider)         |
| Testing       | xUnit + Moq + EF Core InMemory         |

## Project Structure

```
MyTowerRegistration/
├── MyTowerRegistration.API/          # ASP.NET Core web host + GraphQL
│   ├── GraphQL/
│   │   ├── Types/                    # GraphQL type definitions
│   │   │   ├── UserType.cs           # User → GraphQL type mapping
│   │   │   ├── UserErrorType.cs      # Error record
│   │   │   ├── RegisterUserInput.cs  # Mutation input record
│   │   │   └── RegisterUserPayload.cs# Mutation result record
│   │   ├── Queries/
│   │   │   └── UserQueries.cs        # Query resolvers
│   │   ├── Mutations/
│   │   │   └── UserMutations.cs      # Mutation resolvers
│   │   └── DataLoaders/
│   │       └── UserDataLoader.cs     # Batch loading (N+1 prevention)
│   ├── Program.cs                    # Composition root (DI wiring)
│   ├── appsettings.json              # Connection string config
│   └── MyTowerRegistration.API.http  # Test requests for VS/.http
├── MyTowerRegistration.Data/         # Data access layer
│   ├── Models/
│   │   └── User.cs                   # EF Core entity
│   ├── AppDbContext.cs               # DbContext (database bridge)
│   └── Repositories/
│       ├── IUserRepository.cs        # Repository interface
│       └── UserRepository.cs         # EF Core implementation
└── MyTowerRegistration.Tests/        # Unit + integration tests
    ├── UserMutationTests.cs          # Mutation logic tests (mocked)
    └── UserRepositoryTests.cs        # Repository tests (InMemory DB)
```

## Implementation Roadmap

Work through the files in this order. Each file has numbered TODO comments that guide you step by step.

### Phase 1: Data Layer (get the foundation compiling)

| Step | File | What to implement |
|------|------|-------------------|
| 1st  | `Data/Models/User.cs` | Entity class: Id, Username, Email, PasswordHash, CreatedAt properties |
| 2nd  | `Data/AppDbContext.cs` | DbContext: constructor, DbSet, OnModelCreating (unique indexes) |
| 3rd  | `Data/Repositories/IUserRepository.cs` | Interface: 6 method signatures |
| 4th  | `Data/Repositories/UserRepository.cs` | Implementation: EF Core LINQ queries |

**Checkpoint:** The Data project should compile. Run `dotnet build MyTowerRegistration.Data`.

### Phase 2: GraphQL Types (schema building blocks)

| Step | File | What to implement |
|------|------|-------------------|
| 5th  | `API/GraphQL/Types/UserType.cs` | ObjectType descriptor: expose fields, hide PasswordHash |
| 6th  | `API/GraphQL/Types/UserErrorType.cs` | One-line record: `UserError(string Message, string Code)` |
| 7th  | `API/GraphQL/Types/RegisterUserInput.cs` | One-line record: `RegisterUserInput(...)` |
| 8th  | `API/GraphQL/Types/RegisterUserPayload.cs` | One-line record: `RegisterUserPayload(...)` |

### Phase 3: DataLoader + Resolvers

| Step | File | What to implement |
|------|------|-------------------|
| 9th  | `API/GraphQL/DataLoaders/UserDataLoader.cs` | BatchDataLoader: constructor + LoadBatchAsync |
| 10th | `API/GraphQL/Queries/UserQueries.cs` | Two methods: GetUser (DataLoader), GetUsers (repository) |
| 11th | `API/GraphQL/Mutations/UserMutations.cs` | RegisterUser: validate, hash, save, return payload |

### Phase 4: Wire It Up

| Step | File | What to implement |
|------|------|-------------------|
| 12th | `API/Program.cs` | Uncomment/add: AddDbContext, AddScoped, AddGraphQLServer, MapGraphQL |

**Checkpoint:** `dotnet build` should succeed. `dotnet run` should start the server.

### Phase 5: Database

```bash
# Install the EF Core CLI tool (one-time)
dotnet tool install --global dotnet-ef

# Create the initial migration (from the solution root)
dotnet ef migrations add InitialCreate --project MyTowerRegistration.Data --startup-project MyTowerRegistration.API

# Apply migration to PostgreSQL
dotnet ef database update --project MyTowerRegistration.Data --startup-project MyTowerRegistration.API
```

### Phase 6: Test

```bash
# Run the app
dotnet run --project MyTowerRegistration.API

# Open the Nitro GraphQL IDE in your browser:
#   http://localhost:5026/graphql

# Or use the .http file in Visual Studio:
#   MyTowerRegistration.API/MyTowerRegistration.API.http

# Run unit tests
dotnet test
```

### Phase 7: Unit Tests

| File | What to implement |
|------|-------------------|
| `Tests/UserMutationTests.cs` | Uncomment the 5 test methods (they're pre-written in comments) |
| `Tests/UserRepositoryTests.cs` | Implement CreateInMemoryContext helper, uncomment tests |

## Setup Prerequisites

1. **.NET 10 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
2. **PostgreSQL 18** — Running on `localhost:5432`
3. **Create the database:**
   ```sql
   CREATE DATABASE mytower;
   ```
   ## Local Development Setup

  Create `MyTowerRegistration.API/appsettings.Development.json` (not committed — contains secrets):
  ```json
  {
    "Logging": {
      "LogLevel": {
        "Default": "Information",
        "Microsoft.AspNetCore": "Warning",
        "HotChocolate": "Debug"
      }
    },
    "ConnectionStrings": {
      "DefaultConnection": "Host=localhost;Port=5432;Database=mytower;Username=postgres;Password=YOUR_PASSWORD"
    }
  }
  ```

  Also create `.env` in the repo root (see `.env.example` for required variables).

4. **Update connection string** in `appsettings.json` and `appsettings.Development.json`:
  ```json
  "DefaultConnection": "Host=localhost;Port=5432;Database=mytower;Username=postgres;Password=YOUR_PASSWORD"
  ```

## GraphQL Schema

```graphql
type Query {
  user(id: Int!): User
  users: [User!]!
}

type Mutation {
  registerUser(input: RegisterUserInput!): RegisterUserPayload!
}

input RegisterUserInput {
  username: String!
  email: String!
  password: String!
}

type RegisterUserPayload {
  user: User
  errors: [UserError!]
}

type User {
  id: Int!
  username: String!
  email: String!
  createdAt: DateTime!
}

type UserError {
  message: String!
  code: String!
}
```

## Key Concepts for Interview Discussion

- **Code-First GraphQL**: C# classes define the schema (vs. schema-first SDL)
- **DataLoader Pattern**: Batches N+1 queries into single SQL statements
- **Repository Pattern**: Abstracts EF Core behind interfaces for testability
- **DI Lifetimes**: Scoped (per-request) for DbContext/repos, managed by ASP.NET Core
- **Error-as-Data**: Mutations return errors in the payload, not as GraphQL exceptions
- **EF Core Migrations**: Code-first schema evolution (like Prisma Migrate)
