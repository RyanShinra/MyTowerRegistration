# MyTowerRegistration — Claude Guidelines

## Project Overview

Full-stack learning project: Hot Chocolate GraphQL API + EF Core + Blazor WASM admin GUI,
deployed to AWS ECS with RDS PostgreSQL. Primary goal is learning, so educational comments
are intentional and should be preserved.

---

## Code Review Patterns

Patterns discovered during review sessions that are worth checking on every PR.

### GraphQL / Blazor WASM

**DTO property names must match GraphQL JSON keys.**
`System.Text.Json` deserializes by property name (case-insensitive). There are no
`[JsonPropertyName]` attributes on the local DTOs, so the C# property name *is* the
contract. A rename that drifts from the GraphQL field name causes silent null
deserialization — the field deserializes to null with no error.

```csharp
// GraphQL returns: { "registerUser": { "user": {...}, "errors": [...] } }
private record RegisterUserPayload(CreateUserResult? User, ...);   // ✅ matches "user"
private record RegisterUserData(RegisterUserPayload RegisterUser); // ✅ matches "registerUser"

private record RegisterUserPayload(CreateUserResult? CreatedUser, ...);    // ✗ "createdUser" ≠ "user"
private record RegisterUserData(RegisterUserPayload RegistrationPayload);  // ✗ "registrationPayload" ≠ "registerUser"
```

**Every `if`/`else if` discriminated-union chain needs an `else` fallback.**
GraphQL responses have multiple possible shapes. Without a final `else`, a response that
matches none of the expected branches silently does nothing — the spinner stops, the UI
doesn't update, no error surfaces.

```csharp
if (payload?.Errors is { Count: > 0 } errors) { ... }
else if (payload?.User is not null)            { ... }
else { errorMessage = "Unexpected response shape."; }  // ← required
```

**Error state fields must be cleared at the start of each new operation.**
If `HandleDelete` fails (sets `deletionErrorMessage`), and then succeeds on a retry, the
old error persists on screen alongside a correct table state. Clear the field before the
early-return guard, not in `finally`.

```csharp
if (deletionId is not null) return;
deletionErrorMessage = null;  // ← clear before starting, not in finally
deletionId = id;
```

---

### EF Core

**`FindAsync(id, ct)` does NOT pass the CancellationToken.**
`FindAsync` has two overloads:
- `FindAsync(params object?[] keyValues)` — the trap
- `FindAsync(object?[] keyValues, CancellationToken ct)` — what you want

`FindAsync(id, ct)` binds to the `params` overload, boxing both `id` and `ct` into the
key-values array. EF Core searches for an entity with composite key `[id, ct]`, finds
nothing, and returns null. Use the collection expression form:

```csharp
await _context.Users.FindAsync([id], ct);         // ✅
await _context.Users.FindAsync(id, ct);           // ✗ ct is treated as a second key value
```

**Prefer `FirstOrDefaultAsync` over `FindAsync` when CT support is needed and the identity
cache isn't critical.**

```csharp
await _context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);  // no overload trap
```

**Avoid TOCTOU (Time Of Check, Time Of Use) races in async repository methods.**
`GetByIdAsync(id)` followed by `DeleteAsync(id)` has a race window: another request can
delete the row between the two calls, causing the second to fail in a misleading way.
Collapse check + action into a single repository call that returns the entity it operated
on (`User?` instead of `bool`).

```csharp
// ✗ Two calls — race window between them
User? user = await repo.GetByIdAsync(id, ct);
if (user is null) return NotFound();
await repo.DeleteAsync(id, ct);  // could fail if deleted concurrently

// ✅ One call — atomic
User? deleted = await repo.DeleteAsync(id, ct);
if (deleted is null) return NotFound();
```

---

### C# Style

This project follows standard C# conventions:
- File-scoped namespaces (`namespace Foo.Bar;`)
- One public type per file
- `is null` / `is not null` for null checks (not `== null`)
- PascalCase for record parameters (they become public properties)

---

## Architecture Notes

- Raw `HttpClient` GraphQL calls in Blazor pages are **temporary** — StrawberryShake
  typed client codegen is planned. Do not flag these as bugs; flag anything that will make
  the SS migration harder (e.g., DTOs that don't structurally mirror the schema).
- Educational comments throughout the codebase are intentional. Do not suggest removing
  them as part of cleanup.
- `schema.graphql` is auto-exported by an MSBuild `AfterBuild` target (Debug only).
  Do not hand-edit it.
