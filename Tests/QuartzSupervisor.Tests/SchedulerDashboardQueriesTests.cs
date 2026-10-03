using Microsoft.Extensions.DependencyInjection;
using Quartz;
using QuartzSupervisor.Integration;
using Xunit;

namespace QuartzSupervisor.Tests;

public sealed class SchedulerDashboardQueriesTests : IAsyncLifetime
{
    private const int ItemCount = 52;
    private readonly CancellationToken _ct = CancellationToken.None;
    private IScheduler _scheduler = null!;
    private ServiceProvider _services = null!;
    private ISchedulerDashboardQueries _queries = null!;
    private DateTimeOffset _firstFire;
    private readonly List<IScheduler> _namedSchedulers = [];

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddQuartz(quartz =>
        {
            quartz.ConfigureScheduler(options => options.InstanceName = $"QuartzSupervisorTests-{Guid.NewGuid():N}");
            quartz.UseDefaultThreadPool(maxConcurrency: 1);
            quartz.UseInMemoryStore();
        });
        services.AddQuartz("zeta", quartz => quartz.UseInMemoryStore());
        services.AddQuartz("alpha", quartz => quartz.UseInMemoryStore());
        services.AddQuartzSupervisorDashboard();
        _services = services.BuildServiceProvider();
        _scheduler = await _services.GetRequiredService<ISchedulerFactory>().GetScheduler(_ct);
        await _scheduler.Start(_ct);
        foreach (var name in new[] { "alpha", "zeta" })
        {
            var factory = _services.GetRequiredKeyedService<ISchedulerFactory>(name);
            var scheduler = await factory.GetScheduler(_ct);
            await scheduler.Start(_ct);
            _namedSchedulers.Add(scheduler);
        }
        _queries = _services.GetRequiredService<ISchedulerDashboardQueries>();

        _firstFire = DateTimeOffset.UtcNow.AddHours(1);
        for (var index = 0; index < ItemCount; index++)
        {
            var key = new JobKey($"job-{index:D2}", "jobs");
            var job = JobBuilder.Create<NoopJob>().WithIdentity(key).Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trigger-{index:D2}", "triggers")
                .ForJob(key)
                .StartAt(_firstFire.AddMinutes(ItemCount - 1 - index))
                .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromHours(1)).RepeatForever())
                .Build();
            await _scheduler.ScheduleJob(job, trigger, default, _ct);
        }
    }

    [Fact]
    public async Task Scheduler_list_includes_named_schedulers_in_name_order()
    {
        var schedulers = await _queries.GetSchedulersAsync(_ct);

        Assert.Equal(new[] { "alpha", _scheduler.SchedulerName, "zeta" }, schedulers.Select(scheduler => scheduler.Name));
    }

    [Fact]
    public async Task Overview_returns_counts_and_five_soonest_triggers()
    {
        var overview = await _queries.GetOverviewAsync(_scheduler.SchedulerName, _ct);

        Assert.Equal(ItemCount, overview.JobCount);
        Assert.Equal(ItemCount, overview.TriggerCount);
        Assert.Equal(5, overview.UpcomingTriggers.Count);
        Assert.Equal("trigger-51", overview.UpcomingTriggers[0].Name);
        Assert.Equal("trigger-47", overview.UpcomingTriggers[4].Name);
        Assert.Equal(_firstFire, overview.UpcomingTriggers[0].NextFireTimeUtc);
        Assert.Equal(_firstFire.AddMinutes(4), overview.UpcomingTriggers[4].NextFireTimeUtc);
    }

    [Fact]
    public async Task Job_and_trigger_pages_have_stable_boundaries_and_trimmed_filters()
    {
        var firstJobs = await _queries.GetJobsAsync(_scheduler.SchedulerName, null, 0, _ct);
        var lastJobs = await _queries.GetJobsAsync(_scheduler.SchedulerName, null, 50, _ct);
        var matchingJobs = await _queries.GetJobsAsync(_scheduler.SchedulerName, " job-03 ", 0, _ct);
        var firstTriggers = await _queries.GetTriggersAsync(_scheduler.SchedulerName, null, 0, _ct);
        var lastTriggers = await _queries.GetTriggersAsync(_scheduler.SchedulerName, null, 50, _ct);
        var matchingTriggers = await _queries.GetTriggersAsync(_scheduler.SchedulerName, " trigger-03 ", 0, _ct);

        Assert.Equal(50, firstJobs.Items.Count);
        Assert.True(firstJobs.HasMore);
        Assert.Equal("job-00", firstJobs.Items[0].Name);
        Assert.Equal("job-49", firstJobs.Items[^1].Name);
        Assert.Equal(new[] { "job-50", "job-51" }, lastJobs.Items.Select(job => job.Name));
        Assert.False(lastJobs.HasMore);
        Assert.Equal(new[] { "job-03" }, matchingJobs.Items.Select(job => job.Name));

        Assert.Equal(50, firstTriggers.Items.Count);
        Assert.True(firstTriggers.HasMore);
        Assert.Equal("trigger-00", firstTriggers.Items[0].Name);
        Assert.Equal(new[] { "trigger-50", "trigger-51" }, lastTriggers.Items.Select(trigger => trigger.Name));
        Assert.False(lastTriggers.HasMore);
        Assert.Equal(new[] { "trigger-03" }, matchingTriggers.Items.Select(trigger => trigger.Name));
    }

    [Fact]
    public async Task Negative_page_offsets_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.GetJobsAsync(_scheduler.SchedulerName, null, -1, _ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.GetTriggersAsync(_scheduler.SchedulerName, null, -1, _ct));
    }

    [Fact]
    public async Task Unknown_scheduler_is_reported_instead_of_returning_an_empty_result()
    {
        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _queries.GetJobsAsync("not-registered", null, 0, _ct));

        Assert.Contains("not-registered", error.Message, StringComparison.Ordinal);
    }

    public async Task DisposeAsync()
    {
        await _scheduler.Shutdown(waitForJobsToComplete: true, _ct);
        foreach (var scheduler in _namedSchedulers)
            await scheduler.Shutdown(waitForJobsToComplete: true, _ct);
        await _services.DisposeAsync();
    }

    private sealed class NoopJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
