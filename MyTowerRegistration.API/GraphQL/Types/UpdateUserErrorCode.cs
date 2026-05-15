namespace MyTowerRegistration.API.GraphQL.Types;

public enum UpdateUserErrorCode
{
    UserNotFound,
    UnauthorizedAccess,
    UserNotEditable,
    UsernameTaken,
    EmailTaken,
    InvalidEmail,
    InvalidPassword,
    InvalidUsername
}
