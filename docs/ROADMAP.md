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
| **API** | GraphQL registration API — register user, query users, delete users |
| **Data layer** | EF Core + PostgreSQL, repository pattern, code-first migrations |
| **Containerisation** | Multi-stage Dockerfile, Docker Compose stack (db + migrations + api) |
| **Admin UI** | Blazor WASM admin frontend — list, register, and delete users |
| **Rate limiting** | ASP.NET Core fixed-window rate limiting on `/api/graphql` (429 on violation) |
| **AWS deployment** | ECS Fargate + RDS PostgreSQL + Secrets Manager + CloudWatch |
| **Load balancer** | ALB in front of ECS; stable URL independent of task restarts |
| **Static hosting** | Blazor admin deployed to S3 + CloudFront (HTTPS, SPA routing) |
| **Scripts** | `setup-infra.sh` (one-time infra) + `deploy.sh` (repeatable deploys) |
| **Domains** | `mytower.dev` connected: `admin-api.mytower.dev` → ALB, `admin.mytower.dev` → CloudFront |
| **TLS** | ACM wildcard cert `*.mytower.dev` — HTTPS enforced end-to-end |
| **Typed GraphQL client** | StrawberryShake 15.x — `IMyTowerClient` and all operation types generated from `schema.graphql` at build time; raw `HttpClient` DTOs retired |

### Architecture Today

```
Docker Compose (local dev)          AWS (deployed)
──────────────────────────          ──────────────────────────────────────
db (Postgres 18)                    RDS PostgreSQL 16
db-migrations (EF bundle)           ECS one-off task (migrations)
api (ASP.NET Core + Hot Choc)       ECS Fargate service
                                    ↑
                                    ALB (HTTPS:443, HTTP:80 → redirect)
                                    ↑
                                    admin-api.mytower.dev (Namecheap CNAME)

admin (Blazor WASM)                 S3 bucket (static files)
                                    ↑
                                    CloudFront (HTTPS, SPA 404→index.html)
                                    ↑
                                    admin.mytower.dev (Namecheap CNAME)
```

### Subdomain Plan

| Subdomain | Target | Status |
|---|---|---|
| `admin-api.mytower.dev` | ALB → ECS (C# GraphQL API) | ✅ Live |
| `admin.mytower.dev` | CloudFront → S3 (Blazor admin) | ✅ Live |
| `game.mytower.dev` | TBD — Svelte game frontend | Future |
| `game-api.mytower.dev` | TBD — Python GraphQL backend | Future |

Note: Route 53 is **not used**. Namecheap CNAME records point directly to the
ALB and CloudFront hostnames. Route 53 ALIAS records would only be needed for
the bare apex `mytower.dev`, which is not currently served.

---

## What's Next

### ~~Phase 4 — Rate Limiting~~ ✅ Done

Fixed-window rate limiting applied to `/api/graphql` via ASP.NET Core's built-in
`Microsoft.AspNetCore.RateLimiting` middleware. Returns `429 Too Many Requests` on
violation. Future hardening: AWS WAF in front of ALB for infrastructure-level protection.

### Phase 4.5 — UpdateUser (CRUD completion)

The U in CRUD is missing:

- Add `UpdateUserInput` record and `UpdateUserPayload` type
- Add `updateUser(input: UpdateUserInput!)` mutation resolver in `UserMutations.cs`
- Add `UpdateAsync(User user, CancellationToken ct)` to `IUserRepository` and `UserRepository`
- Add `UpdateUser.graphql` operation file for StrawberryShake
- Add edit form to Blazor admin UI
- Unit tests: success path, user-not-found, duplicate username/email on update

### ~~Phase 5 — StrawberryShake Typed GraphQL Client~~ ✅ Done

StrawberryShake 15.x replaces raw `HttpClient` calls with a strongly-typed `IMyTowerClient`
generated from `schema.graphql` at build time. Operation types (`IGetUsersResult`,
`IDeleteUserResult`, `IRegisterUserResult`) can never drift from the schema — breaking
changes are compile errors, not silent null bugs at runtime. The old hand-written DTOs are
retained as an exhibit in `GraphQL/Models/GraphQLResponse.cs` with `[Obsolete(error: true)]`.

See `docs/LEARNING.md §StrawberryShake Typed Client` for the full migration breadcrumb trail.

### Phase 6 — Security Hardening

Before the service handles real user data:

- Replace `Trust Server Certificate=true` with proper RDS CA bundle validation
- Review IAM permissions (principle of least privilege)
- Replace SHA-256 password hashing with BCrypt or Argon2 (with salt)
- Consider adding authentication tokens (JWT) for the game identity flow
- Fix `read -r` password echo in `setup-infra.sh` (add `-s` flag — see TODO)
- Move `DB_PASSWORD` unset earlier in `setup-infra.sh` (see TODO)

### Phase 7 — Python Game Backend

The Python tower game exists as a separate project. The next step is linking
player identity from the registration DB into the game:

- New ECS service for the Python GraphQL API → `game-api.mytower.dev`
- Shares the same ALB via host-based routing rules (no new ALB needed)
- The wildcard cert `*.mytower.dev` covers `game-api` automatically
- Game progress tied to registered accounts, persisted across sessions

### Phase 8 — Svelte Game Frontend

- S3 + CloudFront static hosting (same pattern as Blazor admin)
- `game.mytower.dev` CNAME → new CloudFront distribution
- New CloudFront distribution (separate from admin, different S3 bucket)

### Phase 9 — iOS and Steam Clients

Once the web frontend and Python game are stable, native clients can be added.
The GraphQL endpoint is platform-agnostic — no API changes needed for:

- iOS apps (via Apollo iOS or URLSession)
- Steam / desktop clients

---

## Known Tech Debt

| Item | Location | Notes |
|---|---|---|
| `read -r` password echoed to terminal | `setup-infra.sh` | Add `-s` flag for production use |
| `DB_PASSWORD` lives in env ~160 lines | `setup-infra.sh` | Unset immediately after RDS creation |
| Single-AZ subnet for ECS tasks | `deploy.sh` | Intentional for now; expand for production |
| `|| echo "None"` dead code pattern | `setup-infra.sh` | Replace with `--output json` + jq |
| Placeholder connection string not caught at preflight | `deploy.sh` | Now caught — but secret value is fetched on every deploy |
| `aws ecs wait` 10-min hard timeout | `deploy.sh` | Documented; no retry logic yet |
