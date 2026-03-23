# Deployment

This document covers running the API locally with Docker Compose and deploying
it to AWS ECS (Elastic Container Service) with RDS PostgreSQL.

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
├── Security Group: mytower-registration (sg-05354e42eaaf4662d)
├── Public subnet (us-east-2a)
├── ECR image: mytower-registration-api:latest
└── Secrets Manager → ConnectionStrings__DefaultConnection
    │
    │  port 5432 (private VPC only)
    ▼
RDS PostgreSQL 16 (db.t3.micro)
├── Security Group: mytower-registration-rds-sg
│   └── Ingress: port 5432 from ECS security group only
├── Subnet group: all 3 AZs (us-east-2a/b/c)
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
