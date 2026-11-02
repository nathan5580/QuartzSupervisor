using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Quartz;
using QuartzSupervisor.Dashboard;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5000");

// Only delays, progress and a deliberate exception: no mail, files, payments or external APIs.
builder.Services.AddQuartz(options =>
{
    options.ConfigureScheduler(scheduler => scheduler.InstanceName = "Embedded operations");
    options.UseInMemoryStore();
    options.AddJob<HeartbeatJob>(job => job.WithIdentity("heartbeat", "operations")
        .WithDescription("Checks the sample worker every 20 seconds; completes in 2 seconds.").StoreDurably());
    options.AddTrigger<HeartbeatJob>(trigger => trigger.WithIdentity("heartbeat-every-20s", "operations")
        .ForJob(new JobKey("heartbeat", "operations")).StartNow().WithSimpleSchedule(TimeSpan.FromSeconds(20)));
    options.AddJob<ReportJob>(job => job.WithIdentity("daily-summary", "reports")
        .WithDescription("Prepares an in-memory report with visible progress; one execution at a time.")
        .UsingJobData("DurationSeconds", 12).StoreDurably());
    options.AddTrigger<ReportJob>(trigger => trigger.WithIdentity("report-every-minute", "reports")
        .ForJob(new JobKey("daily-summary", "reports")).WithCronSchedule("0 * * * * ?"));
    options.AddJob<MaintenanceJob>(job => job.WithIdentity("cache-refresh", "maintenance")
        .WithDescription("Simulates a 25-second cache refresh. Cancellation is cooperative.").StoreDurably());
    options.AddTrigger<MaintenanceJob>(trigger => trigger.WithIdentity("cache-every-90s", "maintenance")
        .ForJob(new JobKey("cache-refresh", "maintenance")).StartNow().WithSimpleSchedule(TimeSpan.FromSeconds(90)));
    options.AddJob<FailureJob>(job => job.WithIdentity("validation-example", "diagnostics")
        .WithDescription("Intentionally fails after 3 seconds to demonstrate failed execution history.").StoreDurably());
    options.AddTrigger<FailureJob>(trigger => trigger.WithIdentity("validation-every-2m", "diagnostics")
        .ForJob(new JobKey("validation-example", "diagnostics")).StartNow().WithSimpleSchedule(TimeSpan.FromMinutes(2)));
});
builder.Services.Configure<ThreadPoolOptions>(options => options.MaxConcurrency = 4);
builder.Services.AddQuartzExecutionHistory(options =>
{
    options.Retention = TimeSpan.FromHours(24);
    options.MaxEntriesPerScheduler = 2000;
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

var demoAuthentication = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Dashboard:DemoAuthentication");
if (demoAuthentication)
    builder.Services.AddAuthentication("Demo").AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>("Demo", _ => { });
else
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
    {
        // A production host supplies its own sign-in flow; the sample never creates accounts.
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Operate", policy => policy.RequireRole("operator", "administrator"));
    options.AddPolicy("Administer", policy => policy.RequireRole("administrator"));
});
builder.Services.AddQuartzSupervisorDashboard(options =>
{
    options.Path = builder.Configuration["Dashboard:Path"] ?? "/quartz-supervisor";
    options.Title = "Quartz Supervisor · Embedded sample";
    options.RequireAuthentication = builder.Configuration.GetValue("Dashboard:RequireAuthentication", !builder.Environment.IsDevelopment());
    options.ReadOnly = builder.Configuration.GetValue<bool>("Dashboard:ReadOnly");
    options.Management.RedactDiagnostic = text => text; // This sample emits only fixed, non-sensitive demonstration diagnostics.
    if (options.RequireAuthentication)
    {
        options.OperatePolicy = "Operate";
        options.AdministerPolicy = "Administer";
    }
    options.Management.JobTypes.AddRange([
        new("heartbeat", "Heartbeat check", typeof(HeartbeatJob).AssemblyQualifiedName!, []),
        new("report", "In-memory report", typeof(ReportJob).AssemblyQualifiedName!, [new("DurationSeconds", "Duration in seconds (1–60)", "int", true)]),
        new("maintenance", "Cache refresh simulation", typeof(MaintenanceJob).AssemblyQualifiedName!, []),
        new("failure", "Intentional validation failure", typeof(FailureJob).AssemblyQualifiedName!, [])
    ]);
});

var app = builder.Build();
if (builder.Configuration["Dashboard:PathBase"] is { Length: > 0 } pathBase) app.UsePathBase(pathBase);
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapQuartzSupervisorDashboard();
app.Run();

public sealed class HeartbeatJob : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class ReportJob : IJob
{
    public int DurationSeconds { get; set; } = 12;
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (DurationSeconds is < 1 or > 60) throw new JobExecutionException("Report duration must be between 1 and 60 seconds.");
        for (var step = 1; step <= DurationSeconds; step++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            context.ReportProgress(step * 100 / DurationSeconds, $"Preparing section {step} of {DurationSeconds}");
        }
    }
}

[DisallowConcurrentExecution]
public sealed class MaintenanceJob : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        for (var step = 1; step <= 5; step++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            context.ReportProgress(step * 20, $"Refreshing partition {step} of 5");
        }
    }
}

public sealed class FailureJob : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        throw new JobExecutionException("Expected sample failure: the demonstration record did not pass validation.");
    }
}

// Enabled only with Development + Dashboard__DemoAuthentication=true. Never registered in production.
public sealed class DemoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Dashboard-Demo-Role"].ToString();
        if (role is not ("reader" or "operator" or "administrator")) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "sample-" + role), new(ClaimTypes.Role, role)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
