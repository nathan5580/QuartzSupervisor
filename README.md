# Quartz Supervisor

A Quartz.NET operations dashboard embedded in an existing ASP.NET Core API. Keep your scheduler, routes, and hosting model. [Source code on GitHub](https://github.com/nathan5580/QuartzSupervisor).

## Compatibility

- .NET 10 ASP.NET Core applications using Minimal APIs or MVC controllers.
- Quartz.NET 4.3 or later.
- Scheduler registrations in the same dependency-injection container.

The package does not inspect Quartz database tables. It reads scheduler state through Quartz APIs.

## Run locally

The NuGet package is not published yet. Clone the public repository and run the embedded sample:

```sh
git clone https://github.com/nathan5580/QuartzSupervisor.git
cd QuartzSupervisor
dotnet run --project Samples/Embedded/Embedded.csproj
```

## Add to an existing API

Keep your API's existing Quartz and authentication configuration. Register the dashboard and map its endpoints:

```csharp
using QuartzSupervisor.Dashboard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddQuartz(_ => { }); // Keep your existing Quartz configuration.
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddQuartzSupervisorDashboard();

// Keep your existing authentication scheme and authorization policies.

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
// For a controller API, also keep app.MapControllers().
app.MapQuartzSupervisorDashboard();

app.Run();
```

Open `/quartz-supervisor`. The dashboard endpoints require an authenticated user by default. Configure an authentication scheme in the host; Quartz Supervisor does not ship credentials or change the authorization policy on your API routes. Use the host's existing authentication and authorization middleware. The stylesheet and Blazor framework assets are served by `MapStaticAssets()`.

The sample allows anonymous dashboard access only in Development and binds to localhost for that mode. Production deployments must configure host authentication.

## Protect with an explicit policy

```csharp
app.MapQuartzSupervisorDashboard().RequireAuthorization("Admin");
```

Use `AllowAnonymous()` only for a deliberately public or local-only deployment. Do not expose operational details publicly without access control.

## Scope

- Overview: scheduler status, job and trigger counts, and the next five fire times.
- Jobs and Triggers: searchable, paged listings with run-now, pause/resume, and deletion controls.
- Scheduler controls: start or place a scheduler in reversible standby.
- Multiple schedulers registered in the same host.
- Timeline: live scheduler and execution events observed after first connection; up to 100 per scheduler in memory, not persisted. The all-schedulers view shows the latest 100 overall.

Standby pauses future trigger firings but does not interrupt running jobs. Starting resumes scheduling; Quartz applies its configured misfire instructions. “Run now” requests immediate execution. Pausing a job pauses its triggers; trigger controls pause or resume one schedule. Deleting a job also deletes its triggers. Removing a trigger may also remove its non-durable job when it was the last trigger; both destructive actions require confirmation. Scheduling and rescheduling new triggers are not supported.

No job data maps, secrets, or scheduler database contents are displayed.

Quartz listener callbacks refresh active dashboard views through the host's existing Blazor Server SignalR connection when scheduler, scheduling, pause/delete, or job/trigger execution state changes. Timeline capture starts when each scheduler is first queried; its recent events are cleared when the host process restarts. No polling or extra SignalR hub is added. The light/dark theme follows the browser's system preference until changed; the choice is stored per browser.

## Contributing and tests

```sh
dotnet test QuartzSupervisor.slnx
dotnet build QuartzSupervisor.slnx
```

Tests use Quartz's in-memory store and ASP.NET Core's test host; they need no external database or credentials. See [CONTRIBUTING.md](CONTRIBUTING.md) for the local workflow and [SECURITY.md](SECURITY.md) for vulnerability reports.

## License

[MIT](LICENSE)
