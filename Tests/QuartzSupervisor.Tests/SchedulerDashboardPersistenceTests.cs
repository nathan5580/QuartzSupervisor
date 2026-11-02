using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using QuartzSupervisor.Integration;
using Xunit;

namespace QuartzSupervisor.Tests;

public sealed class SchedulerDashboardPersistenceTests
{
    [Fact]
    public async Task Native_persistent_history_and_configuration_survive_provider_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quartz-supervisor-{Guid.NewGuid():N}.db");
        var name = $"persistent-{Guid.NewGuid():N}";
        var connection = $"Data Source={path};Pooling=False";
        ServiceProvider Provider()
        {
            var services = new ServiceCollection();
            services.AddQuartz(q =>
            {
                q.ConfigureScheduler(o => o.InstanceName = name);
                q.UsePersistentStore(store =>
                {
                    store.UseSqlite(SqliteFactory.Instance, connection);
                    store.ProvisionSchema();
                    store.UseExecutionHistory();
                });
            });
            services.AddQuartzSupervisorIntegration();
            return services.BuildServiceProvider();
        }
        string id;
        try
        {
            await using (var first = Provider())
            {
                var scheduler = await first.GetRequiredService<ISchedulerFactory>().GetScheduler(CancellationToken.None);
                try
                {
                    await scheduler.AddJob(JobBuilder.Create<RecordedJob>().WithIdentity("persistent-job").StoreDurably().Build(), new(), CancellationToken.None);
                    await scheduler.Start(CancellationToken.None);
                    await first.GetRequiredService<ISchedulerDashboardCommands>().TriggerJobAsync(name, "DEFAULT", "persistent-job", CancellationToken.None);
                    var queries = first.GetRequiredService<ISchedulerDashboardQueries>();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    DashboardExecution? execution = null;
                    while (execution is null)
                    {
                        execution = (await queries.GetHistoryAsync(name, new(), deadline.Token)).Page.Items.FirstOrDefault();
                        if (execution is null) await Task.Delay(20, deadline.Token);
                    }
                    id = execution.Id;
                    Assert.False(execution.IsError);
                }
                finally { await scheduler.Shutdown(true, CancellationToken.None); }
            }
            await using (var second = Provider())
            {
                var scheduler = await second.GetRequiredService<ISchedulerFactory>().GetScheduler(CancellationToken.None);
                try
                {
                    var queries = second.GetRequiredService<ISchedulerDashboardQueries>();
                    var history = await queries.GetHistoryAsync(name, new(), CancellationToken.None);
                    Assert.True(history.Available);
                    Assert.Equal(id, Assert.Single(history.Page.Items).Id);
                    Assert.Equal(id, (await queries.GetExecutionAsync(name, id, CancellationToken.None))!.Id);
                    Assert.NotNull(await queries.GetJobAsync(name, "DEFAULT", "persistent-job", CancellationToken.None));
                    Assert.True((await queries.GetSchedulerDetailsAsync(name, CancellationToken.None)).Persistent);
                }
                finally { await scheduler.Shutdown(false, CancellationToken.None); }
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
    public sealed class RecordedJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
