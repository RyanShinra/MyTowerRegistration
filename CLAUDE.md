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

### Testing (xUnit + Moq)

**Use `Assert.Collection` instead of `NotNull` + `Single` + `Equal([0])`.**
`Assert.Collection` implicitly asserts non-null and exact element count via the number of
lambdas. Name lambda parameters `errorZero`, `errorOne`, etc. so the index mapping is
explicit — `error =>` is ambiguous, `errorZero =>` is not.

```csharp
// ✗ Three assertions doing the work of one, index implicit
Assert.NotNull(result.Errors);
Assert.Single(result.Errors);
Assert.Equal(DeleteUserErrorCode.UserNotFound, result.Errors[0].Code);

// ✅ One assertion, index explicit in the parameter name
Assert.Collection(result.Errors,
    errorZero => Assert.Equal(DeleteUserErrorCode.UserNotFound, errorZero.Code));

// ✅ Multiple errors — position is unambiguous
Assert.Collection(result.Errors,
    errorZero => Assert.Equal(DeleteUserErrorCode.UserNotFound,       errorZero.Code),
    errorOne  => Assert.Equal(DeleteUserErrorCode.UnauthorizedDeletion, errorOne.Code));
```

**Use explicit return types (not `var`) for Act results in tests.**
The return type of the method under test is part of its contract. An explicit type causes
a compile error if the signature changes; `var` silently adapts and hides the breakage.

```csharp
RegisterUserPayload result = await _mutations.RegisterUser(...);  // ✅ compile-time contract
var result = await _mutations.RegisterUser(...);                   // ✗ hides signature changes
```

**`Assert.Collection` is order-sensitive — decide if order is part of the contract.**
If error order matters to callers (e.g. primary error is always first), use
`Assert.Collection` and the order is tested. If order is irrelevant, use
`Assert.Contains` per error plus an explicit count check:

```csharp
// ✅ When order is guaranteed
Assert.Collection(result.Errors,
    errorZero => Assert.Equal(DeleteUserErrorCode.UserNotFound,        errorZero.Code),
    errorOne  => Assert.Equal(DeleteUserErrorCode.UnauthorizedDeletion, errorOne.Code));

// ✅ When order is not guaranteed
Assert.Contains(result.Errors, e => e.Code == DeleteUserErrorCode.UserNotFound);
Assert.Contains(result.Errors, e => e.Code == DeleteUserErrorCode.UnauthorizedDeletion);
Assert.Equal(2, result.Errors!.Count); // pin count — Contains alone allows extras
```

**Use descriptive lambda parameter names in Moq Setup/Verify.**
`r =>` is ambiguous when the method under test has a similar name. Use the interface
concept as the name so the stub reads as a sentence.

```csharp
_mockRepo.Setup(repo => repo.DeleteAsync(...))  // ✅ "on the repo, when DeleteAsync is called"
_mockRepo.Setup(r => r.DeleteAsync(...))        // ✗ what is r?
```

**Use `const` for test data values shared between Arrange and Assert.**
Magic numbers repeated in both sections invite typos and produce unreadable failure messages.

```csharp
// ✅
const int testUserId = 42;
_mockRepo.Setup(repo => repo.DeleteAsync(testUserId, ...)).ReturnsAsync(existingUser);
Assert.Equal(testUserId, result.User!.Id);

// ✗ same value in two places — easy to mismatch silently
_mockRepo.Setup(repo => repo.DeleteAsync(42, ...)).ReturnsAsync(existingUser);
Assert.Equal(42, result.User!.Id);
```

**Use `Assert.Same` to verify object identity, `Assert.Equivalent` for structural equality.**
`Assert.Equal` on a class without `Equals` overridden is reference equality — same as
`Assert.Same`. Be explicit about which you mean.

```csharp
Assert.Same(existingUser, result.User);       // same reference — resolver returned the repo object
Assert.Equivalent(existingUser, result.User); // field-by-field — different instance, same values
```

---

## Architecture Notes

- Raw `HttpClient` GraphQL calls in Blazor pages are **temporary** — StrawberryShake
  typed client codegen is planned. Do not flag these as bugs; flag anything that will make
  the SS migration harder (e.g., DTOs that don't structurally mirror the schema).
- Educational comments throughout the codebase are intentional. Do not suggest removing
  them as part of cleanup.
- `schema.graphql` is auto-exported by an MSBuild `AfterBuild` target (Debug only).
  Do not hand-edit it.
- Payload `Errors` lists support multiple errors by design, but current resolvers only
  produce one error per failure path. When multi-error cases are added: order is
  best-effort (most important first) but not guaranteed — tests and clients must use
  `Assert.Contains` + count check rather than `Assert.Collection` for those cases.
  Longer term, each resolver should declare its own error type rather than sharing
  across resolvers, to make the possible error set explicit at the type level (tracked
  in a future issue).
