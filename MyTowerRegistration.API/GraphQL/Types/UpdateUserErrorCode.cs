namespace MyTowerRegistration.API.GraphQL.Types;

public enum UpdateUserErrorCode
{
    UserNotFound,
    UnauthorizedAccess,  // reserved — auth not yet implemented
    UserNotEditable,     // reserved — for system/immutable users
    UsernameTaken,
    EmailTaken,
    InvalidEmail,
    InvalidPassword,
    InvalidUsername
}
