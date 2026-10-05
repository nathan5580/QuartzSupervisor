using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;

namespace QuartzSupervisor.Integration;

public sealed record SchedulerSummary(string Name, string Status);

public sealed record JobSummary(string Group, string Name, string? Description, bool Durable, bool RequestsRecovery);

public sealed record TriggerSummary(
    string Group,
    string Name,
    string JobGroup,
    string JobName,
    string State,
    DateTimeOffset? NextFireTimeUtc,
    DateTimeOffset? PreviousFireTimeUtc);

public sealed record DashboardOverview(SchedulerSummary Scheduler, int JobCount, int TriggerCount, IReadOnlyList<TriggerSummary> UpcomingTriggers);
public sealed record DashboardPage<T>(IReadOnlyList<T> Items, bool HasMore, int Skip, int PageSize);

public interface ISchedulerDashboardQueries
{
    Task<DashboardOverview> GetOverviewAsync(string schedulerName, CancellationToken ct);
    Task<IReadOnlyList<SchedulerSummary>> GetSchedulersAsync(CancellationToken ct);
    Task<DashboardPage<JobSummary>> GetJobsAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct);
    Task<DashboardPage<TriggerSummary>> GetTriggersAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct);
}

public interface ISchedulerDashboardCommands
{
    Task StartSchedulerAsync(string schedulerName, CancellationToken ct);
    Task StandbySchedulerAsync(string schedulerName, CancellationToken ct);
    Task TriggerJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task PauseJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task ResumeJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task<bool> DeleteJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task PauseTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
    Task ResumeTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
    Task<bool> DeleteTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
}

public static class SchedulerDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddQuartzSupervisorDashboard(this IServiceCollection services)
    {
        services.TryAddSingleton<SchedulerDashboardEvents>();
        services.TryAddSingleton<ISchedulerDashboardUpdates>(provider => provider.GetRequiredService<SchedulerDashboardEvents>());
        services.TryAddSingleton<SchedulerDashboardListenerRegistration>();
        services.TryAddSingleton<ISchedulerDashboardQueries, SchedulerDashboardQueries>();
        services.TryAddSingleton<ISchedulerDashboardCommands, SchedulerDashboardCommands>();
        return services;
    }
}

internal sealed class SchedulerDashboardQueries(
    ISchedulerFactory schedulerFactory,
    SchedulerDashboardListenerRegistration listeners) : ISchedulerDashboardQueries
{
    public async Task<IReadOnlyList<SchedulerSummary>> GetSchedulersAsync(CancellationToken ct)
    {
        var schedulers = await schedulerFactory.GetAllSchedulers(ct).ConfigureAwait(false);
        var result = new List<SchedulerSummary>(schedulers.Count);
        foreach (var scheduler in schedulers.OrderBy(x => x.SchedulerName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                listeners.EnsureAttached(scheduler);
                result.Add(await SummarizeAsync(scheduler, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                result.Add(new SchedulerSummary(scheduler.SchedulerName, "Unavailable"));
            }
            catch (Exception ex) when (ex is SchedulerException or HttpRequestException)
            {
                result.Add(new SchedulerSummary(scheduler.SchedulerName, "Unavailable"));
            }
        }
        result.Sort((left, right) =>
        {
            var availability = (left.Status == "Unavailable").CompareTo(right.Status == "Unavailable");
            return availability != 0 ? availability : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });
        return result;
    }

    public async Task<DashboardOverview> GetOverviewAsync(string schedulerName, CancellationToken ct)
    {
        var scheduler = await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false);
        var summary = await SummarizeAsync(scheduler, ct).ConfigureAwait(false);
        var jobs = await scheduler.QueryJobs(new JobQuery { Take = 0, IncludeTotalCount = true }, ct).ConfigureAwait(false);
        var triggers = await scheduler.QueryTriggers(new TriggerQuery { Take = PagedQuery.All }, ct).ConfigureAwait(false);

        // ponytail: scan compact headers once because Quartz orders query pages by key, not next fire time.
        var upcoming = triggers.Items
            .Where(trigger => trigger.NextFireTimeUtc is not null)
            .OrderBy(trigger => trigger.NextFireTimeUtc)
            .ThenBy(trigger => trigger.Key.Group, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Key.Name, StringComparer.Ordinal)
            .Take(5)
            .Select(ToSummary)
            .ToArray();

        return new DashboardOverview(
            summary,
            jobs.TotalCount ?? throw new InvalidOperationException("Quartz did not return the requested job count."),
            triggers.Items.Count,
            upcoming);
    }
    public async Task<DashboardPage<JobSummary>> GetJobsAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        var scheduler = await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false);
        var jobs = await scheduler.QueryJobs(new JobQuery
        {
            Name = string.IsNullOrWhiteSpace(nameFilter) ? null : NameMatcher<JobKey>.NameContains(nameFilter.Trim()),
            Skip = skip,
            Take = PageSize
        }, ct).ConfigureAwait(false);

        return new DashboardPage<JobSummary>(
            jobs.Items.Select(job => new JobSummary(job.Key.Group, job.Key.Name, job.Description, job.Durable, job.RequestsRecovery)).ToArray(),
            jobs.HasMore,
            skip,
            PageSize);
    }
    public async Task<DashboardPage<TriggerSummary>> GetTriggersAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        var scheduler = await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false);
        var triggers = await scheduler.QueryTriggers(new TriggerQuery
        {
            Name = string.IsNullOrWhiteSpace(nameFilter) ? null : NameMatcher<TriggerKey>.NameContains(nameFilter.Trim()),
            Skip = skip,
            Take = PageSize
        }, ct).ConfigureAwait(false);
        return new DashboardPage<TriggerSummary>(triggers.Items.Select(ToSummary).ToArray(), triggers.HasMore, skip, PageSize);
    }
    private const int PageSize = 50;

    private static TriggerSummary ToSummary(TriggerHeader trigger) =>
        new(trigger.Key.Group, trigger.Key.Name, trigger.JobKey.Group, trigger.JobKey.Name, trigger.State.ToString(),
            trigger.NextFireTimeUtc, trigger.PreviousFireTimeUtc);



    private async Task<IScheduler> GetSchedulerAsync(string name, CancellationToken ct)
    {
        var schedulers = await schedulerFactory.GetAllSchedulers(ct).ConfigureAwait(false);
        var scheduler = schedulers.FirstOrDefault(x => string.Equals(x.SchedulerName, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"No registered scheduler named '{name}'.");
        listeners.EnsureAttached(scheduler);
        return scheduler;

    }
    private static async Task<SchedulerSummary> SummarizeAsync(IScheduler scheduler, CancellationToken ct) =>
        new(scheduler.SchedulerName, (await scheduler.GetStatus(ct).ConfigureAwait(false)).ToString());

}

internal sealed class SchedulerDashboardCommands(ISchedulerFactory schedulerFactory) : ISchedulerDashboardCommands
{
    public async Task StartSchedulerAsync(string schedulerName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false)).Start(ct).ConfigureAwait(false);

    public async Task StandbySchedulerAsync(string schedulerName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false)).Standby(ct).ConfigureAwait(false);

    public async Task TriggerJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .TriggerJob(new JobKey(jobName, jobGroup), cancellationToken: ct).ConfigureAwait(false);

    public async Task PauseJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .PauseJob(new JobKey(jobName, jobGroup), ct).ConfigureAwait(false);

    public async Task ResumeJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .ResumeJob(new JobKey(jobName, jobGroup), ct).ConfigureAwait(false);

    public async Task<bool> DeleteJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .DeleteJob(new JobKey(jobName, jobGroup), ct).ConfigureAwait(false);

    public async Task PauseTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .PauseTrigger(new TriggerKey(triggerName, triggerGroup), ct).ConfigureAwait(false);

    public async Task ResumeTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .ResumeTrigger(new TriggerKey(triggerName, triggerGroup), ct).ConfigureAwait(false);

    public async Task<bool> DeleteTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct) =>
        await (await GetSchedulerAsync(schedulerName, ct).ConfigureAwait(false))
            .UnscheduleJob(new TriggerKey(triggerName, triggerGroup), ct).ConfigureAwait(false);

    private async Task<IScheduler> GetSchedulerAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var schedulers = await schedulerFactory.GetAllSchedulers(ct).ConfigureAwait(false);
        return schedulers.FirstOrDefault(x => string.Equals(x.SchedulerName, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"No registered scheduler named '{name}'.");
    }
}
