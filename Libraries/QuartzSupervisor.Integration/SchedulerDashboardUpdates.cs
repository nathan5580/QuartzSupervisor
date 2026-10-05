using Quartz;

namespace QuartzSupervisor.Integration;

public sealed record DashboardActivity(
    DateTimeOffset TimestampUtc,
    string SchedulerName,
    string Category,
    string Message,
    bool IsError);

public interface ISchedulerDashboardUpdates
{
    event Action<string>? Changed;
    IReadOnlyList<DashboardActivity> GetRecentActivity(string? schedulerName = null);
}

internal sealed class SchedulerDashboardEvents : ISchedulerDashboardUpdates
{
    private const int MaxEventsPerScheduler = 100;
    private sealed record StoredActivity(long Sequence, DashboardActivity Activity);
    private readonly object _activityLock = new();
    private readonly Dictionary<string, Queue<StoredActivity>> _activity = new(StringComparer.Ordinal);
    private long _sequence;

    public event Action<string>? Changed;

    public IReadOnlyList<DashboardActivity> GetRecentActivity(string? schedulerName = null)
    {
        lock (_activityLock)
        {
            if (schedulerName is not null)
                return _activity.TryGetValue(schedulerName, out var events)
                    ? events.Reverse().Select(item => item.Activity).ToArray()
                    : [];

            return _activity.Values
                .SelectMany(events => events)
                .OrderByDescending(item => item.Sequence)
                .Take(MaxEventsPerScheduler)
                .Select(item => item.Activity)
                .ToArray();
        }
    }

    internal void Publish(string schedulerName, string? category = null, string? message = null, bool isError = false)
    {
        if (category is not null && message is not null)
        {
            lock (_activityLock)
            {
                if (!_activity.TryGetValue(schedulerName, out var events))
                    _activity.Add(schedulerName, events = new Queue<StoredActivity>());

                events.Enqueue(new StoredActivity(++_sequence, new DashboardActivity(DateTimeOffset.UtcNow, schedulerName, category, message, isError)));
                while (events.Count > MaxEventsPerScheduler)
                    events.Dequeue();
            }
        }

        if (Changed is not { } handlers)
            return;

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(schedulerName); }
            catch (Exception) { }
        }
    }
}

internal sealed class SchedulerDashboardListenerRegistration(SchedulerDashboardEvents events)
{
    private readonly HashSet<IScheduler> _attached = [];

    public void EnsureAttached(IScheduler scheduler)
    {
        lock (_attached)
        {
            if (!_attached.Add(scheduler))
                return;

            var listener = new SchedulerDashboardChangeListener(events);
            IListenerManager manager;
            try { manager = scheduler.ListenerManager; }
            catch (NotSupportedException) { return; } // Remote schedulers own listeners in their process.

            try
            {
                manager.AddSchedulerListener(listener);
                manager.AddJobListener(listener, [Matchers.AllJobs()]);
                manager.AddTriggerListener(listener, [Matchers.AllTriggers()]);
            }
            catch
            {
                _attached.Remove(scheduler);
                throw;
            }
        }
    }
}

internal sealed class SchedulerDashboardChangeListener(SchedulerDashboardEvents events) :
    ISchedulerListener, IJobListener, ITriggerListener
{
    public string Name => nameof(SchedulerDashboardChangeListener);

    public ValueTask JobScheduled(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Record(scheduler, "Schedule", $"Trigger {trigger.Key} scheduled for job {trigger.JobKey}.");
    public ValueTask JobUnscheduled(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Record(scheduler, "Schedule", $"Trigger {triggerKey} unscheduled.");
    public ValueTask TriggerFinalized(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Record(scheduler, "Schedule", $"Trigger {trigger.Key} finalized.");
    public ValueTask SchedulerError(IScheduler scheduler, SchedulerErrorContext errorContext, CancellationToken ct) => Record(scheduler, "Error", "Quartz reported a scheduler error.", true);
    public ValueTask TriggerInError(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Record(scheduler, "Error", $"Trigger {triggerKey} entered an error state.", true);
    public ValueTask TriggerPaused(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Record(scheduler, "Schedule", $"Trigger {triggerKey} paused.");
    public ValueTask TriggersPaused(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Record(scheduler, "Schedule", $"Triggers in group {triggerGroup ?? "all"} paused.");
    public ValueTask TriggerResumed(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Record(scheduler, "Schedule", $"Trigger {triggerKey} resumed.");
    public ValueTask TriggersResumed(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Record(scheduler, "Schedule", $"Triggers in group {triggerGroup ?? "all"} resumed.");
    public ValueTask JobAdded(IScheduler scheduler, IJobDetail jobDetail, CancellationToken ct) => Record(scheduler, "Job", $"Job {jobDetail.Key} added.");
    public ValueTask JobDeleted(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Record(scheduler, "Job", $"Job {jobKey} deleted.");
    public ValueTask JobPaused(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Record(scheduler, "Job", $"Job {jobKey} paused.");
    public ValueTask JobsPaused(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Record(scheduler, "Job", $"Jobs in group {jobGroup ?? "all"} paused.");
    public ValueTask JobResumed(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Record(scheduler, "Job", $"Job {jobKey} resumed.");
    public ValueTask JobsResumed(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Record(scheduler, "Job", $"Jobs in group {jobGroup ?? "all"} resumed.");
    public ValueTask SchedulerInStandbyMode(IScheduler scheduler, CancellationToken ct) => Record(scheduler, "Scheduler", "Scheduler entered standby.");
    public ValueTask SchedulerStarted(IScheduler scheduler, CancellationToken ct) => Record(scheduler, "Scheduler", "Scheduler started.");
    public ValueTask SchedulerShuttingDown(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerShutdown(IScheduler scheduler, CancellationToken ct) => Record(scheduler, "Scheduler", "Scheduler shut down.");
    public ValueTask SchedulingDataCleared(IScheduler scheduler, CancellationToken ct) => Record(scheduler, "Scheduler", "Scheduler data cleared.");
    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken ct) => Record(context.Scheduler, "Execution", $"Job {context.JobDetail.Key} started.");
    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken ct) =>
        Record(context.Scheduler, jobException is null ? "Execution" : "Error",
            $"Job {context.JobDetail.Key} {(jobException is null ? "completed" : "failed")}.", jobException is not null);
    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction instruction, CancellationToken ct) => Notify(context.Scheduler);

    private ValueTask Record(IScheduler scheduler, string category, string message, bool isError = false)
    {
        events.Publish(scheduler.SchedulerName, category, message, isError);
        return ValueTask.CompletedTask;
    }

    private ValueTask Notify(IScheduler scheduler)
    {
        events.Publish(scheduler.SchedulerName);
        return ValueTask.CompletedTask;
    }
}


