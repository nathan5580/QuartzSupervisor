using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Quartz;
using QuartzSupervisor.Dashboard;
using QuartzSupervisor.Integration;
using Xunit;

namespace QuartzSupervisor.Tests;

public sealed class RemoteSchedulerDashboardTests
{
    [Fact]
    public async Task Dashboard_queries_controls_and_isolates_multiple_authenticated_remote_schedulers()
    {
        const string firstName = "remote-a";
        const string secondName = "remote-b";
        const string firstKey = "billing-key";
        const string secondKey = "reports-key";
        await using var firstApi = await StartRemoteApiAsync(firstName, jobCount: 1, firstKey);
        await using var secondApi = await StartRemoteApiAsync(secondName, jobCount: 2, secondKey);
        using var firstClient = firstApi.GetTestClient();
        using var secondClient = secondApi.GetTestClient();
        firstClient.BaseAddress = new Uri("http://localhost/quartz-api/");
        secondClient.BaseAddress = new Uri("http://localhost/quartz-api/");
        firstClient.DefaultRequestHeaders.Add("X-Api-Key", firstKey);
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", secondKey);

        var hubBuilder = WebApplication.CreateBuilder();
        hubBuilder.WebHost.UseTestServer();
        hubBuilder.Services.AddQuartz(_ => { });
        hubBuilder.Services.AddQuartzHttpClient(firstName, _ => firstClient);
        hubBuilder.Services.AddQuartzHttpClient(secondName, _ => secondClient);
        QuartzSupervisor.Dashboard.QuartzSupervisorDashboardExtensions.AddQuartzSupervisorDashboard(hubBuilder.Services);
        await using var hubApp = hubBuilder.Build();
        await hubApp.StartAsync();

        var queries = hubApp.Services.GetRequiredService<ISchedulerDashboardQueries>();
        var commands = hubApp.Services.GetRequiredService<ISchedulerDashboardCommands>();
        var schedulers = await queries.GetSchedulersAsync(CancellationToken.None);

        Assert.Equal([firstName, secondName], schedulers.Select(scheduler => scheduler.Name));
        Assert.Equal(1, (await queries.GetOverviewAsync(firstName, CancellationToken.None)).JobCount);
        Assert.Equal(2, (await queries.GetOverviewAsync(secondName, CancellationToken.None)).JobCount);

        await commands.StandbySchedulerAsync(firstName, CancellationToken.None);
        Assert.Equal("Standby", (await queries.GetOverviewAsync(firstName, CancellationToken.None)).Scheduler.Status);
        Assert.Equal("Running", (await queries.GetOverviewAsync(secondName, CancellationToken.None)).Scheduler.Status);

        secondClient.DefaultRequestHeaders.Remove("X-Api-Key");
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", firstKey);
        schedulers = await queries.GetSchedulersAsync(CancellationToken.None);

        Assert.Equal("Standby", schedulers[0].Status);
        Assert.Equal(secondName, schedulers[1].Name);
        Assert.Equal("Unavailable", schedulers[1].Status);
        Assert.Equal(1, (await queries.GetOverviewAsync(firstName, CancellationToken.None)).JobCount);
        secondClient.DefaultRequestHeaders.Remove("X-Api-Key");
        secondClient.DefaultRequestHeaders.Add("X-Api-Key", secondKey);
        await secondApi.StopAsync();
        schedulers = await queries.GetSchedulersAsync(CancellationToken.None);
        Assert.Equal("Unavailable", schedulers[1].Status);
        Assert.Equal(1, (await queries.GetOverviewAsync(firstName, CancellationToken.None)).JobCount);
    }

    private static async Task<WebApplication> StartRemoteApiAsync(string schedulerName, int jobCount, string apiKey)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(quartz =>
        {
            quartz.ConfigureScheduler(options => options.InstanceName = schedulerName);
            quartz.UseInMemoryStore();
            for (var index = 0; index < jobCount; index++)
            {
                var jobName = $"work-{index}";
                quartz.AddJob<RemoteJob>(job => job.WithIdentity(jobName, "jobs").StoreDurably());
            }
        });
        builder.Services.AddQuartzHttpApi();
        builder.Services.AddAntiforgery();
        builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Request.Headers["X-Api-Key"], apiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.UseAntiforgery();
        app.MapQuartzHttpApi("/quartz-api").AllowAnonymous();
        await app.StartAsync();
        return app;
    }

    private sealed class RemoteJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
