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
| **Admin UI** | Blazor WASM admin frontend — list and delete users |
| **AWS deployment** | ECS Fargate + RDS PostgreSQL + Secrets Manager + CloudWatch |
| **Load balancer** | ALB in front of ECS; stable URL independent of task restarts |
| **Static hosting** | Blazor admin deployed to S3 + CloudFront (HTTPS, SPA routing) |
| **Deploy script** | `scripts/deploy-aws.sh` — idempotent, one-command deployment |
| **Domains** | `mytower.dev` and `mytowergame.com` registered at Namecheap |

### Architecture Today

```
Docker Compose (local dev)          AWS (deployed)
──────────────────────────          ──────────────────────────────────────
db (Postgres 18)                    RDS PostgreSQL 16
db-migrations (EF bundle)           ECS one-off task (migrations)
api (ASP.NET Core + Hot Choc)       ECS Fargate service
                                    ↑
                                    ALB (stable DNS, health checks)

admin (Blazor WASM)                 S3 bucket (static files)
                                    ↑
                                    CloudFront (HTTPS, SPA 404→index.html)
```

---

## What's Next

### Phase 3 — Custom Domains + HTTPS on ALB

DNS and TLS are the last step before sharing the service publicly.

1. Delegate `mytower.dev` nameservers to Route 53
2. Issue SSL certificate via ACM (`*.mytower.dev` or individual subdomains)
3. Add HTTPS listener on the ALB; terminate TLS there
4. DNS records:
   - `api.mytower.dev` → ALB
   - `admin.mytower.dev` → CloudFront distribution
5. Update deploy script to set `ApiBaseUrl` to `https://api.mytower.dev`
   instead of the CloudFront domain (eliminates the need to route `/api/*`
   through CloudFront)

### Phase 4 — StrawberryShake Typed GraphQL Client

The Blazor admin currently uses raw `HttpClient` calls with hand-written DTOs.
StrawberryShake generates strongly-typed C# client code from `schema.graphql`
at build time — the same pattern as Apollo codegen in the JS ecosystem.

- Add StrawberryShake to the Admin project
- Replace raw `HttpClient` calls with generated client
- `schema.graphql` is already auto-exported on every Debug build

### Phase 5 — Security Hardening

Before the service is permanently public under a registered domain:

- Replace `Trust Server Certificate=true` with proper RDS CA bundle validation
- Review IAM permissions (principle of least privilege)
- Add rate limiting to the registration endpoint
- Replace SHA-256 password hashing with BCrypt or Argon2 (with salt)
- Consider adding authentication tokens (JWT) for the game identity flow

### Phase 6 — Python Game Frontend + Identity Linking

The Python tower game exists as a separate project. The next step is linking
player identity from the registration DB into the game so that:

- Game progress is tied to a registered account
- Scores and progress persist across sessions
- The registration and game backends share a user identity

The Python game's backend will call `api.mytower.dev` for identity verification.

### Phase 7 — iOS and Steam Clients

Once the web frontend and Python game are stable, native clients can be added.
The GraphQL endpoint is platform-agnostic — no API changes needed for:

- iOS apps (via Apollo iOS or URLSession)
- Steam / desktop clients

---

## Domain Plan

| Domain | Purpose |
|---|---|
| `mytower.dev` | Infrastructure / API domain |
| `api.mytower.dev` | Registration API (ALB) |
| `admin.mytower.dev` | Blazor admin UI (CloudFront) |
| `mytowergame.com` | Player-facing marketing / landing page |

Both domains are registered at Namecheap. DNS will be delegated to Route 53
in Phase 3.
