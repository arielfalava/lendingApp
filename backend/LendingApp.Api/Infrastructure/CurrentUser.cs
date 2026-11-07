using System.Security.Claims;

namespace LendingApp.Api.Infrastructure;

/// <summary>Who is making the current request. Null outside a signed-in request (e.g. at startup).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
