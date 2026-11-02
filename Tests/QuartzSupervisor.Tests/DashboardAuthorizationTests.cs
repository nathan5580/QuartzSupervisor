using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using QuartzSupervisor.Dashboard;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;
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
        foreach (var path in new[] { "/quartz-supervisor", "/quartz-supervisor/jobs", "/quartz-supervisor/triggers", "/quartz-supervisor/timeline" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.GetAsync(path)).StatusCode);

        using var authenticatedClient = app.GetTestClient();
        authenticatedClient.DefaultRequestHeaders.Add("X-Test-User", "operator");
        var dashboard = await authenticatedClient.GetAsync("/quartz-supervisor");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Quartz Supervisor", await dashboard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_accepts_a_host_named_authorization_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartzSupervisorDashboard();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("DashboardAdmin", policy => policy.RequireRole("DashboardAdmin")));
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapQuartzSupervisorDashboard().RequireAuthorization("DashboardAdmin");
        await app.StartAsync();

        using var readerClient = app.GetTestClient();
        readerClient.DefaultRequestHeaders.Add("X-Test-User", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/quartz-supervisor")).StatusCode);

        using var adminClient = app.GetTestClient();
        adminClient.DefaultRequestHeaders.Add("X-Test-User", "operator");
        adminClient.DefaultRequestHeaders.Add("X-Test-Dashboard-Admin", "true");
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/quartz-supervisor")).StatusCode);

        using var anonymousClient = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymousClient.GetAsync("/api/health")).StatusCode);
    }

    [Theory]
    [InlineData("/quartz-supervisor", "")]
    [InlineData("/operations/work", "")]
    [InlineData("/operations/work", "/proxy")]
    public async Task Anonymous_mapping_supports_custom_prefix_pathbase_and_a_host_component_app(string prefix, string pathBase)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddQuartzSupervisorDashboard(options =>
        {
            options.Path = prefix;
            options.RequireAuthentication = false;
            options.Title = "Embedded operations";
        });
        await using var app = builder.Build();
        if (pathBase.Length != 0) app.UsePathBase(pathBase);
        app.UseAntiforgery();
        app.MapRazorComponents<HostComponent>().AddInteractiveServerRenderMode();
        app.MapQuartzSupervisorDashboard();
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var page in new[] { "", "/jobs", "/triggers", "/timeline" })
        {
            var response = await client.GetAsync(pathBase + prefix + page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"<base href=\"{pathBase}{prefix}/\"", html, StringComparison.Ordinal);
            Assert.Contains($"src=\"{pathBase}/_framework/blazor.web.js\"", html, StringComparison.Ordinal);
            Assert.Contains($"src=\"{pathBase}/_content/QuartzSupervisor/dashboard-theme.js\"", html, StringComparison.Ordinal);
        }
        Assert.Contains("Host application", await client.GetStringAsync(pathBase + "/"), StringComparison.Ordinal);
        var negotiate = await client.PostAsync(pathBase + prefix + "/_blazor/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var hostNegotiate = await client.PostAsync(pathBase + "/_blazor/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, hostNegotiate.StatusCode);
        if (prefix != "/quartz-supervisor")
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(pathBase + "/quartz-supervisor")).StatusCode);
    }

    [Fact]
    public async Task Current_scoped_identity_and_host_mapping_policy_are_rechecked_for_commands()
    {
        var identity = new MutableAuthenticationState();
        identity.SetUser("operator", "DashboardAdmin");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddQuartzSupervisorDashboard();
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => identity);
        builder.Services.AddAuthorization(options => options.AddPolicy("DashboardAdmin", policy => policy.RequireRole("DashboardAdmin")));
        await using var app = builder.Build();
        app.UseAntiforgery();
        app.MapQuartzSupervisorDashboard().RequireAuthorization("DashboardAdmin");
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var scheduler = await scope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var commands = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardCommands>();
        await commands.StartSchedulerAsync(scheduler.SchedulerName, CancellationToken.None);
        identity.SetUser("reader");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.StandbySchedulerAsync(scheduler.SchedulerName, CancellationToken.None));
        Assert.Equal("Running", (await scheduler.GetStatus()).ToString());
        identity.SetUser("operator", "DashboardAdmin");
        await commands.StandbySchedulerAsync(scheduler.SchedulerName, CancellationToken.None);
        Assert.Equal("Standby", (await scheduler.GetStatus()).ToString());
        identity.SetAnonymous();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.StartSchedulerAsync(scheduler.SchedulerName, CancellationToken.None));
        await scheduler.Shutdown();
    }

    [Fact]
    public async Task Named_permissions_use_scheduler_resource_and_read_only_blocks_all_writes()
    {
        var identity = new MutableAuthenticationState();
        identity.SetUser("operator", "operator");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddQuartzSupervisorDashboard(options =>
        {
            options.ViewPolicy = "Tenant";
            options.OperatePolicy = "Operate";
            options.AdministerPolicy = "Administer";
        });
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => identity);
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Tenant", policy => policy.RequireAssertion(context => Equals(context.Resource, "allowed")));
            options.AddPolicy("Operate", policy => policy.RequireRole("operator"));
            options.AddPolicy("Administer", policy => policy.RequireRole("administrator"));
        });
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardAccess>();
        Assert.Equal("operator", await access.AuthorizeAsync("allowed", DashboardPermission.Operate, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.AuthorizeAsync("other", DashboardPermission.View, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.AuthorizeAsync("allowed", DashboardPermission.Administer, CancellationToken.None).AsTask());
        app.Services.GetRequiredService<QuartzSupervisorOptions>().ReadOnly = true;
        Assert.Equal("operator", await access.AuthorizeAsync("allowed", DashboardPermission.View, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.AuthorizeAsync("allowed", DashboardPermission.Operate, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Explicit_anonymous_opt_in_permits_commands_without_an_authentication_scheme()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddQuartzSupervisorDashboard(options => options.RequireAuthentication = false);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var scheduler = await scope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var commands = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardCommands>();
        await commands.StartSchedulerAsync(scheduler.SchedulerName, CancellationToken.None);
        Assert.Equal("Running", (await scheduler.GetStatus()).ToString());
        await commands.StandbySchedulerAsync(scheduler.SchedulerName, CancellationToken.None);
        Assert.Equal("Standby", (await scheduler.GetStatus()).ToString());
        await scheduler.Shutdown();
    }

    [Fact]
    public async Task Expired_identity_is_rejected_even_when_the_provider_still_reports_authenticated()
    {
        var identity = new MutableAuthenticationState();
        identity.SetUser("operator");
        identity.Expire();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(options => options.UseInMemoryStore());
        builder.Services.AddQuartzSupervisorDashboard();
        builder.Services.AddScoped<AuthenticationStateProvider>(_ => identity);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardAccess>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            access.AuthorizeAsync("scheduler", DashboardPermission.View, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//unsafe")]
    [InlineData("/safe/../unsafe")]
    [InlineData("/unsafe?query")]
    public void Invalid_dashboard_paths_are_rejected_at_registration(string path)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddQuartzSupervisorDashboard(options => options.Path = path));
    }

    [Fact]
    public void Context_encodes_identity_and_preserves_proxy_base()
    {
        var navigation = new TestNavigation("https://host/proxy/ops/", "https://host/proxy/ops/jobs?scheduler=alpha");
        var context = new DashboardContext(navigation, new HttpContextAccessor(), new QuartzSupervisorOptions { Path = "/ops" }, new AllowAccess());
        Assert.Equal("/proxy/ops", context.RootPath);
        Assert.Equal("alpha", context.SchedulerName);
        var url = context.Url("job", query: new Dictionary<string, string?> { ["group"] = "a/b", ["name"] = "x & y" });
        Assert.Contains("group=a%2Fb", url, StringComparison.Ordinal);
        Assert.Contains("name=x%20%26%20y", url, StringComparison.Ordinal);
        Assert.Equal("/proxy/_content/QuartzSupervisor/dashboard.css", context.Asset("dashboard.css"));
    }

    [Route("/")]
    public sealed class HostComponent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "Host application");
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string baseUri, string uri) => Initialize(baseUri, uri);
    }

    private sealed class AllowAccess : ISchedulerDashboardAccess
    {
        public ValueTask<string> AuthorizeAsync(string schedulerName, DashboardPermission permission, CancellationToken ct) => ValueTask.FromResult("test");
    }

    private sealed class MutableAuthenticationState : AuthenticationStateProvider
    {
        private ClaimsPrincipal user = new(new ClaimsIdentity());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
        public void SetUser(string name, params string[] roles)
        {
            user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }
                .Concat(roles.Select(role => new Claim(ClaimTypes.Role, role))), "test"));
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
        public void Expire() => ((ClaimsIdentity)user.Identity!).AddClaim(
            new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public void SetAnonymous()
        {
            user = new ClaimsPrincipal(new ClaimsIdentity());
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
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

            var claims = new List<Claim> { new(ClaimTypes.Name, userName.ToString()) };
            if (Request.Headers.ContainsKey("X-Test-Dashboard-Admin"))
                claims.Add(new Claim(ClaimTypes.Role, "DashboardAdmin"));

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
