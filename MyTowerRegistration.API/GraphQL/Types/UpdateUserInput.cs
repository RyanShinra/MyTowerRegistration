namespace MyTowerRegistration.API.GraphQL.Types;

/// <summary>
/// A Record to use when updating a user's info. Only non-null fields will be updated.
/// </summary>
/// <param name="Username">Update the Username if non-null</param>
/// <param name="Email">Update the Email if non-null</param>
/// <param name="Password">Update the Password if non-null</param>
public record UpdateUserInput(int Id, string? Username, string? Email, string? Password);
