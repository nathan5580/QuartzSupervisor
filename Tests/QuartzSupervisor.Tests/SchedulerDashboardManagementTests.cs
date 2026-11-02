using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Extensibility;
using QuartzSupervisor.Integration;
using Xunit;

namespace QuartzSupervisor.Tests;

public sealed class SchedulerDashboardManagementTests : IAsyncLifetime
{
    private ServiceProvider services = null!;
    private IScheduler scheduler = null!;
    private ISchedulerDashboardQueries queries = null!;
    private ISchedulerDashboardCommands commands = null!;
    private readonly MutableAccess _access = new();
    private readonly CancellationToken _ct = CancellationToken.None;
    private readonly List<IHostedService> _hosted = [];
    public async Task InitializeAsync()
    {
        var collection = new ServiceCollection();
        collection.AddQuartz(q => { q.ConfigureScheduler(o => o.InstanceName = $"management-{Guid.NewGuid():N}"); q.UseInMemoryStore(); });
        collection.AddSingleton<ISchedulerDashboardAccess>(_access);
        collection.AddQuartzSupervisorIntegration(o => o.JobTypes.Add(new("approved", "Approved", typeof(ApprovedJob).AssemblyQualifiedName!,
            [new("count", "Count", "int", true), new("secret", "Secret", Sensitive: true)])));
        collection.Configure<ExecutionHistoryOptions>(o => { o.MaxEntriesPerScheduler = 3; o.Retention = TimeSpan.FromHours(1); });
        services = collection.BuildServiceProvider();
        scheduler = await services.GetRequiredService<ISchedulerFactory>().GetScheduler(_ct);
        foreach (var service in services.GetServices<IHostedService>()) { await service.StartAsync(_ct); _hosted.Add(service); }
        queries = services.GetRequiredService<ISchedulerDashboardQueries>();
        commands = services.GetRequiredService<ISchedulerDashboardCommands>();
    }
    private Task Create(string name = "job") => commands.CreateJobAsync(scheduler.SchedulerName, new JobDefinition
    { Name = name, JobTypeKey = "approved", Parameters = new() { ["count"] = "2", ["secret"] = "hidden-credential" } }, _ct);
    private TriggerDefinition Trigger(string name = "trigger") => new()
    {
        Name = name,
        JobName = "job",
        Kind = "Cron",
        CronExpression = "0 0 9 * * ?",
        TimeZoneId = "Europe/Paris",
        StartUtc = DateTimeOffset.UtcNow.AddMinutes(1)
    };
    [Fact]
    public async Task Catalog_validation_and_secrets_apply_to_real_job_consumers()
    {
        await Create();
        var job = await queries.GetJobAsync(scheduler.SchedulerName, "DEFAULT", "job", _ct);
        Assert.Equal("2", job!.Parameters["count"]);
        Assert.False(job.Parameters.ContainsKey("secret"));
        Assert.True(Assert.Single(job.ParameterSchema, p => p.Name == "secret").Sensitive);
        await Assert.ThrowsAsync<ArgumentException>(() => commands.CreateJobAsync(scheduler.SchedulerName, new JobDefinition { Name = "bad", JobTypeKey = "arbitrary-type" }, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => commands.TriggerJobAsync(scheduler.SchedulerName, "DEFAULT", "job", _ct, new Dictionary<string, string> { ["count"] = "not-an-int" }));
        Assert.Empty((await queries.GetTriggersAsync(scheduler.SchedulerName, "run-", 0, _ct)).Items);
        await commands.TriggerJobAsync(scheduler.SchedulerName, "DEFAULT", "job", _ct, new Dictionary<string, string> { ["count"] = "4" });
        var manual = Assert.Single((await queries.GetTriggersAsync(scheduler.SchedulerName, "run-", 0, _ct)).Items);
        var native = await scheduler.GetTrigger(new TriggerKey(manual.Name, manual.Group), _ct);
        Assert.Equal(4, native!.JobDataMap["count"]);
        var audit = await queries.GetAuditAsync(scheduler.SchedulerName, _ct);
        Assert.DoesNotContain(audit, e => (e.Error ?? "").Contains("hidden-credential", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Trigger_revisions_reject_stale_edits_and_preview_obeys_timezone()
    {
        await Create();
        await commands.SaveTriggerAsync(scheduler.SchedulerName, Trigger(), false, _ct);
        var initial = (await queries.GetTriggerAsync(scheduler.SchedulerName, "DEFAULT", "trigger", _ct))!;
        var staleRevision = initial.Definition.Revision;
        var preview = await queries.PreviewTriggerAsync(scheduler.SchedulerName, initial.Definition, _ct);
        Assert.Equal(10, preview.Count);
        Assert.All(preview, time => Assert.Equal(9, TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris")).Hour));
        initial.Definition.Description = "first edit";
        await commands.SaveTriggerAsync(scheduler.SchedulerName, initial.Definition, true, _ct);
        initial.Definition.Revision = staleRevision;
        initial.Definition.Description = "stale edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SaveTriggerAsync(scheduler.SchedulerName, initial.Definition, true, _ct));
        Assert.Equal("first edit", (await scheduler.GetTrigger(new TriggerKey("trigger"), _ct))!.Description);
        initial.Definition.TimeZoneId = "no-such-zone";
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() => queries.PreviewTriggerAsync(scheduler.SchedulerName, initial.Definition, _ct));
    }
    [Fact]
    public async Task Calendar_preview_excludes_days_and_references_prevent_deletion()
    {
        await Create();
        var calendar = new CalendarDefinition { Name = "weekday", Kind = "Weekly", TimeZoneId = "Europe/Paris", ExcludedDays = [DayOfWeek.Saturday, DayOfWeek.Sunday] };
        await commands.SaveCalendarAsync(scheduler.SchedulerName, calendar, false, _ct);
        var trigger = Trigger(); trigger.CalendarName = calendar.Name;
        await commands.SaveTriggerAsync(scheduler.SchedulerName, trigger, false, _ct);
        var preview = await queries.PreviewTriggerAsync(scheduler.SchedulerName, trigger, _ct);
        Assert.DoesNotContain(preview, time => TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId)).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var current = Assert.Single(await queries.GetCalendarsAsync(scheduler.SchedulerName, _ct));
        Assert.Single(current.AffectedTriggers);
        Assert.Single(await queries.PreviewCalendarAsync(scheduler.SchedulerName, current.Definition, _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.DeleteCalendarAsync(scheduler.SchedulerName, calendar.Name, _ct));
        current.Definition.ExcludedDays = [DayOfWeek.Sunday];
        await commands.SaveCalendarAsync(scheduler.SchedulerName, current.Definition, true, _ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SaveCalendarAsync(scheduler.SchedulerName, current.Definition, true, _ct));
        await commands.DeleteTriggerAsync(scheduler.SchedulerName, "DEFAULT", "trigger", _ct);
        Assert.True(await commands.DeleteCalendarAsync(scheduler.SchedulerName, calendar.Name, _ct));
    }
    [Fact]
    public async Task All_command_families_authorize_before_validation_or_scheduler_lookup()
    {
        _access.Deny = true;
        var name = "not-registered";
        Func<Task>[] attempts =
        [
            () => commands.StartSchedulerAsync(name, _ct), () => commands.StandbySchedulerAsync(name, _ct),
            () => commands.TriggerJobAsync(name, "", "", _ct), () => commands.PauseJobAsync(name, "", "", _ct),
            () => commands.ResumeJobAsync(name, "", "", _ct), () => commands.DeleteJobAsync(name, "", "", _ct),
            () => commands.PauseTriggerAsync(name, "", "", _ct), () => commands.ResumeTriggerAsync(name, "", "", _ct),
            () => commands.DeleteTriggerAsync(name, "", "", _ct), () => commands.ResetTriggerAsync(name, "", "", _ct),
            () => commands.InterruptExecutionAsync(name, "", _ct), () => commands.CreateJobAsync(name, new(), _ct),
            () => commands.SaveTriggerAsync(name, new(), true, _ct), () => commands.SaveCalendarAsync(name, new(), true, _ct),
            () => commands.DeleteCalendarAsync(name, "", _ct), () => commands.BulkJobsAsync(name, [], "invalid", _ct),
            () => commands.BulkTriggersAsync(name, [], "invalid", _ct)
        ];
        foreach (var attempt in attempts) await Assert.ThrowsAsync<UnauthorizedAccessException>(attempt);
        Assert.Empty(await queries.GetSchedulersAsync(_ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.GetHistoryAsync(name, new(), _ct));
    }
    [Fact]
    public async Task Operate_permission_does_not_grant_destructive_or_configuration_commands()
    {
        await Create();
        _access.Maximum = DashboardPermission.Operate;
        await commands.StandbySchedulerAsync(scheduler.SchedulerName, _ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.DeleteJobAsync(scheduler.SchedulerName, "DEFAULT", "job", _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.SaveTriggerAsync(scheduler.SchedulerName, Trigger(), false, _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queries.GetAuditAsync(scheduler.SchedulerName, _ct));
        Assert.NotNull(await scheduler.GetJobDetail(new JobKey("job"), _ct));
    }
    [Fact]
    public async Task Native_retention_and_node_attempt_identity_survive_query_projection()
    {
        var store = services.GetRequiredService<IExecutionHistoryStore>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.AddExecution(new(scheduler.SchedulerName, $"node-{i % 2}", "group", "job", "triggers", "trigger", now.AddSeconds(i - 5), TimeSpan.FromSeconds(1), i % 2 == 0, "hidden-credential")
            { EntryId = $"entry-{i}", RetryAttempt = i, Log = "hidden-credential log" }, _ct);
        var history = await queries.GetHistoryAsync(scheduler.SchedulerName, new(), _ct);
        Assert.True(history.Available);
        Assert.Equal(3, history.Page.Items.Count);
        Assert.Equal(3, history.Page.Items.Select(e => e.Id).Distinct().Count());
        Assert.All(history.Page.Items, e => { Assert.Null(e.ErrorMessage); Assert.Null(e.Log); });
        var id = history.Page.Items[0].Id;
        Assert.Equal(id, (await queries.GetExecutionAsync(scheduler.SchedulerName, id, _ct))!.Id);
        Assert.Single((await queries.GetHistoryAsync(scheduler.SchedulerName, new() { InstanceId = "node-1" }, _ct)).Page.Items);
        services.GetRequiredService<SchedulerDashboardOptions>().RedactDiagnostic = value => value.Replace("hidden-credential", "[redacted]", StringComparison.Ordinal);
        var detail = await queries.GetExecutionAsync(scheduler.SchedulerName, id, _ct);
        Assert.Contains("[redacted]", detail!.Log);
        Assert.DoesNotContain("hidden-credential", detail.Log);
        Assert.Empty((await queries.GetHistoryAsync(scheduler.SchedulerName, new() { TriggerName = "other" }, _ct)).Page.Items);
    }
    [Fact]
    public async Task Timeline_is_bounded_and_paused_triggers_are_not_projected()
    {
        await Create();
        var trigger = Trigger(); trigger.Kind = "Simple"; trigger.IntervalSeconds = 1;
        await commands.SaveTriggerAsync(scheduler.SchedulerName, trigger, false, _ct);
        services.GetRequiredService<SchedulerDashboardOptions>().MaxTimelineItems = 3;
        var timeline = await queries.GetTimelineAsync(scheduler.SchedulerName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, _ct);
        Assert.Equal(3, timeline.Scheduled.Count);
        Assert.True(timeline.Truncated);
        await commands.PauseTriggerAsync(scheduler.SchedulerName, "DEFAULT", "trigger", _ct);
        Assert.Empty((await queries.GetTimelineAsync(scheduler.SchedulerName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, _ct)).Scheduled);
    }
    [Fact]
    public async Task Startup_observation_notifies_consumers_before_first_query()
    {
        var updates = services.GetRequiredService<ISchedulerDashboardUpdates>();
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(string name) => changed.TrySetResult(name);
        updates.Changed += OnChanged;
        try
        {
            await scheduler.AddJob(JobBuilder.Create<ApprovedJob>().WithIdentity("external").StoreDurably().Build(), new(), _ct);
            Assert.Equal(scheduler.SchedulerName, await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { updates.Changed -= OnChanged; }
    }
    [Fact]
    public async Task Audit_sink_failure_does_not_turn_success_into_retryable_failure()
    {
        services.GetRequiredService<SchedulerDashboardOptions>().AuditSink = (_, _) => throw new IOException("secret sink failure");
        await Create();
        Assert.NotNull(await scheduler.GetJobDetail(new JobKey("job"), _ct));
        Assert.True(Assert.Single(await queries.GetAuditAsync(scheduler.SchedulerName, _ct)).Succeeded);
    }
    [Fact]
    public async Task Interrupt_requests_cancellation_without_claiming_the_execution_completed()
    {
        CooperativeJob.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CooperativeJob.Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CooperativeJob.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await scheduler.AddJob(JobBuilder.Create<CooperativeJob>().WithIdentity("cooperative").StoreDurably().Build(), new(), _ct);
        var triggerKey = new TriggerKey("cooperative-schedule");
        await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(new JobKey("cooperative"))
            .StartNow().WithSimpleSchedule(TimeSpan.FromMinutes(10)).Build(), new(), _ct);
        await scheduler.Start(_ct);
        try
        {
            await CooperativeJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = Assert.Single(await queries.GetRunningAsync(scheduler.SchedulerName, _ct));
            Assert.NotNull(running.FireInstanceId);
            Assert.Equal(TriggerState.Executing, await scheduler.GetTriggerState(triggerKey, _ct));
            Assert.Contains((await queries.GetOverviewAsync(scheduler.SchedulerName, _ct)).UpcomingTriggers, t => t.Name == triggerKey.Name);
            Assert.NotNull((await queries.GetJobAsync(scheduler.SchedulerName, "DEFAULT", "cooperative", _ct))!.Summary.NextFireTimeUtc);
            Assert.Contains((await queries.GetTimelineAsync(scheduler.SchedulerName, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1), null, _ct)).Scheduled, t => t.TriggerName == triggerKey.Name);
            await commands.PauseTriggerAsync(scheduler.SchedulerName, triggerKey.Group, triggerKey.Name, _ct);
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(triggerKey, _ct));
            Assert.Empty((await queries.GetTimelineAsync(scheduler.SchedulerName, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1), null, _ct)).Scheduled);
            Assert.False(await commands.InterruptExecutionAsync(scheduler.SchedulerName, "unknown-fire", _ct));
            Assert.True(await commands.InterruptExecutionAsync(scheduler.SchedulerName, running.FireInstanceId!, _ct));
            await CooperativeJob.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(Assert.Single(await queries.GetRunningAsync(scheduler.SchedulerName, _ct)).CompletedUtc);
            Assert.Empty((await queries.GetHistoryAsync(scheduler.SchedulerName, new(), _ct)).Page.Items);
        }
        finally
        {
            CooperativeJob.Release.TrySetResult();
            await scheduler.Standby(_ct);
        }
    }
    public async Task DisposeAsync()
    {
        foreach (var service in _hosted.AsEnumerable().Reverse()) await service.StopAsync(_ct);
        await scheduler.Shutdown(false, _ct);
        await services.DisposeAsync();
    }
    private sealed class MutableAccess : ISchedulerDashboardAccess
    {
        internal bool Deny { get; set; }
        internal DashboardPermission Maximum { get; set; } = DashboardPermission.Administer;
        public ValueTask<string> AuthorizeAsync(string schedulerName, DashboardPermission permission, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Deny || permission > Maximum) throw new UnauthorizedAccessException();
            return ValueTask.FromResult("operator");
        }
    }
    public sealed class CooperativeJob : IJob
    {
        public static TaskCompletionSource Started { get; set; } = new();
        public static TaskCompletionSource Cancelled { get; set; } = new();
        public static TaskCompletionSource Release { get; set; } = new();
        public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            await Release.Task;
        }
    }
    public sealed class ApprovedJob : IJob
    {
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
