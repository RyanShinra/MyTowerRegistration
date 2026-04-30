using MyTowerRegistration.Data.Models;

namespace MyTowerRegistration.API.GraphQL.Types;

public record UpdateUserPayload(User? User, IReadOnlyList<UpdateUserError>? Errors);
