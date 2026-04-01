// =============================================================================
// TEMPORARY — Raw GraphQL HTTP response shapes
//
// These records exist only while the pages use raw HttpClient POST calls.
// Once StrawberryShake codegen runs (see .graphqlrc.json), it generates
// strongly-typed result types for every operation and these go away entirely.
//
// Why they live here and not inside a page's @code block:
//   GraphQLResponse<T> and GraphQLError are infrastructure — not specific to
//   users or registration. Keeping one copy prevents the two pages drifting
//   apart if we add fields (extensions, path, locations) during debugging.
// =============================================================================

namespace MyTowerRegistration.Admin.GraphQL.Models;

/// <summary>
/// Envelope for a raw GraphQL HTTP response.
/// A GraphQL server returns HTTP 200 for most responses — including many error
/// conditions. The HTTP status covers transport failures; application-level
/// errors live in the <see cref="Errors"/> collection.
/// </summary>
internal record GraphQLResponse<T>(T? Data, List<GraphQLError>? Errors);

/// <summary>
/// A single entry in the top-level GraphQL errors array.
/// These are distinct from error-as-data payload errors (e.g. UserError) —
/// they represent resolver exceptions, auth failures, or schema violations.
/// </summary>
internal record GraphQLError(string Message);
