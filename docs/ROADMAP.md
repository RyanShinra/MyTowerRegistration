# Roadmap

## What This Is

MyTowerRegistration is the backend identity and registration service for a
tower defence game project. It handles user accounts so that game progress,
leaderboards, and cross-platform identity can be tied to a persistent profile.

The registration API was built first — as a deliberate learning exercise in
the .NET ecosystem — before the game frontends are connected to it.

---

## Current State

### Done

| Area | What's built |
|---|---|
| **API** | GraphQL registration API — register user, query users |
| **Data layer** | EF Core + PostgreSQL, repository pattern, code-first migrations |
| **Containerisation** | Multi-stage Dockerfile, Docker Compose stack (db + migrations + api) |
| **AWS deployment** | ECS Fargate + RDS PostgreSQL + Secrets Manager + CloudWatch |
| **Deploy script** | `scripts/deploy-aws.sh` — idempotent, one-command deployment |
| **Domain** | `mytower.dev` and `mytowergame.com` registered at Namecheap |

### Architecture Today

```
Docker Compose (local dev)          AWS ECS (deployed)
──────────────────────────          ──────────────────
db (Postgres 18)                    RDS PostgreSQL 16
db-migrations (EF bundle)           ECS one-off task (migrations)
api (ASP.NET Core + Hot Choc)       ECS service (API, Fargate)
```

---

## What's Next

### Phase 3 — Registration Frontend

A web frontend (likely Next.js or React) that calls the GraphQL API directly.
Intended as the player-facing registration and profile page at `io.mytower.dev`.

- GraphQL client (Apollo Client or urql)
- Register / login UI
- Profile page

### Phase 4 — Python Game Frontend + Identity Linking

The Python tower game already exists as a separate project. The next
step is linking player identity from the registration DB into the game so that:

- Game progress is tied to a registered account
- Scores and progress persist across sessions
- The registration and game backends share a user identity

The Python game's backend will call `io.mytower.dev` for identity verification.

### Phase 5 — Stable URL (ALB + Domain)

Currently the ECS task gets an ephemeral public IP on each start. Before sharing
links publicly (resume, portfolio), a stable URL is needed:

1. Set up an Application Load Balancer (ALB) in front of ECS
   - Routes `io.mytower.dev` → .NET registration API
   - Routes `game.mytower.dev` → Python game backend
   - One ALB (~$16/month) serves both backends
2. Point `mytower.dev` nameservers to Route 53
3. Issue free SSL certificate via AWS Certificate Manager (ACM)
4. HTTPS on both subdomains

### Phase 6 — Security Review

Before making the service permanently available under a registered domain:

- Replace `Trust Server Certificate=true` with proper RDS CA bundle validation
- Review IAM permissions (principle of least privilege)
- Add rate limiting to the registration endpoint
- Replace SHA-256 password hashing with BCrypt or Argon2 (with salt)
- Review CORS policy for frontend origins
- Consider adding authentication tokens (JWT) for the game identity flow

### Phase 7 — iOS and Steam Clients

Once the web frontend and Python game are stable, native clients can be added.
The API endpoint (`io.mytower.dev/api/graphql`) works identically for:

- Web browsers
- iOS apps (via Apollo iOS or URLSession)
- Steam / desktop clients

No API changes are needed — the GraphQL endpoint is platform-agnostic.

---

## Domain Plan

| Domain | Purpose |
|---|---|
| `mytower.dev` | Infrastructure / API domain |
| `io.mytower.dev` | Registration API (once ALB is set up) |
| `game.mytower.dev` | Python game backend |
| `mytowergame.com` | Player-facing marketing / landing page |

Both domains are currently registered at Namecheap. DNS will be delegated to
Route 53 when the ALB is configured (Phase 5).
