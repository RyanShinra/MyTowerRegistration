# MyTowerRegistration

A user registration GraphQL API built as a learning project to get hands-on with **C#**, **ASP.NET Core**, **Hot Chocolate 15** (code-first GraphQL), and **Entity Framework Core**. Deployed to AWS ECS Fargate with RDS PostgreSQL.

## Tech Stack

| Component     | Technology                              |
|---------------|-----------------------------------------|
| Runtime       | .NET 10                                 |
| GraphQL       | Hot Chocolate 15 (code-first)           |
| ORM           | Entity Framework Core 10                |
| Database      | PostgreSQL 18 (local) / 16 (AWS RDS) (Npgsql provider) |
| Containerisation | Docker + Docker Compose              |
| Hosting       | AWS ECS Fargate + RDS PostgreSQL        |
| Testing       | xUnit + Moq + EF Core InMemory          |

## Project Structure

```
MyTowerRegistration/
├── MyTowerRegistration.API/          # ASP.NET Core web host + GraphQL
│   ├── GraphQL/
│   │   ├── Types/                    # GraphQL type definitions
│   │   ├── Queries/                  # Query resolvers
│   │   ├── Mutations/                # Mutation resolvers
│   │   └── DataLoaders/              # Batch loading (N+1 prevention)
│   └── Program.cs                    # Composition root (DI wiring)
├── MyTowerRegistration.Data/         # Data access layer
│   ├── Models/User.cs                # EF Core entity
│   ├── AppDbContext.cs               # DbContext
│   └── Repositories/                 # IUserRepository + EF Core implementation
├── MyTowerRegistration.Tests/        # Unit + integration tests
├── scripts/deploy-aws.sh             # One-command AWS deployment
├── docker-compose.yml                # Local dev stack
└── schema.graphql                    # GraphQL schema (SDL reference)
```

## Quick Start (Docker)

```bash
# 1. Copy env file and set your password
cp .env.example .env
# then edit .env and replace the placeholder: DB_PASSWORD=your_password_here

# 2. Start the full stack (Postgres + migrations + API)
docker compose up --build

# 3. Open the GraphQL playground
#    http://localhost:8080/api/graphql
```

## Documentation

| Doc | Contents |
|-----|----------|
| [docs/LEARNING.md](docs/LEARNING.md) | Why these technologies, design decisions, implementation walkthrough |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker Compose setup, AWS ECS architecture, deploy script guide, costs |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Current state, what's next (frontend, Python game, ALB, security) |
| [schema.graphql](schema.graphql) | Full GraphQL schema in SDL format |
