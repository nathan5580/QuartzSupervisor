using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using QuartzSupervisor.Dashboard;
using Xunit;

namespace QuartzSupervisor.Tests;

public sealed class DashboardAuthorizationTests
{
    [Fact]
    public async Task Dashboard_requires_authentication_without_protecting_host_api_routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartzSupervisorDashboard();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapQuartzSupervisorDashboard();
        await app.StartAsync();

        using var anonymousClient = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymousClient.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.GetAsync("/quartz-supervisor")).StatusCode);

        using var authenticatedClient = app.GetTestClient();
        authenticatedClient.DefaultRequestHeaders.Add("X-Test-User", "operator");
        var dashboard = await authenticatedClient.GetAsync("/quartz-supervisor");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("<h1>Overview</h1>", await dashboard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var userName))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName.ToString())], Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
