using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
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

    [Fact]
    public async Task Dashboard_reads_and_controls_multiple_schedulers_through_one_named_api_client()
    {
        const string apiKey = "shared-key";
        await using var api = await StartMultiSchedulerApiAsync(apiKey);
        var apiServer = (TestServer)api.Services.GetRequiredService<IServer>();

        var hubBuilder = WebApplication.CreateBuilder();
        hubBuilder.WebHost.UseTestServer();
        hubBuilder.Services.AddQuartz(_ => { });
        hubBuilder.Services.AddHttpClient("shared-api", client =>
        {
            client.BaseAddress = new Uri("http://localhost/quartz-api/");
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }).ConfigurePrimaryHttpMessageHandler(apiServer.CreateHandler);
        hubBuilder.Services.AddQuartzHttpClient("billing", "shared-api");
        hubBuilder.Services.AddQuartzHttpClient("reports", "shared-api");
        QuartzSupervisor.Dashboard.QuartzSupervisorDashboardExtensions.AddQuartzSupervisorDashboard(hubBuilder.Services);

        await using var hubApp = hubBuilder.Build();
        await hubApp.StartAsync();

        var queries = hubApp.Services.GetRequiredService<ISchedulerDashboardQueries>();
        var commands = hubApp.Services.GetRequiredService<ISchedulerDashboardCommands>();
        var schedulers = await queries.GetSchedulersAsync(CancellationToken.None);

        Assert.Equal(["billing", "reports"], schedulers.Select(scheduler => scheduler.Name));
        Assert.Equal(1, (await queries.GetOverviewAsync("billing", CancellationToken.None)).JobCount);
        Assert.Equal(2, (await queries.GetOverviewAsync("reports", CancellationToken.None)).JobCount);

        await commands.StandbySchedulerAsync("billing", CancellationToken.None);

        Assert.Equal("Standby", (await queries.GetOverviewAsync("billing", CancellationToken.None)).Scheduler.Status);
        Assert.Equal("Running", (await queries.GetOverviewAsync("reports", CancellationToken.None)).Scheduler.Status);
    }

    private static async Task<WebApplication> StartMultiSchedulerApiAsync(string apiKey)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz("billing", quartz =>
        {
            quartz.UseInMemoryStore();
            quartz.AddJob<RemoteJob>(job => job.WithIdentity("work-0", "jobs").StoreDurably());
        });
        builder.Services.AddQuartz("reports", quartz =>
        {
            quartz.UseInMemoryStore();
            for (var index = 0; index < 2; index++)
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
