// =============================================================================
// SUPERSEDED — Raw GraphQL HTTP response shapes (kept as a before/after exhibit)
//
// These types were the manual scaffolding that powered Users.razor and
// Register.razor before StrawberryShake codegen replaced them.
//
// WHAT THEY SHOW
// ──────────────
// The problem these solved: HttpClient.PostAsJsonAsync returns raw JSON. To get
// a typed result you need a wrapper record. But you have to keep the C# property
// names in sync with the GraphQL JSON keys manually — System.Text.Json matches
// by name (case-insensitive), so a drift causes silent null deserialization.
//
// WHAT REPLACED THEM
// ──────────────────
// StrawberryShake generates these from the schema — they can never drift.
// The generated types live in obj/berry/ (see LEARNING.md § StrawberryShake).
//   GraphQLResponse<T>   →  IOperationResult<T>         (in StrawberryShake)
//   GraphQLError         →  IClientError                 (in StrawberryShake)
//   UsersData, UserDto   →  IGetUsersResult, IGetUsers_Users  (generated)
//   DeleteUserData etc.  →  IDeleteUserResult, IDeleteUser_DeleteUser (generated)
//
// DO NOT USE — [Obsolete(error: true)] below is the C# equivalent of a
// "do not import" pragma. Any call site that references these types will fail
// to compile. The namespace is also no longer imported in _Imports.razor.
// =============================================================================

namespace MyTowerRegistration.Admin.GraphQL.Models;

/// <summary>
/// Envelope for a raw GraphQL HTTP response.
/// A GraphQL server returns HTTP 200 for most responses — including many error
/// conditions. The HTTP status covers transport failures; application-level
/// errors live in the <see cref="Errors"/> collection.
/// </summary>
[System.Obsolete("Superseded by StrawberryShake IOperationResult<T>. Do not use.", error: true)]
internal record GraphQLResponse<T>(T? Data, List<GraphQLError>? Errors);

/// <summary>
/// A single entry in the top-level GraphQL errors array.
/// These are distinct from error-as-data payload errors (e.g. UserError) —
/// they represent resolver exceptions, auth failures, or schema violations.
/// </summary>
[System.Obsolete("Superseded by StrawberryShake IClientError. Do not use.", error: true)]
internal record GraphQLError(string Message);
