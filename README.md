# Quartz Supervisor
[![CI](https://github.com/nathan5580/QuartzSupervisor/actions/workflows/ci.yml/badge.svg)](https://github.com/nathan5580/QuartzSupervisor/actions/workflows/ci.yml)

An embeddable Quartz.NET operations dashboard for ASP.NET Core. Source: [GitHub](https://github.com/nathan5580/QuartzSupervisor). **The NuGet package is not published yet**; the instructions below distinguish running from this clone from future package installation.

## Compatibility

- .NET 10 / ASP.NET Core 10 host.
- Quartz.NET 4.3 or later, with scheduler(s) registered in the host's dependency-injection container.
- Minimal API and MVC hosts are supported. The dashboard uses Razor Components with interactive server rendering.

Quartz Supervisor reads and controls schedulers through Quartz APIs; it does not inspect scheduler database tables or display job data maps.

## Run the sample from source

Requires the .NET 10 SDK. Clone the repository and run:

```sh
git clone https://github.com/nathan5580/QuartzSupervisor.git
cd QuartzSupervisor
dotnet run --project Samples/Embedded/Embedded.csproj
```

Open `http://localhost:5000/quartz-supervisor` in Development. The sample allows anonymous access only in Development and binds to localhost there; set `ASPNETCORE_URLS` to choose another local port. This is a source/sample workflow, not a NuGet installation.

## Embed in an ASP.NET Core host

Until publication, consume the projects from a local clone (for example, add a project reference to `Libraries/QuartzSupervisor.Dashboard/QuartzSupervisor.Dashboard.csproj`). That project references the integration project. After publication, install the `QuartzSupervisor` package instead; do not assume it is currently available on NuGet.

Keep the host's Quartz registration and configure its own authentication. Minimal setup:

```csharp
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

`AddQuartzSupervisorDashboard()` registers the dashboard services, authorization, and interactive server components. `MapQuartzSupervisorDashboard()` maps the UI at `/quartz-supervisor` and requires an authenticated user by default. Put the host's `UseAuthentication()` and `UseAuthorization()` before mapped endpoints; use the host's normal authentication scheme and policies. The dashboard's default requirement does not protect unrelated host routes. Do not use `AllowAnonymous()` outside a deliberately public or local-only deployment.

To require a host role for the dashboard while leaving other API routes under their existing policies:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("DashboardAdmin", policy => policy.RequireRole("DashboardAdmin")));

app.MapQuartzSupervisorDashboard().RequireAuthorization("DashboardAdmin");
```

Use the host's normal role/claim mapping and authentication scheme; this policy applies to the dashboard endpoint only.

`UseAntiforgery()` and `MapStaticAssets()` are part of the working .NET 10 setup: static asset mapping serves the dashboard stylesheet and Blazor framework assets. Keep any other middleware your host needs; the dashboard does not configure authentication credentials for you.

## Connect remote Quartz APIs

Quartz.NET 4.3 adds `Quartz.HttpClient` and `Quartz.AspNetCore` for remote schedulers. Configure each API host with its own Quartz store and protect its HTTP endpoints:

```csharp
builder.Services.AddQuartzHttpApi();
// Configure authentication and authorization for this API.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapQuartzHttpApi("/quartz-api").RequireAuthorization();
```

On the dashboard host, register one Quartz HTTP client per remote scheduler. Each scheduler name must match the name on its API host; each named `HttpClient` can use its own base address and authentication handler:

```csharp
builder.Services.AddHttpClient("billing-api", client =>
    client.BaseAddress = new Uri("https://billing.example/quartz-api/"));
builder.Services.AddHttpClient("reports-api", client =>
    client.BaseAddress = new Uri("https://reports.example/quartz-api/"));
builder.Services.AddQuartz(options => { }); // Required before AddQuartzHttpClient.
builder.Services.AddQuartzHttpClient("billing", "billing-api");
builder.Services.AddQuartzHttpClient("reports", "reports-api");
builder.Services.AddQuartzSupervisorDashboard();
```

Install Quartz's `Quartz.HttpClient` package on the dashboard host and `Quartz.AspNetCore` on each API host. Quartz Supervisor does not proxy arbitrary URLs, store remote credentials, or choose the job store: every remote API owns its Quartz configuration and storage. Remote queries and controls use Quartz's HTTP client; configure each named client's own authentication handler in the dashboard host. Local listener-based timeline capture is not forwarded from remote hosts.

## Behavior and limits

- Multiple schedulers registered with the host's `ISchedulerFactory` can be selected in the dashboard.
- Overview reports scheduler state, job/trigger counts, and up to five upcoming triggers. Job and trigger lists are searchable and paged.
- Operators can start a scheduler, put it in standby, run a job now, pause/resume jobs or triggers, and delete jobs or triggers.
- Standby does not interrupt running jobs. Starting resumes scheduling and Quartz applies configured misfire instructions. Deleting a job also deletes its triggers. Removing a trigger may also remove its non-durable job if it was the last trigger; destructive actions ask for confirmation.
- Creating or rescheduling triggers is not supported. No job data maps or secrets are displayed.
- For in-process schedulers, Quartz listener callbacks stream updates over the existing Blazor Server connection; no extra hub or polling loop is added.
- A scheduler whose status request fails with a Quartz/HTTP error or times out is labeled **Unavailable** and listed after healthy schedulers. Other schedulers remain usable; requests to the unavailable scheduler still report an error.
- Timeline events flow oldest-to-newest from left to right and follow the newest event unless the operator has scrolled back. It keeps up to 100 observed events per scheduler in process memory; remote HTTP schedulers are not part of this local event stream.
- If a Blazor Server circuit drops, the branded dialog reports retries and exposes retry/refresh actions only when automatic recovery fails.

## Troubleshooting

- **Dashboard returns 401/redirects:** configure the host's authentication scheme and middleware; dashboard access is authenticated by default.
- **Page loads without styling or interactive behavior:** retain `MapStaticAssets()`, `UseAntiforgery()`, and the interactive server component setup shown above.
- **No scheduler appears:** register Quartz in the same host service provider before mapping the dashboard; the dashboard discovers schedulers via `ISchedulerFactory`.
- **Remote scheduler missing:** install `Quartz.HttpClient` (4.3 or later), start the dashboard host so the Quartz HTTP clients bind to the scheduler repository, and check that each registered name matches its API host.
- **Timeline is empty:** it is in-memory and starts observing a scheduler only after it is first queried; it is not historical storage.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for the local workflow and useful issue/PR reports. Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
