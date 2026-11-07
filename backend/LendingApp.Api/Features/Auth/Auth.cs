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

// ───────────── Commands / queries ─────────────

/// <summary>Checks the credentials. Returns the user, or null when the email or password is wrong.</summary>
public record LoginCommand(string Email, string Password) : ICommand<UserDto?>;

public record GetUserQuery(Guid Id) : IQuery<UserDto?>;

/// <summary>Creates an account for a Gmail address. The new user starts with no data of their own.</summary>
public record RegisterCommand(string? Email, string? Password) : ICommand<UserDto>;

public partial class RegisterHandler(LendingDbContext db, IPasswordHasher<AppUser> hasher) : ICommandHandler<RegisterCommand, UserDto>
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9](?:[a-z0-9.+_-]{0,62}[a-z0-9])?@gmail\.com$")]
    private static partial System.Text.RegularExpressions.Regex GmailAddress();

    public async Task<UserDto> Handle(RegisterCommand c, CancellationToken ct)
    {
        var email = c.Email?.Trim().ToLowerInvariant() ?? "";
        var password = c.Password ?? "";
        var isGmail = GmailAddress().IsMatch(email);
        var taken = isGmail && await db.Users.AnyAsync(u => u.Email == email, ct);

        new Rules()
            .Require(isGmail, "email", "Use a Gmail address (name@gmail.com).")
            .Require(!taken, "email", "An account with this email already exists. Sign in instead.")
            .Require(password.Length >= 8, "password", "Use at least 8 characters.")
            .Require(password.Length <= 128, "password", "Use at most 128 characters.")
            .Require(password.Any(char.IsLetter) && password.Any(char.IsDigit), "password", "Include at least one letter and one number.")
            .ThrowIfInvalid();

        var user = new AppUser { Email = email };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // two sign-ups for the same address at once: the unique index catches it
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["email"] = ["An account with this email already exists. Sign in instead."] });
        }
        return new UserDto(user.Id, user.Email);
    }
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

        // Shares the login rate limit, so sign-up can't be used to spam accounts or probe emails quickly.
        g.MapPost("/register", async (LoginInput input, IDispatcher d, HttpContext http, CancellationToken ct) =>
        {
            var user = await d.Send(new RegisterCommand(input.Email, input.Password), ct);
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
