using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.API.GraphQL.Types;

public record DeleteUserPayload(User? User, IReadOnlyList<DeleteUserError>? Errors);
