using System.Security.Claims;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Auth;

public record UserDto(Guid Id, string Email);
public record LoginInput(string? Email, string? Password);
public record GoogleSignInInput(string? Credential);

// ───────────── Commands / queries ─────────────

/// <summary>Checks the credentials. Returns the user, or null when the email or password is wrong.</summary>
public record LoginCommand(string Email, string Password) : ICommand<UserDto?>;

public record GetUserQuery(Guid Id) : IQuery<UserDto?>;

/// <summary>
/// Signs in with a Google ID token from the "Continue with Google" button. A Gmail address seen for the first
/// time gets a new, empty account; an existing account with the same verified address is linked to Google.
/// Returns null when the token isn't a valid Google sign-in for this app.
/// </summary>
public record GoogleSignInCommand(string Credential) : ICommand<UserDto?>;

/// <summary>The verified facts we use from a Google ID token.</summary>
public record GoogleIdentity(string Subject, string Email, bool EmailVerified);

public interface IGoogleTokenValidator
{
    bool IsConfigured { get; }
    string? ClientId { get; }
    /// <summary>Checks Google's signature, expiry, issuer and that the token was issued for this app. Null if invalid.</summary>
    Task<GoogleIdentity?> ValidateAsync(string idToken);
}

public sealed class GoogleTokenValidator(IConfiguration config) : IGoogleTokenValidator
{
    public string? ClientId => config["Google:ClientId"] is { Length: > 0 } id ? id.Trim() : null;
    public bool IsConfigured => ClientId is not null;

    public async Task<GoogleIdentity?> ValidateAsync(string idToken)
    {
        if (ClientId is null) return null;
        try
        {
            var p = await Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync(idToken,
                new Google.Apis.Auth.GoogleJsonWebSignature.ValidationSettings { Audience = [ClientId] });
            return new GoogleIdentity(p.Subject, p.Email ?? "", p.EmailVerified);
        }
        catch (Google.Apis.Auth.InvalidJwtException)
        {
            return null;
        }
    }
}

public class GoogleSignInHandler(LendingDbContext db, IGoogleTokenValidator google, ILogger<GoogleSignInHandler> logger)
    : ICommandHandler<GoogleSignInCommand, UserDto?>
{
    public async Task<UserDto?> Handle(GoogleSignInCommand c, CancellationToken ct)
    {
        if (!google.IsConfigured)
            throw Invalid("Google sign-in isn't set up yet (Google:ClientId is missing).");

        var id = await google.ValidateAsync(c.Credential);
        if (id is null) return null;

        var email = id.Email.Trim().ToLowerInvariant();
        if (!id.EmailVerified || !email.EndsWith("@gmail.com", StringComparison.Ordinal))
            throw Invalid("Use a Gmail account (name@gmail.com) to sign in.");

        // Prefer the stable Google id; fall back to the address to link accounts created before Google sign-in.
        var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSubject == id.Subject, ct)
                   ?? await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            user = new AppUser { Email = email, GoogleSubject = id.Subject };
            db.Users.Add(user);
            logger.LogInformation("New account {Email} created with Google sign-in.", email);
        }
        else if (user.GoogleSubject is null)
        {
            user.GoogleSubject = id.Subject;
            logger.LogInformation("Linked Google sign-in to existing account {Email}.", user.Email);
        }
        else if (user.GoogleSubject != id.Subject)
        {
            throw Invalid("This email is already linked to a different Google account.");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // first sign-in raced with another for the same address
        {
            throw Invalid("Please try signing in again.");
        }
        return new UserDto(user.Id, user.Email);
    }

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["google"] = [message] });
}


public class LoginHandler(LendingDbContext db, IPasswordHasher<AppUser> hasher) : ICommandHandler<LoginCommand, UserDto?>
{
    // Verifying against a throwaway hash when the email is unknown keeps the response time the same,
    // so timing doesn't reveal which emails have accounts.
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString());

    public async Task<UserDto?> Handle(LoginCommand c, CancellationToken ct)
    {
        var email = c.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        // Google-only accounts have no password, so they can't sign in this way.
        if (user is { PasswordHash.Length: 0 }) user = null;
        var result = hasher.VerifyHashedPassword(user ?? new AppUser(), user?.PasswordHash ?? DummyHash, c.Password);
        if (user is null || result == PasswordVerificationResult.Failed) return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, c.Password);
            await db.SaveChangesAsync(ct);
        }
        return new UserDto(user.Id, user.Email);
    }
}

public class GetUserHandler(LendingDbContext db) : IQueryHandler<GetUserQuery, UserDto?>
{
    public Task<UserDto?> Handle(GetUserQuery q, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == q.Id).Select(u => new UserDto(u.Id, u.Email)).FirstOrDefaultAsync(ct);
}

// ───────────── Setup ─────────────

public static class AuthSetup
{
    public const string LoginRateLimit = "login";

    public static void AddAppAuth(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
        services.AddSingleton<IGoogleTokenValidator, GoogleTokenValidator>();
        services.AddAuthorization();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "lendingapp.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict; // blocks cross-site requests from carrying the session
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                // This is an API: answer 401/403 instead of redirecting to a login URL.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimit, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
        });
    }

    /// <summary>
    /// Creates the first admin when there are no users yet, from configuration (Admin:Email / Admin:Password).
    /// Keep those in user-secrets or environment variables, never in appsettings.json. Once the account exists
    /// the settings are ignored and can be removed.
    /// </summary>
    public static async Task EnsureAdminAsync(LendingDbContext db, IConfiguration config, IPasswordHasher<AppUser> hasher, ILogger logger)
    {
        if (await db.Users.AnyAsync()) return;

        var email = config["Admin:Email"]?.Trim().ToLowerInvariant();
        var password = config["Admin:Password"];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            logger.LogWarning("No users exist and Admin:Email / Admin:Password aren't configured, so nobody can sign in. " +
                              "Set them with `dotnet user-secrets` and restart.");
            return;
        }

        var admin = new AppUser { Email = email };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        logger.LogInformation("Created admin account {Email}.", email);
    }
}

// ───────────── Endpoints ─────────────

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginInput input, IDispatcher d, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || string.IsNullOrEmpty(input.Password))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Enter your email and password."] });

            var user = await d.Send(new LoginCommand(input.Email, input.Password), ct);
            if (user is null)
                return Results.Problem("Incorrect email or password.", statusCode: StatusCodes.Status401Unauthorized);

            await SignIn(http, user);
            return Results.Ok(user);
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginRateLimit);

        // What the sign-in page needs to show the Google button. The client ID is public by design.
        g.MapGet("/config", (IGoogleTokenValidator google) => Results.Ok(new { googleClientId = google.ClientId }))
            .AllowAnonymous();

        // Sign in or create an account with Google. Shares the login rate limit.
        g.MapPost("/google", async (GoogleSignInInput input, IDispatcher d, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Credential))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["google"] = ["Missing Google credential."] });

            var user = await d.Send(new GoogleSignInCommand(input.Credential), ct);
            if (user is null)
                return Results.Problem("Google sign-in failed. Please try again.", statusCode: StatusCodes.Status401Unauthorized);

            await SignIn(http, user);
            return Results.Ok(user);
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginRateLimit);

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (ClaimsPrincipal principal, IDispatcher d, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
            var user = await d.Query(new GetUserQuery(id), ct);
            return user is null ? Results.Unauthorized() : Results.Ok(user);
        }).RequireAuthorization();
    }

    private static Task SignIn(HttpContext http, UserDto user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
