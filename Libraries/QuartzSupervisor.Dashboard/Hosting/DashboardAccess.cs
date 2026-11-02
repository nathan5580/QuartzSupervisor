using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Hosting;

internal sealed class DashboardMapping
{
    public Func<IEnumerable<Endpoint>>? Endpoints { get; set; }
}

internal sealed class DashboardEndpointMetadata { }

internal sealed class DashboardCircuitIdentity(IHttpContextAccessor http) : CircuitHandler
{
    public bool IsCircuit { get; private set; }
    public DateTimeOffset? ExpiresUtc { get; private set; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        IsCircuit = true;
        CaptureExpiration();
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CaptureExpiration();
        return Task.CompletedTask;
    }

    private void CaptureExpiration()
    {
        var expires = http.HttpContext?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        if (expires is not null) ExpiresUtc = expires;
    }
}

internal sealed class DashboardAccess(
    QuartzSupervisorOptions options,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    IAuthorizationPolicyProvider policies,
    IHttpContextAccessor http,
    DashboardCircuitIdentity circuit,
    DashboardMapping mapping) : ISchedulerDashboardAccess
{
    public async ValueTask<string> AuthorizeAsync(string schedulerName, DashboardPermission permission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ClaimsPrincipal user;
        if (circuit.IsCircuit)
            user = (await authentication.GetAuthenticationStateAsync()).User;
        else if (http.HttpContext is { } context)
            user = context.User;
        else
        {
            // Programmatic scopes have no browser identity; anonymous access must be explicitly enabled.
            try { user = (await authentication.GetAuthenticationStateAsync()).User; }
            catch (InvalidOperationException) { user = new ClaimsPrincipal(new ClaimsIdentity()); }
        }

        if (options.ReadOnly && permission != DashboardPermission.View)
            throw new UnauthorizedAccessException("This dashboard is read-only.");
        var expiry = circuit.ExpiresUtc ?? http.HttpContext?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        var tokenExpired = long.TryParse(user.FindFirst("exp")?.Value, out var expirySeconds)
            && expirySeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (tokenExpired || expiry <= DateTimeOffset.UtcNow || (options.RequireAuthentication && user.Identity?.IsAuthenticated != true))
            throw new UnauthorizedAccessException("Sign in again to use this dashboard.");

        // Read built endpoint metadata, not an early snapshot: includes host RequireAuthorization
        // conventions, inline policies and parent route-group policies after endpoint construction.
        var endpoint = mapping.Endpoints?.Invoke().FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<DashboardEndpointMetadata>() is not null
            && endpoint is RouteEndpoint route && route.RoutePattern.RawText?.EndsWith("/_blazor", StringComparison.Ordinal) == true);
        if (endpoint is not null)
        {
            var policy = await AuthorizationPolicy.CombineAsync(policies,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            if (policy is not null && !(await authorization.AuthorizeAsync(user, schedulerName, policy)).Succeeded)
                throw new UnauthorizedAccessException("The host dashboard access policy denied this operation.");
        }

        await RequirePolicyAsync(options.ViewPolicy);
        if (permission != DashboardPermission.View) await RequirePolicyAsync(options.OperatePolicy);
        if (permission == DashboardPermission.Administer) await RequirePolicyAsync(options.AdministerPolicy);
        return user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";

        async Task RequirePolicyAsync(string? policy)
        {
            ct.ThrowIfCancellationRequested();
            if (policy is not null && !(await authorization.AuthorizeAsync(user, schedulerName, policy)).Succeeded)
                throw new UnauthorizedAccessException($"Permission {permission} was denied for scheduler '{schedulerName}'.");
        }
    }
}
