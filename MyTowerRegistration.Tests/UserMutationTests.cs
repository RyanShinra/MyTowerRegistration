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

public class UserMutationTests : IDisposable
{
    // Shared setup — create the mock repository and mutation instance once.
    // In xUnit, the constructor runs before EACH test (like beforeEach in Jest).

    private readonly Mock<IUserRepository> _mockRepo;
    private readonly UserMutations _mutations;

    // Shared baseline user — reused across tests that just need a valid existing user.
    // Use _testUser.Id, .Username etc. in assertions so values stay in sync automatically.
    private readonly User _testUser = new() {
        Id = 42,
        Username = "targetUser",
        Email = "target@example.com",
        PasswordHash = "myHashedPW"
    };

    public UserMutationTests()
    {
        _mockRepo = new Mock<IUserRepository>();
        _mutations = new UserMutations();
    }

    // Runs after every test — mutations should never call read-only fetch methods.
    // Any of these firing is a sign of a TOCTOU regression or a logic error.
    public void Dispose()
    {
        _mockRepo.Verify(repo => repo.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepo.Verify(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockRepo.Verify(repo => repo.GetByIdsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: RegisterUser — success path
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RegisterUser_WithValidInput_ReturnsUserAndNoErrors()
    {
        // Arrange — set up the mock to simulate "no conflicts"
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .ReturnsAsync((User u, CancellationToken _) => { u.Id = 1; return u; });

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
    // TEST: RegisterUser — duplicate username
    // -------------------------------------------------------------------------
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
    // TEST: RegisterUser — duplicate email
    // -------------------------------------------------------------------------
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
    // TEST: RegisterUser — invalid email format
    // -------------------------------------------------------------------------
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

        // No DB calls should happen for a format-check failure — both existence checks must be skipped
        _mockRepo.Verify(repo => repo.UsernameExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepo.Verify(repo => repo.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: DeleteUser — success path
    // -------------------------------------------------------------------------
    // DeleteAsync returns the deleted User → payload should have that User, no errors.
    //
    [Fact]
    public async Task DeleteUser_WithExistingId_ReturnsDeletedUserAndNoErrors()
    {
        // Arrange: The existing user will be returned by DeleteAsync (we will compare the fields below)
        _mockRepo.Setup(repo => repo.DeleteAsync(_testUser.Id, CancellationToken.None))
            .ReturnsAsync(_testUser);

        // Act: Delete the user
        DeleteUserPayload result = await _mutations.DeleteUser(_testUser.Id, _mockRepo.Object, CancellationToken.None);

        // Assert — resolver must return the exact repo object, not a reconstruction
        Assert.Null(result.Errors);
        Assert.Same(_testUser, result.User);
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
    // The resolver must call DeleteAsync exactly once. Fetch-then-delete is the
    // two-call anti-pattern — Dispose() enforces that GetByIdAsync is never called.
    //
    [Fact]
    public async Task DeleteUser_OnlyCallsDeleteAsync_Once()
    {
        // Arrange: _testUser is notionally already in the DB — DeleteAsync returns it on success.
        _mockRepo.Setup(repo => repo.DeleteAsync(_testUser.Id, CancellationToken.None))
            .ReturnsAsync(_testUser);

        // Act — same call as the success test
        DeleteUserPayload result = await _mutations.DeleteUser(_testUser.Id, _mockRepo.Object, CancellationToken.None);
        _mockRepo.Verify(repo => repo.DeleteAsync(_testUser.Id, CancellationToken.None), Times.Once);

        // That we got back the right kind of response
        Assert.NotNull(result);
        Assert.NotNull(result.User);
        Assert.Null(result.Errors);
        Assert.Same(_testUser, result.User);
    }

    // -------------------------------------------------------------------------
    // TEST: RegisterUser — password is hashed (not stored in plaintext)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RegisterUser_PasswordIsHashed_NotStoredPlaintext()
    {
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);

        User? capturedUser = null;
        _mockRepo.Setup(repo => repo.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .Callback<User, CancellationToken>((user, _) => capturedUser = user)
            .ReturnsAsync((User u, CancellationToken _) => u);

        var input = new RegisterUserInput("user", "user@test.com", "MySecret");

        await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.NotNull(capturedUser);
        Assert.NotEqual("MySecret", capturedUser!.PasswordHash);  // Not plaintext
        Assert.NotEmpty(capturedUser.PasswordHash);                // Not empty
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — success path
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithValidInput_ReturnsUpdatedUserAndNoErrors()
    {
        // Arrange: _testUser is notionally already in the DB — UpdateAsync returns it on success.
        _mockRepo.Setup(repo => repo.UpdateAsync(_testUser.Id, _testUser.Username, _testUser.Email, null, CancellationToken.None))
            .ReturnsAsync(_testUser);
        _mockRepo.Setup(repo => repo.UsernameExistsAsync(_testUser.Username, CancellationToken.None)).ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.EmailExistsAsync(_testUser.Email, CancellationToken.None)).ReturnsAsync(false);

        // Act: Run the update
        UpdateUserInput input = new UpdateUserInput(_testUser.Id, _testUser.Username, _testUser.Email, null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        // Assert — same reference (no copy made) and fields unmodified (resolver didn't mutate before returning)
        Assert.Null(result.Errors);
        Assert.Same(_testUser, result.User);
        Assert.Equivalent(_testUser, result.User);

        _mockRepo.Verify(repo => repo.UpdateAsync(_testUser.Id, _testUser.Username, _testUser.Email, null, CancellationToken.None),
            Times.Once);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — user not found
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithNonExistentId_ReturnsUserNotFoundError()
    {
        const int missingId = 99;

        _mockRepo.Setup(repo => repo.UpdateAsync(missingId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), CancellationToken.None))
            .ReturnsAsync((User?)null);

        UpdateUserInput input = new UpdateUserInput(missingId, null, null, null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(UpdateUserErrorCode.UserNotFound, errorZero.Code));

        _mockRepo.Verify(repo => repo.UpdateAsync(missingId, null, null, null, CancellationToken.None), Times.Once);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — username already taken
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithDuplicateUsername_ReturnsUsernameTakenError()
    {
        const string takenUsername = "takenuser";

        _mockRepo.Setup(repo => repo.UsernameExistsAsync(takenUsername, CancellationToken.None))
            .ReturnsAsync(true);

        UpdateUserInput input = new UpdateUserInput(_testUser.Id, takenUsername, null, null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(UpdateUserErrorCode.UsernameTaken, errorZero.Code));

        _mockRepo.Verify(repo => repo.UpdateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — email already taken
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithDuplicateEmail_ReturnsEmailTakenError()
    {
        const string takenEmail = "taken@example.com";

        _mockRepo.Setup(repo => repo.EmailExistsAsync(takenEmail, CancellationToken.None))
            .ReturnsAsync(true);

        UpdateUserInput input = new UpdateUserInput(_testUser.Id, null, takenEmail, null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(UpdateUserErrorCode.EmailTaken, errorZero.Code));

        _mockRepo.Verify(repo => repo.UpdateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — invalid email format
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithInvalidEmail_ReturnsInvalidEmailError()
    {
        UpdateUserInput input = new UpdateUserInput(_testUser.Id, null, "not-an-email", null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.Collection(result.Errors,
            errorZero => Assert.Equal(UpdateUserErrorCode.InvalidEmail, errorZero.Code));

        _mockRepo.Verify(repo => repo.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepo.Verify(repo => repo.UpdateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — partial update (only some fields provided)
    // When providing only an email to update, only the email is validated against the DB
    // Also, since no username is provided, Assert that the username is not checked against the DB
    // Also, also, confirm that the repo update method is called correctly with only user ID and new Email
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_WithPartialInput_OnlyValidatesAndUpdatesProvidedFields()
    {
        const string newEmail = "new@example.com";

        _mockRepo.Setup(repo => repo.EmailExistsAsync(newEmail, CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(repo => repo.UpdateAsync(_testUser.Id, null, newEmail, null, CancellationToken.None))
            .ReturnsAsync(_testUser);

        UpdateUserInput input = new UpdateUserInput(_testUser.Id, null, newEmail, null);

        UpdateUserPayload result = await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.Errors);
        Assert.NotNull(result.User);
        Assert.Same(_testUser, result.User);

        _mockRepo.Verify(repo => repo.EmailExistsAsync(newEmail, CancellationToken.None), Times.Once);
        _mockRepo.Verify(repo => repo.UsernameExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRepo.Verify(repo => repo.UpdateAsync(_testUser.Id, null, newEmail, null, CancellationToken.None), Times.Once);
    }

    // -------------------------------------------------------------------------
    // TEST: UpdateUser — password is hashed before being sent to the repo
    // -------------------------------------------------------------------------
    [Fact]
    public async Task UpdateUser_PasswordIsHashed_NotForwardedAsPlaintext()
    {
        const string plainPassword = "MyNewSecret";
        string? capturedHash = null;

        _mockRepo.Setup(repo => repo.UpdateAsync(_testUser.Id, null, null, It.IsAny<string?>(), CancellationToken.None))
            .Callback<int, string?, string?, string?, CancellationToken>((_, _, _, hash, _) => capturedHash = hash)
            .ReturnsAsync(_testUser);

        UpdateUserInput input = new UpdateUserInput(_testUser.Id, null, null, plainPassword);

        await _mutations.UpdateUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.NotNull(capturedHash);
        Assert.NotEqual(plainPassword, capturedHash);
        Assert.NotEmpty(capturedHash!);
    }
}
