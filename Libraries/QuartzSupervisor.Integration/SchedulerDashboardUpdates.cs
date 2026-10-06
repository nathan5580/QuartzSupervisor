using Quartz;

namespace QuartzSupervisor.Integration;

public sealed record DashboardExecution(
    string Id,
    string SchedulerName,
    string JobName,
    string JobGroup,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool IsError)
{
    public bool IsManualRun { get; init; }
}

public interface ISchedulerDashboardUpdates
{
    event Action<string>? Changed;
    IReadOnlyList<DashboardExecution> GetRecentExecutions(string? schedulerName = null);
}

internal sealed class SchedulerDashboardEvents : ISchedulerDashboardUpdates
{
    private const int MaxExecutionsPerScheduler = 100;
    private readonly object _executionLock = new();
    private readonly Dictionary<string, List<DashboardExecution>> _executions = new(StringComparer.Ordinal);

    public event Action<string>? Changed;

    public IReadOnlyList<DashboardExecution> GetRecentExecutions(string? schedulerName = null)
    {
        lock (_executionLock)
            return schedulerName is null
                ? _executions.Values.SelectMany(runs => runs).OrderBy(run => run.StartedUtc).ToArray()
                : _executions.TryGetValue(schedulerName, out var runs) ? runs.ToArray() : [];
    }

    internal void StartExecution(IJobExecutionContext context)
    {
        var schedulerName = context.Scheduler.SchedulerName;
        var jobKey = context.JobDetail.Key;
        var execution = new DashboardExecution(
            context.FireInstanceId, schedulerName, jobKey.Name, jobKey.Group, DateTimeOffset.UtcNow, null, false)
        {
            IsManualRun = string.Equals(
                context.Trigger.Key.Group,
                SchedulerDashboardCommands.ManualRunTriggerGroup,
                StringComparison.Ordinal)
        };

        lock (_executionLock)
        {
            if (!_executions.TryGetValue(schedulerName, out var runs))
                _executions.Add(schedulerName, runs = []);
            runs.Add(execution);
            while (runs.Count > MaxExecutionsPerScheduler)
            {
                var completedIndex = runs.FindIndex(run => run.CompletedUtc is not null);
                if (completedIndex < 0)
                    break;
                runs.RemoveAt(completedIndex);
            }
        }

        Notify(schedulerName);
    }

    internal void CompleteExecution(IJobExecutionContext context, bool isError)
    {
        var schedulerName = context.Scheduler.SchedulerName;
        var jobKey = context.JobDetail.Key;
        lock (_executionLock)
        {
            if (_executions.TryGetValue(schedulerName, out var runs))
            {
                var index = runs.FindLastIndex(run => string.Equals(run.Id, context.FireInstanceId, StringComparison.Ordinal));
                if (index >= 0)
                {
                    var execution = runs[index];
                    runs[index] = execution with
                    {
                        CompletedUtc = execution.StartedUtc + context.JobRunTime,
                        IsError = isError
                    };
                }
            }
        }

        Notify(schedulerName);
    }

    internal void Notify(string schedulerName)
    {
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

    public ValueTask JobScheduled(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobUnscheduled(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerFinalized(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerError(IScheduler scheduler, SchedulerErrorContext errorContext, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerInError(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerPaused(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggersPaused(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerResumed(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggersResumed(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobAdded(IScheduler scheduler, IJobDetail jobDetail, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobDeleted(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobPaused(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobsPaused(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobResumed(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobsResumed(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerInStandbyMode(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerStarted(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerShuttingDown(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerShutdown(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulingDataCleared(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);

    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken ct)
    {
        events.StartExecution(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken ct)
    {
        events.CompleteExecution(context, jobException is not null);
        return ValueTask.CompletedTask;
    }

    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction instruction, CancellationToken ct) => Notify(context.Scheduler);

    private ValueTask Notify(IScheduler scheduler)
    {
        events.Notify(scheduler.SchedulerName);
        return ValueTask.CompletedTask;
    }
}

