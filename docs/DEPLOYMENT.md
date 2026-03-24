# Deployment

This document covers three ways to run the API:

1. **[Visual Studio (bare metal)](#local-development-with-visual-studio)** — fastest for active development and debugging
2. **[Docker Compose](#local-development-with-docker-compose)** — full stack locally, no Postgres installation required
3. **[AWS ECS](#aws-ecs-deployment)** — production deployment on Fargate + RDS

---

## Local Development with Visual Studio

The best option when you want to set a breakpoint, step through resolver logic, or iterate quickly without rebuilding Docker images.

### Prerequisites

1. **Visual Studio 2022** (or Rider) with the **ASP.NET and web development** workload
2. **.NET 10 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
3. **PostgreSQL** running locally on port 5432
   - Windows installer: [postgresql.org/download/windows](https://www.postgresql.org/download/windows/) (includes pgAdmin)
   - Remember the `postgres` password you set during installation
4. **CLI tools** — restored from `dotnet-tools.json` (see First-Time Setup step 1).
   If you prefer a global install instead:
   ```bash
   dotnet tool install --global dotnet-ef --version 10.0.0
   ```

### First-Time Setup

**0. Restore CLI tools**

From the solution root (once per machine, or after a fresh clone):
```bash
dotnet tool restore
```
This installs `dotnet-ef` and any other tools listed in `dotnet-tools.json`.
Unlike NuGet packages, CLI tools are not restored automatically by `dotnet build`.

**1. Create the database**

Open pgAdmin (or `psql`) and run:
```sql
CREATE DATABASE mytower;
```

**2. Create your local settings file**

Create `MyTowerRegistration.API/appsettings.Development.json` — this file is git-ignored and holds your local secrets:

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

Replace `YOUR_PASSWORD` with the password you chose when installing Postgres.

**3. Apply migrations**

From the solution root:
```bash
dotnet ef database update \
  --project MyTowerRegistration.Data \
  --startup-project MyTowerRegistration.API
```

This creates the `Users` table and `__EFMigrationsHistory` in your local `mytower` database.

### Running and Debugging

**In Visual Studio (API only):**

1. Set `MyTowerRegistration.API` as the startup project (right-click → *Set as Startup Project*)
2. Press **F5** to run with the debugger, or **Ctrl+F5** to run without
3. ASP.NET Core loads `appsettings.Development.json` automatically because `ASPNETCORE_ENVIRONMENT=Development` is set in the default launch profile

**Open the Nitro GraphQL playground:**
```
http://localhost:5026/api/graphql
```

> **Note:** Port 5026 is the local dev port (set in `launchSettings.json`).
> Docker Compose uses port 8080. They're different environments — both are fine.

**Alternatively, use the `.http` file** in Visual Studio:
`MyTowerRegistration.API/MyTowerRegistration.API.http` has pre-written requests for the register mutation and user queries.

**Running API + Admin together:**

1. Right-click the **Solution** → **Properties** → **Common Properties → Configure Startup Projects**
2. Select **Multiple startup projects**
3. Set both `MyTowerRegistration.API` and `MyTowerRegistration.Admin` to **Start**
4. While there, go to **Project Build Dependencies** and tick `MyTowerRegistration.API` as a dependency of `MyTowerRegistration.Admin` — this ensures the schema export runs before the Admin project builds
5. Press **F5** — both projects launch, two browser tabs open:
   - API Nitro playground: `http://localhost:5026/api/graphql`
   - Admin app: `http://localhost:5273`

### Adding a New Migration (after schema changes)

When you change an EF Core entity (e.g. add a field to `User.cs`):

```bash
dotnet ef migrations add YourMigrationName \
  --project MyTowerRegistration.Data \
  --startup-project MyTowerRegistration.API

dotnet ef database update \
  --project MyTowerRegistration.Data \
  --startup-project MyTowerRegistration.API
```

Commit the generated migration files alongside the code change.

---

## Local Development with Docker Compose

The fastest way to run the full stack locally — no local Postgres installation required.

### Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with WSL 2 backend (Windows)

### Quick Start

1. Copy `.env.example` to `.env` and set a password:
   ```
   DB_PASSWORD=your_password_here
   ```

2. Start the stack:
   ```bash
   docker compose up --build
   ```

3. Open the GraphQL playground: `http://localhost:8080/api/graphql`

### How Docker Compose Works

Three services start in dependency order:

```
db (Postgres)  →  db-migrations (EF bundle, exits 0)  →  api (ASP.NET Core)
     ↑                      ↑                                    ↑
health check          waits for db healthy              waits for migrations done
```

| Service | Image | Role |
|---|---|---|
| `db` | `postgres:18` | Runs PostgreSQL, exposes port 5432 locally |
| `db-migrations` | Built from `dbMigrations` stage | Applies EF Core migrations, then exits |
| `api` | Built from `runtime` stage | Runs the .NET API on port 8080 |

### Multi-Stage Dockerfile

The Dockerfile has three stages — each produces a lean image by discarding
the full SDK after use:

```
build stage (SDK ~800MB)
    ├── dotnet restore (cached unless .csproj changes)
    ├── dotnet publish  →  /app/publish
    └── dotnet ef migrations bundle  →  /app/migrate-db

dbMigrations stage (~100MB runtime)
    └── copies migrate-db from build
        ENTRYPOINT: ./migrate-db --connection

runtime stage (~100MB runtime)
    └── copies /app/publish from build
        ENTRYPOINT: dotnet MyTowerRegistration.API.dll
```

### Expected Log Noise (Not Errors)

On first run you will see:

- `Cannot load library libgssapi_krb5.so.2` — Npgsql probes for optional Kerberos
  support, doesn't find it, continues normally
- `relation "__EFMigrationsHistory" does not exist` — EF checks this table before
  creating it; expected on a fresh database

### Stopping

```bash
docker compose down        # stop containers (data persists in named volume)
docker compose down -v     # stop containers AND wipe the database volume
```

---

## AWS ECS Deployment

The API is deployed to AWS Fargate (serverless containers) with RDS PostgreSQL.

### Architecture

```
Internet
    │
    │  port 8080 (HTTP)
    ▼
ECS Task (Fargate)
├── Security Group: mytower-registration
├── Public subnet (default VPC)
├── ECR image: mytower-registration-api:latest
└── Secrets Manager → ConnectionStrings__DefaultConnection
    │
    │  port 5432 (private VPC only)
    ▼
RDS PostgreSQL 16 (db.t3.micro)
├── Security Group: mytower-registration-rds-sg
│   └── Ingress: port 5432 from ECS security group only
├── Subnet group: all 3 AZs
└── Credentials stored in Secrets Manager
```

### Services Used

| Service | Purpose |
|---|---|
| **ECR** | Private Docker image registry — stores API and migrations images |
| **ECS Fargate** | Runs containers without managing EC2 instances |
| **RDS PostgreSQL 16** | Managed database (db.t3.micro, 20GB gp2) |
| **Secrets Manager** | Stores the DB connection string — injected at container startup |
| **CloudWatch Logs** | Container stdout/stderr → `/ecs/mytower-registration` |
| **IAM** | `ecsTaskExecutionRole` — allows ECS to pull images, write logs, read secrets |

### Credentials Flow

The DB password never touches source control, task definitions, or CloudWatch logs:

```
deploy script prompts → HTTPS → Secrets Manager (AES-256 at rest)
                                         ↓
                              ECS fetches at container startup
                                         ↓
                              Injected as env var inside container only
```

### Running the Deploy Script

**Prerequisites:**
- AWS CLI configured (`aws configure`)
- Docker Desktop running
- `jq` installed (`winget install jqlang.jq` on Windows)
- Run from the repo root on Windows using Git Bash

```bash
./scripts/deploy-aws.sh
```

The script will prompt for a DB password once (step 4). It's never written to
disk or shell history.

**What the script does:**

| Step | Action |
|---|---|
| 1 | Grants `ecsTaskExecutionRole` permission to read Secrets Manager |
| 2 | Creates ECR repositories for API and migrations images |
| 3 | Builds and pushes Docker images to ECR |
| 4 | Prompts for DB password, stores placeholder in Secrets Manager |
| 5 | Creates RDS security group (port 5432, ECS traffic only) |
| 6 | Opens port 8080 on the ECS security group |
| 7 | Creates RDS subnet group and RDS instance (~5-10 min wait) |
| 8 | Updates Secrets Manager with full connection string |
| 9 | Creates CloudWatch log group |
| 10 | Registers migrations ECS task definition |
| 11 | Runs migrations as a one-off task, waits for exit 0 |
| 12 | Registers API ECS task definition |
| 13 | Creates ECS service (keeps 1 task running, auto-restarts on crash) |
| 14 | Prints public IP and GraphQL playground URL |

**Windows note:** The script sets `MSYS_NO_PATHCONV=1` to prevent Git Bash
from converting paths like `/ecs/mytower-registration` into Windows file paths.

### Starting and Stopping (Cost Management)

ECS and RDS can be stopped when not in use to reduce costs.

**Via AWS Console mobile app or web console:**

Stop:
1. ECS → Clusters → mytower-cluster → mytower-registration-api → Update → desired count: **0**
2. RDS → Databases → mytower-registration-db → Actions → **Stop temporarily**

Start:
1. RDS → Start (takes ~2 minutes)
2. ECS → Update → desired count: **1** (task starts in ~30 seconds)

**Note:** AWS automatically restarts stopped RDS instances after 7 days.

### Cost Breakdown

| Resource | Running | Stopped |
|---|---|---|
| ECS Fargate (0.25 vCPU, 0.5GB) | ~$9/month | $0 |
| RDS db.t3.micro | ~$13/month | ~$2/month (storage only) |
| ECR (image storage) | ~$1/month | ~$1/month |
| **Total** | **~$23/month** | **~$3/month** |

A load balancer (ALB, ~$16/month) is not yet configured — the task gets an
ephemeral public IP on each start. See [ROADMAP.md](ROADMAP.md) for the plan
to add a stable URL via ALB + `io.mytower.dev`.

### Useful Commands

```bash
# Check running tasks
aws ecs list-tasks --cluster mytower-cluster

# View logs
# https://us-east-2.console.aws.amazon.com/cloudwatch/home?region=us-east-2#logsV2:log-groups/log-group/%2Fecs%2Fmytower-registration

# Retrieve the connection string (if you need to connect directly to RDS)
aws secretsmanager get-secret-value \
  --secret-id "mytower-registration/db-connection-string" \
  --query 'SecretString' --output text
```
