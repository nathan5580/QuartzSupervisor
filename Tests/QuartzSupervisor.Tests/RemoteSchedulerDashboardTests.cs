using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
        hubBuilder.Services.AddQuartzSupervisorDashboard(options => options.RequireAuthentication = false);
        await using var hubApp = hubBuilder.Build();
        await hubApp.StartAsync();

        await using var scope = hubApp.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardQueries>();
        var commands = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardCommands>();
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
        hubBuilder.Services.AddQuartzSupervisorRemote("billing", "shared-api");
        hubBuilder.Services.AddQuartzSupervisorRemote("reports", "shared-api");
        hubBuilder.Services.AddQuartzSupervisorDashboard(options => options.RequireAuthentication = false);

        await using var hubApp = hubBuilder.Build();
        await hubApp.StartAsync();

        await using var scope = hubApp.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardQueries>();
        var commands = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardCommands>();
        var schedulers = await queries.GetSchedulersAsync(CancellationToken.None);

        Assert.Equal(["billing", "reports"], schedulers.Select(scheduler => scheduler.Name));
        Assert.Equal(1, (await queries.GetOverviewAsync("billing", CancellationToken.None)).JobCount);
        Assert.Equal(2, (await queries.GetOverviewAsync("reports", CancellationToken.None)).JobCount);

        await commands.StandbySchedulerAsync("billing", CancellationToken.None);

        Assert.Equal("Standby", (await queries.GetOverviewAsync("billing", CancellationToken.None)).Scheduler.Status);
        Assert.Equal("Running", (await queries.GetOverviewAsync("reports", CancellationToken.None)).Scheduler.Status);
    }

    [Fact]
    public async Task Remote_execution_history_and_event_stream_use_the_named_authenticated_client()
    {
        const string schedulerName = "remote-history";
        const string apiKey = "history-test-key";
        await using var api = await StartRemoteApiAsync(schedulerName, 1, apiKey, includeFailure: true);
        var server = (TestServer)api.Services.GetRequiredService<IServer>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddQuartz(_ => { });
        builder.Services.AddHttpClient("remote-history-api", client =>
        {
            client.BaseAddress = new Uri("http://localhost/quartz-api/");
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }).ConfigurePrimaryHttpMessageHandler(server.CreateHandler);
        builder.Services.AddQuartzSupervisorRemote(schedulerName, "remote-history-api");
        builder.Services.AddQuartzSupervisorDashboard(options => options.RequireAuthentication = false);
        await using var hub = builder.Build();
        await hub.StartAsync();
        await using var scope = hub.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardQueries>();
        var commands = scope.ServiceProvider.GetRequiredService<ISchedulerDashboardCommands>();
        var updates = hub.Services.GetRequiredService<ISchedulerDashboardUpdates>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await EventuallyAsync(() => Task.FromResult(updates.GetFreshness(schedulerName).Mode == "remote"), deadline.Token);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(string name) { if (name == schedulerName) changed.TrySetResult(); }
        updates.Changed += OnChanged;
        try
        {
            // Execute through the remote server itself: this invalidation cannot come from dashboard commands.
            var scheduler = await api.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(deadline.Token);
            await scheduler.TriggerJob(new JobKey("work-0", "jobs"), cancellationToken: deadline.Token);
            await EventuallyAsync(async () => (await queries.GetRunningAsync(schedulerName, deadline.Token)).Count > 0, deadline.Token);
            await changed.Task.WaitAsync(deadline.Token);
            await EventuallyAsync(async () =>
            {
                var history = await queries.GetHistoryAsync(schedulerName, new ExecutionFilter(), deadline.Token);
                return history.Available && history.Page.Items.Any(execution => execution.CompletedUtc is not null);
            }, deadline.Token);
            var first = (await queries.GetHistoryAsync(schedulerName, new ExecutionFilter(), deadline.Token)).Page.Items.Single();
            Assert.Equal("work-0", first.JobName);
            Assert.Equal(schedulerName, first.SchedulerName);
            Assert.False(first.IsError);
            Assert.NotNull(await queries.GetExecutionAsync(schedulerName, first.Id, deadline.Token));

            await commands.TriggerJobAsync(schedulerName, "jobs", "work-0", deadline.Token);
            await EventuallyAsync(async () => (await queries.GetHistoryAsync(schedulerName, new ExecutionFilter(), deadline.Token))
                .Page.Items.Count(execution => execution.CompletedUtc is not null) == 2, deadline.Token);
            var rows = (await queries.GetHistoryAsync(schedulerName, new ExecutionFilter(), deadline.Token)).Page.Items;
            Assert.Equal(2, rows.Select(execution => execution.Id).Distinct().Count());

            await commands.TriggerJobAsync(schedulerName, "jobs", "expected-failure", deadline.Token);
            await EventuallyAsync(async () => (await queries.GetHistoryAsync(schedulerName,
                new ExecutionFilter { FailedOnly = true }, deadline.Token)).Page.Items.Count == 1, deadline.Token);
            var failed = (await queries.GetHistoryAsync(schedulerName, new ExecutionFilter { FailedOnly = true }, deadline.Token)).Page.Items.Single();
            Assert.Equal("expected-failure", failed.JobName);
            Assert.True(failed.IsError);
            Assert.Null(failed.ErrorMessage); // Raw diagnostics require explicit host redaction.
        }
        finally { updates.Changed -= OnChanged; }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
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

    private static async Task<WebApplication> StartRemoteApiAsync(string schedulerName, int jobCount, string apiKey, bool includeFailure = false)
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
            if (includeFailure)
                quartz.AddJob<FailingRemoteJob>(job => job.WithIdentity("expected-failure", "jobs").StoreDurably());
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
        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            context.ReportProgress(25, "Remote test work");
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }
    }

    private sealed class FailingRemoteJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
            throw new JobExecutionException("Expected remote test failure");
    }
}
