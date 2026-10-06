using Quartz;
using QuartzSupervisor.Dashboard;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5000");
}

builder.Services.AddQuartz(options =>
{
    var jobKey = new JobKey("heartbeat", "sample");
    options.AddJob<HeartbeatJob>(job => job
        .WithIdentity(jobKey)
        .WithDescription("Harmless embedded sample job"));
    options.AddTrigger<HeartbeatJob>(trigger => trigger
        .WithIdentity("heartbeat-trigger", "sample")
        .ForJob(jobKey)
        .StartNow()
        .WithSimpleSchedule(TimeSpan.FromMinutes(1)));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddQuartzSupervisorDashboard();

var app = builder.Build();
app.MapStaticAssets();
app.UseAntiforgery();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
var dashboard = app.MapQuartzSupervisorDashboard();
if (app.Environment.IsDevelopment())
    dashboard.AllowAnonymous(); // Local sample only; configure host authentication outside Development.
app.Run();

file sealed class HeartbeatJob : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
}
