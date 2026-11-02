using Quartz;

namespace QuartzSupervisor.Integration;

internal sealed class SchedulerDashboardCommands(DashboardBackend backend, ISchedulerDashboardAccess access, DashboardAudit audit, SchedulerDashboardEvents events) : ISchedulerDashboardCommands
{
    internal const string ManualRunTriggerGroup = "QuartzSupervisor.ManualRuns";
    private async Task<T> Execute<T>(string schedulerName, DashboardPermission permission, string action, string target, CancellationToken ct, Func<IScheduler, Task<T>> operation)
    {
        string actor = "unauthorized";
        bool entered = false;
        try
        {
            actor = await access.AuthorizeAsync(schedulerName, permission, ct).ConfigureAwait(false);
            await audit.MutationGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var scheduler = await backend.Resolve(schedulerName, ct).ConfigureAwait(false);
            var result = await operation(scheduler).ConfigureAwait(false);
            await audit.Record(actor, schedulerName, action, target, result is not bool b || b, null).ConfigureAwait(false);
            events.Notify(schedulerName);
            return result;
        }
        catch (Exception ex)
        {
            await audit.Record(actor, schedulerName, action, target, false, ex).ConfigureAwait(false);
            throw;
        }
        finally { if (entered) audit.MutationGate.Release(); }
    }
    private Task Execute(string schedulerName, DashboardPermission permission, string action, string target, CancellationToken ct, Func<IScheduler, Task> operation) =>
        Execute(schedulerName, permission, action, target, ct, async scheduler => { await operation(scheduler).ConfigureAwait(false); return true; });
    private static JobKey Job(string group, string name) => new(DashboardBackend.Text(name, "Job name"), DashboardBackend.Text(group, "Job group"));
    private static TriggerKey Trigger(string group, string name) => new(DashboardBackend.Text(name, "Trigger name"), DashboardBackend.Text(group, "Trigger group"));
    public Task StartSchedulerAsync(string schedulerName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "scheduler.start", schedulerName, ct, async s => await s.Start(ct).ConfigureAwait(false));
    public Task StandbySchedulerAsync(string schedulerName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "scheduler.standby", schedulerName, ct, async s => await s.Standby(ct).ConfigureAwait(false));
    public Task TriggerJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct, IReadOnlyDictionary<string, string>? parameters = null) =>
        Execute(schedulerName, DashboardPermission.Operate, "job.run", $"{jobGroup}/{jobName}", ct, async s =>
        {
            var key = Job(jobGroup, jobName);
            var job = await s.GetJobDetail(key, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Job no longer exists.");
            var catalog = backend.Catalog(job);
            var data = DashboardBackend.Parameters(catalog, parameters ?? new Dictionary<string, string>(), false);
            if (catalog?.Parameters.Any(p => p.Required && !data.ContainsKey(p.Name) && !job.JobDataMap.ContainsKey(p.Name)) == true)
                throw new ArgumentException("A required job parameter is missing.");
            var trigger = TriggerBuilder.Create().WithIdentity($"run-{Guid.NewGuid():N}", ManualRunTriggerGroup).ForJob(key).UsingJobData(data).StartNow().Build();
            await s.ScheduleJob(trigger, new ScheduleJobOptions(), ct).ConfigureAwait(false);
        });
    public Task PauseJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "job.pause", $"{jobGroup}/{jobName}", ct, async s => await s.PauseJob(Job(jobGroup, jobName), ct).ConfigureAwait(false));
    public Task ResumeJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "job.resume", $"{jobGroup}/{jobName}", ct, async s => await s.ResumeJob(Job(jobGroup, jobName), ct).ConfigureAwait(false));
    public Task<bool> DeleteJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, "job.delete", $"{jobGroup}/{jobName}", ct, async s => await s.DeleteJob(Job(jobGroup, jobName), ct).ConfigureAwait(false));
    public Task PauseTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "trigger.pause", $"{triggerGroup}/{triggerName}", ct, async s => await s.PauseTrigger(Trigger(triggerGroup, triggerName), ct).ConfigureAwait(false));
    public Task ResumeTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "trigger.resume", $"{triggerGroup}/{triggerName}", ct, async s => await s.ResumeTrigger(Trigger(triggerGroup, triggerName), ct).ConfigureAwait(false));
    public Task<bool> DeleteTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, "trigger.delete", $"{triggerGroup}/{triggerName}", ct, async s => await s.UnscheduleJob(Trigger(triggerGroup, triggerName), ct).ConfigureAwait(false));
    public Task ResetTriggerAsync(string schedulerName, string group, string name, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "trigger.reset", $"{group}/{name}", ct, async s => await s.ResetTriggerFromErrorState(Trigger(group, name), ct).ConfigureAwait(false));
    public Task<bool> InterruptExecutionAsync(string schedulerName, string fireInstanceId, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Operate, "execution.interrupt-request", fireInstanceId, ct, async s => await s.InterruptFireInstance(DashboardBackend.Text(fireInstanceId, "Fire instance id", 512), ct).ConfigureAwait(false));
    public Task CreateJobAsync(string schedulerName, JobDefinition definition, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, "job.create", $"{definition.Group}/{definition.Name}", ct, async s =>
    {
        var key = Job(definition.Group, definition.Name);
        DashboardBackend.Description(definition.Description);
        var catalog = backend.Options.JobTypes.SingleOrDefault(x => x.Key == definition.JobTypeKey) ?? throw new ArgumentException("Choose a host-approved job type.");
        var type = Type.GetType(catalog.TypeName, throwOnError: true)!;
        if (!typeof(IJob).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters) throw new ArgumentException("The approved type must be a concrete Quartz job.");
        var job = JobBuilder.Create().OfType(type).WithIdentity(key).WithDescription(definition.Description).StoreDurably(definition.Durable)
            .RequestRecovery(definition.RequestsRecovery).UsingJobData(DashboardBackend.Parameters(catalog, definition.Parameters, true)).Build();
        await s.AddJob(job, new AddJobOptions { StoreNonDurableWhileAwaitingScheduling = !definition.Durable }, ct).ConfigureAwait(false);
    });
    public Task SaveTriggerAsync(string schedulerName, TriggerDefinition definition, bool replace, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, replace ? "trigger.update" : "trigger.create", $"{definition.Group}/{definition.Name}", ct, async s =>
    {
        var key = Trigger(definition.Group, definition.Name);
        var original = await s.GetTrigger(key, ct).ConfigureAwait(false);
        if (replace)
        {
            if (original is null) throw new InvalidOperationException("Trigger was removed. Reload before saving.");
            if (!DashboardBackend.Editable(original)) throw new ArgumentException("This trigger family is read only.");
            DashboardBackend.CheckRevision(definition.Revision, DashboardBackend.Definition(original).Revision!);
        }
        else if (original is not null) throw new InvalidOperationException("A trigger with this key already exists.");
        if (await s.GetJobDetail(Job(definition.JobGroup, definition.JobName), ct).ConfigureAwait(false) is null) throw new ArgumentException("Target job does not exist.");
        if (!string.IsNullOrWhiteSpace(definition.CalendarName) && await s.GetCalendar(definition.CalendarName, ct).ConfigureAwait(false) is null) throw new ArgumentException("Calendar does not exist.");
        var trigger = DashboardBackend.Build(definition, original);
        if (replace)
        {
            if (await s.RescheduleJob(key, trigger, ct).ConfigureAwait(false) is null) throw new InvalidOperationException("Trigger was removed before saving.");
        }
        else await s.ScheduleJob(trigger, new ScheduleJobOptions(), ct).ConfigureAwait(false);
    });
    public Task SaveCalendarAsync(string schedulerName, CalendarDefinition definition, bool replace, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, replace ? "calendar.update" : "calendar.create", definition.Name, ct, async s =>
    {
        DashboardBackend.Text(definition.Name, "Calendar name");
        var original = await s.GetCalendar(definition.Name, ct).ConfigureAwait(false);
        if (replace)
        {
            if (original is null) throw new InvalidOperationException("Calendar was removed. Reload before saving.");
            var current = DashboardBackend.Definition(definition.Name, original);
            if (current.Kind == "Unsupported") throw new ArgumentException("This calendar family is read only.");
            DashboardBackend.CheckRevision(definition.Revision, current.Revision!);
        }
        else if (original is not null) throw new InvalidOperationException("A calendar with this name already exists.");
        await s.AddCalendar(definition.Name, DashboardBackend.Build(definition), new AddCalendarOptions { Replace = replace, UpdateTriggers = replace }, ct).ConfigureAwait(false);
    });
    public Task<bool> DeleteCalendarAsync(string schedulerName, string name, CancellationToken ct) => Execute(schedulerName, DashboardPermission.Administer, "calendar.delete", name, ct, async s =>
    {
        DashboardBackend.Text(name, "Calendar name");
        var affected = await s.QueryTriggers(new TriggerQuery { CalendarName = name, Take = 1 }, ct).ConfigureAwait(false);
        if (affected.Items.Count > 0) throw new InvalidOperationException("Calendar is still used by triggers; detach those triggers before deleting it.");
        return await s.DeleteCalendar(name, ct).ConfigureAwait(false);
    });
    public Task<IReadOnlyList<BulkResult>> BulkJobsAsync(string schedulerName, IReadOnlyList<DashboardKey> keys, string action, CancellationToken ct) => Bulk(schedulerName, keys, action, false, ct);
    public Task<IReadOnlyList<BulkResult>> BulkTriggersAsync(string schedulerName, IReadOnlyList<DashboardKey> keys, string action, CancellationToken ct) => Bulk(schedulerName, keys, action, true, ct);
    private async Task<IReadOnlyList<BulkResult>> Bulk(string schedulerName, IReadOnlyList<DashboardKey> keys, string action, bool triggers, CancellationToken ct)
    {
        await access.AuthorizeAsync(schedulerName, action == "delete" ? DashboardPermission.Administer : DashboardPermission.Operate, ct).ConfigureAwait(false);
        if (action is not ("pause" or "resume" or "delete") || keys.Count is < 1 or > 100) throw new ArgumentException("Choose pause, resume or delete and between 1 and 100 keys.");
        // Validate the entire request before making its first mutation.
        foreach (var key in keys) { DashboardBackend.Text(key.Group, "Group"); DashboardBackend.Text(key.Name, "Name"); }
        var results = new List<BulkResult>();
        foreach (var key in keys.Distinct())
        {
            try
            {
                bool success = true;
                if (triggers)
                {
                    if (action == "pause") await PauseTriggerAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                    else if (action == "resume") await ResumeTriggerAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                    else success = await DeleteTriggerAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                }
                else
                {
                    if (action == "pause") await PauseJobAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                    else if (action == "resume") await ResumeJobAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                    else success = await DeleteJobAsync(schedulerName, key.Group, key.Name, ct).ConfigureAwait(false);
                }
                results.Add(new(key.Group, key.Name, success, success ? null : "No matching object remains."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex) when (ex is SchedulerException or HttpRequestException or InvalidOperationException) { results.Add(new(key.Group, key.Name, false, "The operation failed; inspect the protected host logs.")); }
        }
        return results;
    }
}
