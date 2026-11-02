using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Quartz;

namespace QuartzSupervisor.Integration;

public sealed record SchedulerSummary(string Name, string Status);
public sealed record JobSummary(string Group, string Name, string? Description, bool Durable, bool RequestsRecovery)
{
    public string State { get; init; } = "Unscheduled";
    public int RunningCount { get; init; }
    public DateTimeOffset? NextFireTimeUtc { get; init; }
    public DateTimeOffset? LastCompletedUtc { get; init; }
    public bool? LastRunFailed { get; init; }
    public int TriggerCount { get; init; }
}
public sealed record TriggerSummary(string Group, string Name, string JobGroup, string JobName, string State, DateTimeOffset? NextFireTimeUtc, DateTimeOffset? PreviousFireTimeUtc)
{
    public bool IsManualRun { get; init; }
    public string? ScheduleDescription { get; init; }
}
public sealed record DashboardOverview(SchedulerSummary Scheduler, int JobCount, int TriggerCount, IReadOnlyList<TriggerSummary> UpcomingTriggers);
public sealed record DashboardPage<T>(IReadOnlyList<T> Items, bool HasMore, int Skip, int PageSize);

public interface ISchedulerDashboardQueries
{
    Task<DashboardOverview> GetOverviewAsync(string schedulerName, CancellationToken ct);
    Task<IReadOnlyList<SchedulerSummary>> GetSchedulersAsync(CancellationToken ct);
    Task<DashboardPage<JobSummary>> GetJobsAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct, string? groupFilter = null, string? stateFilter = null);
    Task<DashboardPage<TriggerSummary>> GetTriggersAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct, string? groupFilter = null, string? stateFilter = null);
    Task<JobDetails?> GetJobAsync(string schedulerName, string group, string name, CancellationToken ct);
    Task<TriggerDetails?> GetTriggerAsync(string schedulerName, string group, string name, CancellationToken ct);
    Task<SchedulerDetails> GetSchedulerDetailsAsync(string schedulerName, CancellationToken ct);
    Task<IReadOnlyList<DashboardExecution>> GetRunningAsync(string schedulerName, CancellationToken ct);
    Task<DashboardHistory> GetHistoryAsync(string schedulerName, ExecutionFilter filter, CancellationToken ct);
    Task<DashboardExecution?> GetExecutionAsync(string schedulerName, string id, CancellationToken ct);
    Task<DashboardTimeline> GetTimelineAsync(string schedulerName, DateTimeOffset fromUtc, DateTimeOffset toUtc, string? filter, CancellationToken ct);
    Task<IReadOnlyList<DateTimeOffset>> PreviewTriggerAsync(string schedulerName, TriggerDefinition definition, CancellationToken ct);
    Task<IReadOnlyList<CalendarDetails>> GetCalendarsAsync(string schedulerName, CancellationToken ct);
    Task<IReadOnlyList<TriggerSummary>> PreviewCalendarAsync(string schedulerName, CalendarDefinition definition, CancellationToken ct);
    Task<IReadOnlyList<DashboardAuditEntry>> GetAuditAsync(string schedulerName, CancellationToken ct);
    Task<IReadOnlyList<DashboardJobType>> GetJobTypesAsync(string schedulerName, CancellationToken ct);
}
public interface ISchedulerDashboardCommands
{
    Task StartSchedulerAsync(string schedulerName, CancellationToken ct);
    Task StandbySchedulerAsync(string schedulerName, CancellationToken ct);
    Task TriggerJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct, IReadOnlyDictionary<string, string>? parameters = null);
    Task PauseJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task ResumeJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task<bool> DeleteJobAsync(string schedulerName, string jobGroup, string jobName, CancellationToken ct);
    Task PauseTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
    Task ResumeTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
    Task<bool> DeleteTriggerAsync(string schedulerName, string triggerGroup, string triggerName, CancellationToken ct);
    Task CreateJobAsync(string schedulerName, JobDefinition definition, CancellationToken ct);
    Task SaveTriggerAsync(string schedulerName, TriggerDefinition definition, bool replace, CancellationToken ct);
    Task ResetTriggerAsync(string schedulerName, string group, string name, CancellationToken ct);
    Task<bool> InterruptExecutionAsync(string schedulerName, string fireInstanceId, CancellationToken ct);
    Task SaveCalendarAsync(string schedulerName, CalendarDefinition definition, bool replace, CancellationToken ct);
    Task<bool> DeleteCalendarAsync(string schedulerName, string name, CancellationToken ct);
    Task<IReadOnlyList<BulkResult>> BulkJobsAsync(string schedulerName, IReadOnlyList<DashboardKey> keys, string action, CancellationToken ct);
    Task<IReadOnlyList<BulkResult>> BulkTriggersAsync(string schedulerName, IReadOnlyList<DashboardKey> keys, string action, CancellationToken ct);
}
public static class SchedulerDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddQuartzSupervisorIntegration(this IServiceCollection services, Action<SchedulerDashboardOptions>? configure = null)
    {
        services.AddOptions<SchedulerDashboardOptions>().Validate(o => o.PageSize is > 0 and <= 200 && o.MaxTimelineItems is > 0 and <= 10000, "Invalid dashboard bounds.");
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<SchedulerDashboardOptions>>().Value);
        services.AddLogging();
        services.AddHttpClient();
        services.AddQuartzExecutionHistory();
        services.TryAddSingleton<SchedulerDashboardEvents>();
        services.TryAddSingleton<ISchedulerDashboardUpdates>(sp => sp.GetRequiredService<SchedulerDashboardEvents>());
        services.TryAddSingleton<SchedulerDashboardListenerRegistration>();
        services.AddHostedService<SchedulerDashboardObserver>();
        services.TryAddSingleton<DashboardAudit>();
        services.TryAddScoped<ISchedulerDashboardAccess, TrustedDashboardAccess>();
        services.TryAddScoped<DashboardBackend>();
        services.TryAddScoped<ISchedulerDashboardQueries, SchedulerDashboardQueries>();
        services.TryAddScoped<ISchedulerDashboardCommands, SchedulerDashboardCommands>();
        return services;
    }
}
internal sealed class TrustedDashboardAccess : ISchedulerDashboardAccess
{
    public ValueTask<string> AuthorizeAsync(string schedulerName, DashboardPermission permission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("trusted-host");
    }
}
