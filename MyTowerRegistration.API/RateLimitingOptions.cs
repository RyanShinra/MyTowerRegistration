using System.ComponentModel.DataAnnotations;

namespace MyTowerRegistration.API;

// Typed configuration for the rate limiter, bound from the "RateLimiting" section
// of appsettings.json (or env vars: RateLimiting__PermitLimit, RateLimiting__WindowSeconds).
//
// Using a typed options class instead of raw GetValue<int> calls has three advantages:
//   1. Defaults live in exactly one place (the property initializers below).
//   2. ValidateDataAnnotations() + ValidateOnStart() in Program.cs crash the app at
//      startup if either value is invalid — no silent misconfiguration in production.
//   3. Any future code that needs these values can inject IOptions<RateLimitingOptions>
//      rather than referencing magic strings.
public class RateLimitingOptions
{
    [Range(1, int.MaxValue, ErrorMessage = "RateLimiting:PermitLimit must be at least 1.")]
    public int PermitLimit   { get; init; } = 30;

    [Range(1, int.MaxValue, ErrorMessage = "RateLimiting:WindowSeconds must be at least 1.")]
    public int WindowSeconds { get; init; } = 60;
}
