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

public static class SchedulerDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddQuartzSupervisorDashboard(this IServiceCollection services)
    {
        services.TryAddSingleton<ISchedulerDashboardQueries, SchedulerDashboardQueries>();
        return services;
    }
}

internal sealed class SchedulerDashboardQueries(ISchedulerFactory schedulerFactory) : ISchedulerDashboardQueries
{
    public async Task<IReadOnlyList<SchedulerSummary>> GetSchedulersAsync(CancellationToken ct)
    {
        var schedulers = await schedulerFactory.GetAllSchedulers(ct).ConfigureAwait(false);
        var result = new List<SchedulerSummary>(schedulers.Count);
        foreach (var scheduler in schedulers.OrderBy(x => x.SchedulerName, StringComparer.OrdinalIgnoreCase))
            result.Add(await SummarizeAsync(scheduler, ct).ConfigureAwait(false));
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
        var found = schedulers.FirstOrDefault(x => string.Equals(x.SchedulerName, name, StringComparison.Ordinal));
        return found ?? throw new KeyNotFoundException($"No registered scheduler named '{name}'.");
    }

    private static async Task<SchedulerSummary> SummarizeAsync(IScheduler scheduler, CancellationToken ct) =>
        new(scheduler.SchedulerName, (await scheduler.GetStatus(ct).ConfigureAwait(false)).ToString());

}
