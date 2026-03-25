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
        _mockRepo.Setup(r => r.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(r => r.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(r => r.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .ReturnsAsync((User u) => { u.Id = 1; return u; });

        var input = new RegisterUserInput("testuser", "test@example.com", "Password123");

        // Act — call the mutation
        var result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(result.User);
        Assert.Null(result.Errors);
        Assert.Equal("testuser", result.User!.Username);
        Assert.Equal("test@example.com", result.User.Email);

        // Verify the repository was called
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<User>(), CancellationToken.None), Times.Once);
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
        _mockRepo.Setup(r => r.UsernameExistsAsync("taken", CancellationToken.None))
            .ReturnsAsync(true);
        _mockRepo.Setup(r => r.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        
        // Input with the taken username
        var input = new RegisterUserInput("taken", "new@example.com", "Password123");

        // Act
        var result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        // Assert
        Assert.Null(result.User);
        Assert.NotNull(result.Errors);
        Assert.Single(result.Errors!);
        Assert.Equal(CreateUserErrorCode.UsernameTaken, result.Errors![0].Code);

        // Verify AddAsync was NEVER called (we short-circuited)
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<User>(), CancellationToken.None), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST 3: Duplicate email
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithDuplicateEmail_ReturnsError()
    {
        _mockRepo.Setup(r => r.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(r => r.EmailExistsAsync("taken@example.com", CancellationToken.None))
            .ReturnsAsync(true);

        var input = new RegisterUserInput("newuser", "taken@example.com", "Password123");

        var result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.NotNull(result.Errors);
        Assert.Equal(CreateUserErrorCode.EmailTaken, result.Errors![0].Code);
    }

    // -------------------------------------------------------------------------
    // TEST 4: Invalid email format
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_WithInvalidEmail_ReturnsError()
    {
        var input = new RegisterUserInput("user", "not-an-email", "Password123");

        var result = await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.Null(result.User);
        Assert.NotNull(result.Errors);
        Assert.Equal(CreateUserErrorCode.InvalidEmail, result.Errors![0].Code);

        // No DB calls should happen for a validation failure
        _mockRepo.Verify(r => r.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None), Times.Never);
    }

    // -------------------------------------------------------------------------
    // TEST 5: Password is hashed (not stored in plaintext)
    // -------------------------------------------------------------------------
    // TODO: Implement this test
    //
    [Fact]
    public async Task RegisterUser_PasswordIsHashed_NotStoredPlaintext()
    {
        _mockRepo.Setup(r => r.UsernameExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);
        _mockRepo.Setup(r => r.EmailExistsAsync(It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(false);

        User? capturedUser = null;
        _mockRepo.Setup(r => r.AddAsync(It.IsAny<User>(), CancellationToken.None))
            .Callback<User>(u => capturedUser = u)  // Capture the entity
            .ReturnsAsync((User u) => u);

        var input = new RegisterUserInput("user", "user@test.com", "MySecret");

        await _mutations.RegisterUser(input, _mockRepo.Object, CancellationToken.None);

        Assert.NotNull(capturedUser);
        Assert.NotEqual("MySecret", capturedUser!.PasswordHash);  // Not plaintext
        Assert.NotEmpty(capturedUser.PasswordHash);                // Not empty
    }
}
