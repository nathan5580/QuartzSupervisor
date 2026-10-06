# Quartz Supervisor
[![CI](https://github.com/nathan5580/QuartzSupervisor/actions/workflows/ci.yml/badge.svg)](https://github.com/nathan5580/QuartzSupervisor/actions/workflows/ci.yml)

An embeddable Quartz.NET operations dashboard for ASP.NET Core. Source: [GitHub](https://github.com/nathan5580/QuartzSupervisor). **The NuGet package is not published yet**; the instructions below distinguish running from this clone from future package installation.

![Quartz Supervisor dashboard overview](https://raw.githubusercontent.com/nathan5580/QuartzSupervisor/main/docs/images/dashboard-overview.png)

## Compatibility

- .NET 10 / ASP.NET Core 10 host.
- Quartz.NET 4.3 or later, with scheduler(s) registered in the host's dependency-injection container.
- Minimal API and MVC hosts are supported. The dashboard uses Razor Components with interactive server rendering.

Quartz Supervisor reads and controls schedulers through Quartz APIs; it does not inspect scheduler database tables or display job data maps.

## Guided AI setup

Use the [guided AI setup prompt](docs/AI_SETUP_PROMPT.md) from the root of the host application. It makes the agent inspect the existing host first, confirm only missing topology/auth decisions, never request secrets, and verify the result.

## Run the sample from source

Requires the .NET 10 SDK. Clone the repository and run:

```sh
git clone https://github.com/nathan5580/QuartzSupervisor.git
cd QuartzSupervisor
dotnet run --project Samples/Embedded/Embedded.csproj
```

Open `http://localhost:5000/quartz-supervisor` in Development. The sample allows anonymous access only in Development and binds to localhost there; set `ASPNETCORE_URLS` to choose another local port. This is a source/sample workflow, not a NuGet installation.

The sample's heartbeat job waits 15 seconds so its live execution bar is visible in the timeline.

## Embed in an ASP.NET Core host

Until publication, consume the projects from a local clone (for example, add a project reference to `Libraries/QuartzSupervisor.Dashboard/QuartzSupervisor.Dashboard.csproj`). That project references the integration project. After publication, install the `QuartzSupervisor` package instead; do not assume it is currently available on NuGet.

Keep the host's Quartz registration and configure its own authentication. Minimal setup:

```csharp
using Quartz;
using QuartzSupervisor.Dashboard;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddQuartz(options => { /* your jobs and scheduler configuration */ });
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddQuartzSupervisorDashboard();
// Configure your host's authentication scheme and authorization services.

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
// Map your own routes/controllers as usual.
app.MapQuartzSupervisorDashboard();
app.Run();
```

`AddQuartzSupervisorDashboard()` registers dashboard services, authorization services, and interactive server components. It does not register an authentication scheme, user store, or sign-in page. `MapQuartzSupervisorDashboard()` requires an authenticated user by default. The host owns human sign-in and authorization.

For the browser dashboard, configure the host's existing interactive sign-in scheme (commonly cookie plus OIDC). API-only bearer authentication is not automatically a browser sign-in flow. Register the scheme and policies in `builder.Services`, then call `UseAuthentication()` and `UseAuthorization()` in that order before mapped endpoints.

To require a host role for the dashboard while leaving other API routes under their existing policies:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("DashboardAdmin", policy => policy.RequireRole("DashboardAdmin")));

app.MapQuartzSupervisorDashboard().RequireAuthorization("DashboardAdmin");
```

This policy applies to the dashboard endpoint only. Use the host's normal role/claim mapping and authentication scheme.

`UseAntiforgery()` and `MapStaticAssets()` are part of the working .NET 10 setup: static asset mapping serves dashboard and Blazor framework assets. Keep any other middleware your host needs. Do not make the dashboard anonymous outside a deliberately public, isolated local development setup.

## Connect one or more remote Quartz APIs

Choose the deployment that matches where the scheduler runs:

| Topology | Dashboard host | Scheduler host |
| --- | --- | --- |
| Same process | Register the local scheduler and dashboard. No Quartz HTTP client/API is needed. | Same host. |
| Central dashboard, one API and one scheduler | Register one `AddQuartzHttpClient` entry and its named `HttpClient`. | Expose the scheduler through a protected Quartz HTTP API. |
| Central dashboard, one API and multiple schedulers | Register one `AddQuartzHttpClient` entry per scheduler; they may share one named `HttpClient`. | One `AddQuartzHttpApi()` serves the schedulers registered in that API host. |
| Central dashboard, multiple API hosts | Register one entry per remote scheduler. Use separate named clients where API URL or credentials differ. | Each API host owns its Quartz configuration, storage, and authorization. |

Remote `AddQuartzHttpClient` registrations are keyed by scheduler name, so names must be unique in the central host. If separate API hosts both use the default scheduler name, configure distinct scheduler instance names on the API hosts before registering them centrally. Registering two independent targets under the same name collides; it does not create failover or a cluster.

On each scheduler-owning API host, retain its existing Quartz job-store/jobs configuration and expose a protected endpoint. `AddQuartzHttpApi()` serves the schedulers registered in that host under one API path. Each scheduler's name must match its dashboard `AddQuartzHttpClient` registration:

```csharp
using Quartz;

builder.Services.AddQuartz(options =>
{
    options.ConfigureScheduler(scheduler => scheduler.InstanceName = "billing");
    // Keep this API's existing job store, jobs, and triggers here.
});

builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddQuartzHttpApi();
// Register this API host's existing authentication scheme.
builder.Services.AddAuthorization(options =>
    options.AddPolicy("QuartzApi", policy => policy.RequireAuthenticatedUser()));

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapQuartzHttpApi("/quartz-api").RequireAuthorization("QuartzApi");
```

For scheduler-specific access rules, configure `QuartzHttpApiOptions.SchedulerAuthorizationPolicy` with a resource-aware policy for `SchedulerResource`. The API then applies that policy to scheduler-scoped routes and filters the scheduler listing to schedulers the caller may access. `RequireAuthorization("QuartzApi")` above is the separate host-level requirement that callers authenticate before reaching the API.

On the central dashboard host, a single remote scheduler uses one named client and one registration:

```csharp
using Quartz;

var billingAddress = builder.Configuration["QuartzApis:Billing:BaseAddress"]
    ?? throw new InvalidOperationException("Missing QuartzApis:Billing:BaseAddress.");
var billingToken = builder.Configuration["QuartzApis:Billing:AccessToken"]
    ?? throw new InvalidOperationException("Missing QuartzApis:Billing:AccessToken.");

builder.Services.AddQuartz(options => { }); // Required before AddQuartzHttpClient.
builder.Services.AddHttpClient("billing-api", client =>
{
    client.BaseAddress = new Uri(billingAddress);
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", billingToken);
});
builder.Services.AddQuartzHttpClient("billing", "billing-api");
builder.Services.AddQuartzSupervisorDashboard();
```

Use the authentication scheme actually configured by that API. Set `BaseAddress` to the API route with a trailing slash (for example, `https://billing.example/quartz-api/`). This snippet shows a static bearer credential only; for OAuth client credentials or rotating tokens, attach a `DelegatingHandler` that uses the host's token provider. Supply secrets through the host's secret manager or environment (for example, `QuartzApis__Billing__AccessToken`), never committed settings or source.

Register one `AddQuartzHttpClient` entry per remote scheduler. Schedulers behind the same API URL and using the same credentials can share a named client:

```csharp
using Quartz;

builder.Services.AddQuartzHttpClient("billing", "billing-api");
builder.Services.AddQuartzHttpClient("reports", "billing-api");
```

Use separate named clients when API URLs or credentials differ. `billing` and `reports` may be hosted by one API or two; one registration is required for each scheduler.

Authentication boundaries:

- The dashboard host authenticates the human operator. Remote API calls use the central host's service credentials; the API does not automatically receive the human user's identity.
- The dashboard's host authorization policy controls which operators can use the UI. Each Quartz API separately controls which service identities can query or control its schedulers; scheduler-specific rules can use `QuartzHttpApiOptions.SchedulerAuthorizationPolicy`.
- If an API must authorize individual human operators, implement and verify explicit token delegation. Do not assume it is built in or forward browser cookies.

Use HTTPS and never expose service credentials to browser code. Quartz Supervisor does not proxy arbitrary URLs, store credentials, or choose a job store. Listener-based live timeline capture is local to the dashboard process; remote executions are not streamed.


## Behavior and limits

- Multiple schedulers registered with the host's `ISchedulerFactory` can be selected in the dashboard.
- Overview reports scheduler state, job/trigger counts, and up to five upcoming triggers. Job and trigger lists are searchable and paged.
- Operators can start a scheduler, put it in standby, run a job now, pause/resume jobs or triggers, and delete jobs or triggers.
- Standby does not interrupt running jobs. Starting resumes scheduling and Quartz applies configured misfire instructions. Deleting a job also deletes its triggers. Removing a trigger may also remove its non-durable job if it was the last trigger; destructive actions ask for confirmation.
- Creating or rescheduling triggers is not supported. No job data maps or secrets are displayed.
- For in-process schedulers, Quartz listener callbacks stream updates over the existing Blazor Server connection; no extra hub or polling loop is added.
- A scheduler whose status request fails with a Quartz/HTTP error or times out is labeled **Unavailable** and listed after healthy schedulers. Other schedulers remain usable; requests to the unavailable scheduler still report an error.
- Timeline uses a self-hosted vis-timeline 8.5.2 build (Apache-2.0 OR MIT; no CDN). Runs are grouped by job, shown against a UTC time axis, and color-coded by status; the chart supports zoom, pan, a moving current-time marker, and a Live now reset. In-process runs only; up to 100 are retained per scheduler in memory, with a rolling 10-minute view and manual one-shot runs labeled.

- If a Blazor Server circuit drops, the branded dialog reports retries and exposes retry/refresh actions only when automatic recovery fails.

![Quartz Supervisor live job execution timeline](https://raw.githubusercontent.com/nathan5580/QuartzSupervisor/main/docs/images/live-execution-timeline.png)

## Troubleshooting

- **Dashboard returns 401/redirects:** configure the host's authentication scheme and middleware; dashboard access is authenticated by default.
- **Page loads without styling or interactive behavior:** retain `MapStaticAssets()`, `UseAntiforgery()`, and the interactive server component setup shown above.
- **No scheduler appears:** register Quartz in the same host service provider before mapping the dashboard; the dashboard discovers schedulers via `ISchedulerFactory`.
- **Remote scheduler missing:** install `Quartz.HttpClient` (4.3 or later), start the dashboard host so the Quartz HTTP clients bind to the scheduler repository, and check that each registered name matches its API host.
- **No executions visible:** the graph starts observing after a scheduler is first queried and retains runs only in memory; run a job or wait for a trigger.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local workflow and useful issue/PR reports. Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
