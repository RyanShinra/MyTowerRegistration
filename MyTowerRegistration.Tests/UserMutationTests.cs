// =============================================================================
// IMPLEMENT LAST — After everything else compiles and runs.
//
// These tests verify your UserMutations logic WITHOUT hitting a real database.
// We use Moq to create a fake IUserRepository, then call the mutation method
// directly with that fake.
//
// Compare to TypeScript/Jest:
//   const mockRepo = { usernameExistsAsync: jest.fn().mockResolvedValue(false), ... };
//   const result = await registerUser(input, mockRepo);
//   expect(result.user).toBeDefined();
//
// Same pattern here, just with C# syntax and Moq instead of jest.fn().
//
// Test naming convention: MethodName_Scenario_ExpectedResult
// =============================================================================

using Moq;
using MyTowerRegistration.API.GraphQL.Mutations;
using MyTowerRegistration.API.GraphQL.Types;
using MyTowerRegistration.Data.Models;
using MyTowerRegistration.Data.Repositories;

namespace MyTowerRegistration.Tests;

public class UserMutationTests
{
    // Shared setup — create the mock repository and mutation instance once.
    // In xUnit, the constructor runs before EACH test (like beforeEach in Jest).

    private readonly Mock<IUserRepository> _mockRepo;
    private readonly UserMutations _mutations;

    public UserMutationTests()
    {
        _mockRepo = new Mock<IUserRepository>();
        _mutations = new UserMutations();
    }

    // -------------------------------------------------------------------------
    // TEST 1: Successful registration
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithValidInput_ReturnsUserAndNoErrors()
    {
        // Arrange — set up the mock to simulate "no conflicts"
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .ReturnsAsync((User u) => { u.Id = 1; return u; });

        var input = new RegisterUserInput("testuser", "test@example.com", "Password123");

        // Act — call the mutation
        RegisterUserPayload result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(result.User);
        Assert.Null(result.Errors);
        Assert.Equal("testuser", result.User!.Username);
        Assert.Equal("test@example.com", result.User.Email);

        // Verify the repository was called
        _mockRepo.Verify(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None), Times.Once);
    }

    // -------------------------------------------------------------------------
    // TEST 2: Duplicate username
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithDuplicateUsername_ReturnsError()
    {
        // Arrange — username already exists
        _mockRepo.Setup(repo => repo.UsernameExistsAsync("taken", CancellationToken.None))
            .ReturnsAsync(true);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);

        // Input with the taken username
        var input = new RegisterUserInput("taken", "new@example.com", "Password123");

        // Act
        RegisterUserPayload result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Assert
        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(CreateUserErrorCode.UsernameTaken, errorZero.Code));

        // Verify AddAsync was NEVER called (we short-circuited)
        _mockRepo.Verify(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST 3: Duplicate email
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithDuplicateEmail_ReturnsError()
    {
        // Arrange: Attempt to create a new user [username doesn't exist, return false]
        // but with an existing email address [exists returns true]
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync("taken@example.com", CancellationToken.None))
            .ReturnsAsync(true);

        // Create a new user, only the email address matters
        var input = new RegisterUserInput("newuser", "taken@example.com", "Password123");

        RegisterUserPayload result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Error returns from GQL land with no user (couldn't create), 
        // and one error of the EmailTaken
        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(CreateUserErrorCode.EmailTaken, errorZero.Code));
    }

    // -------------------------------------------------------------------------
    // TEST 4: Invalid email format
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithInvalidEmail_ReturnsError()
    {
        // Create a user with a valid username and password (they will be checked, too)
        var input = new RegisterUserInput("user", "not-an-email", "Password123");

        // Call the test function
        // (Implicitly, the bad email check should happen and bail out before trying to call into the DB).
        RegisterUserPayload result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Similar error pattern, null user, 1 error with correct error code
        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(CreateUserErrorCode.InvalidEmail, errorZero.Code));

        // No DB calls should happen for a validation failure
        _mockRepo.Verify(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: DeleteUser — success path
    // -------------------------------------------------------------------------
    // DeleteAsync returns the deleted User → payload should have that User, no errors.
    //
    [Fact]
    public async Task DeleteUser_WithExistingId_ReturnsDeletedUserAndNoErrors()
    {
        //User properties:
        const int testUserId = 42;
        const string testUsername = "targetUser";
        const string testEmail = "target@example.com";

        // Arrange: The existing user will be returned by DeleteAsync (we will compare the fields below)
        var existingUser = new User { Id = testUserId, Username = testUsername, Email = testEmail };
        _mockRepo.Setup(repo => repo.DeleteAsync(testUserId, CancellationToken.None))
            .ReturnsAsync(existingUser);

        // Act: Delete the user 
        DeleteUserPayload result = await _mutations.DeleteUser(testUserId, _mockRepo.Object, CancellationToken.None);

        // Assert — The user should be not null with matching fields, the error property should be null (i.e. deleted successfully)
        Assert.Null(result.Errors);
        Assert.NotNull(result.User);
        Assert.Equal(testUserId, result.User.Id);
        Assert.Equal(testUsername, result.User.Username);
        Assert.Equal(testEmail, result.User.Email);
    }

    // -------------------------------------------------------------------------
    // TEST: DeleteUser — user not found
    // -------------------------------------------------------------------------
    // DeleteAsync returns null (user never existed, or concurrent deletion) →
    // payload should have no User and a UserNotFound error.
    //
    [Fact]
    public async Task DeleteUser_WithNonExistentId_ReturnsUserNotFoundError()
    {
        //Delete User properties
        const int delUserId = 99;
        // Arrange: The database will return null user when called with `delUserId`
        _mockRepo.Setup(repo => repo.DeleteAsync(delUserId, CancellationToken.None))
            .ReturnsAsync((User?)null);

        // Act
        DeleteUserPayload result = await _mutations.DeleteUser(delUserId, _mockRepo.Object, CancellationToken.None);

        // Assert — verify result.User is null, result.Errors has one UserNotFound entry
        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(DeleteUserErrorCode.UserNotFound, errorZero.Code));
    }

    // -------------------------------------------------------------------------
    // TEST: DeleteUser — single repository call (TOCTOU guard)
    // -------------------------------------------------------------------------
    // The resolver must call DeleteAsync exactly once and must NOT call
    // GetByIdAsync at all. If someone reverts to the two-call pattern this
    // test will catch the regression.
    //
    [Fact]
    public async Task DeleteUser_OnlyCallsDeleteAsync_NeverCallsGetByIdAsync()
    {
        // Arrange: Create an existing user, notionally already in the DB. When you delete it, it is returned via the 'STL' convention
        var existingUser = new User { Id = 1, Username = "user", Email = "user@user.com" };
        _mockRepo.Setup(repo => repo.DeleteAsync(existingUser.Id, CancellationToken.None))
            .ReturnsAsync(existingUser);

        // Act — same call as the success test
        DeleteUserPayload result = await _mutations.DeleteUser(existingUser.Id, _mockRepo.Object, CancellationToken.None);
        // Assert: The mutation only called delete once on the DB, it never called the GetById (the old pattern which has the race condition)
        _mockRepo.Verify(repo => repo.DeleteAsync(existingUser.Id, CancellationToken.None), Times.Once);
        _mockRepo.Verify(repo => repo.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        
        // That we got back the right kind of response
        Assert.NotNull(result);
        Assert.NotNull(result.User);
        Assert.Null(result.Errors);
        Assert.Equal(existingUser, result.User);
    }

    // -------------------------------------------------------------------------
    // TEST 5: Password is hashed (not stored in plaintext)
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_PasswordIsHashed_NotStoredPlaintext()
    {
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);

        User? capturedUser = null;
        _mockRepo.Setup(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .Callback<User>(user => capturedUser = user)  // Capture the entity
            .ReturnsAsync((User u) => u);

        var input = new RegisterUserInput("user", "user@test.com", "MySecret");

        await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.NotNull(capturedUser);
        Assert.NotEqual("MySecret", capturedUser!.PasswordHash);  // Not plaintext
        Assert.NotEmpty(capturedUser.PasswordHash);                // Not empty
    }
}
