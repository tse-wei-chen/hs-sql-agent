using Microsoft.AspNetCore.Http;

namespace HsSqlAgent.Server.Authorization;

/// <summary>
/// Authorizes HsSqlAgent canonical permission keys without requiring HsSqlAgent to own the host application's
/// ASP.NET Core authorization policy provider.
/// </summary>
public interface IHsSqlAgentPermissionAuthorizer
{
    /// <summary>
    /// Authentication scheme used when the HsSqlAgent permission filter must challenge or forbid.
    /// Null delegates challenge/forbid behavior to the host application's configured defaults.
    /// </summary>
    string? AuthenticationScheme { get; }

    /// <summary>
    /// Returns true when the current request is authorized for at least one of the supplied canonical permission keys.
    /// </summary>
    ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resource passed to host ASP.NET Core authorization policies so host handlers can optionally inspect the
/// HsSqlAgent canonical permission keys being requested.
/// </summary>
public sealed record HsSqlAgentPermissionResource(
    HttpContext HttpContext,
    IReadOnlyCollection<string> Permissions);
