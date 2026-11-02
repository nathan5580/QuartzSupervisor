namespace QuartzSupervisor.Integration;

public enum DashboardPermission { View, Operate, Administer }

public interface ISchedulerDashboardAccess
{
    ValueTask<string> AuthorizeAsync(string schedulerName, DashboardPermission permission, CancellationToken ct);
}

public sealed class SchedulerDashboardOptions
{
    public int PageSize { get; set; } = 50;
    public int MaxTimelineItems { get; set; } = 1000;
    public List<DashboardJobType> JobTypes { get; set; } = [];
    public Func<string, string?>? RedactDiagnostic { get; set; }
    public Dictionary<string, string> RemoteClients { get; set; } = new(StringComparer.Ordinal);
    public Func<DashboardAuditEntry, CancellationToken, ValueTask>? AuditSink { get; set; }
}

public sealed record DashboardParameter(string Name, string Label, string Type = "string", bool Required = false, bool Sensitive = false);
public sealed record DashboardJobType(string Key, string Label, string TypeName, IReadOnlyList<DashboardParameter> Parameters);
public sealed class JobDefinition
{
    public string Group { get; set; } = "DEFAULT";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string JobTypeKey { get; set; } = "";
    public bool Durable { get; set; } = true;
    public bool RequestsRecovery { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = [];
}
public sealed record JobDetails(JobSummary Summary, string JobType, bool ConcurrentExecutionDisallowed, bool PersistJobDataAfterExecution,
    IReadOnlyList<TriggerSummary> Triggers, IReadOnlyDictionary<string, string> Parameters, IReadOnlyList<DashboardParameter> ParameterSchema, string Revision);
public sealed class TriggerDefinition
{
    public string Group { get; set; } = "DEFAULT";
    public string Name { get; set; } = "";
    public string JobGroup { get; set; } = "DEFAULT";
    public string JobName { get; set; } = "";
    public string Kind { get; set; } = "Cron";
    public string? Description { get; set; }
    public string CronExpression { get; set; } = "0 0 * * * ?";
    public double IntervalSeconds { get; set; } = 60;
    public int RepeatCount { get; set; } = -1;
    public string TimeZoneId { get; set; } = "UTC";
    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public int Priority { get; set; } = 5;
    public string? CalendarName { get; set; }
    public string Misfire { get; set; } = "SmartPolicy";
    public string? Revision { get; set; }
}
public sealed record TriggerDetails(TriggerSummary Summary, TriggerDefinition Definition, IReadOnlyList<DateTimeOffset> NextFireTimes, bool Editable, string ScheduleDescription);
public sealed record SchedulerDetails(SchedulerSummary Summary, string InstanceId, string Version, string StoreType, bool Persistent, bool Clustered,
    int MaxConcurrency, IReadOnlyList<DashboardNode> Nodes, DashboardCapabilities Capabilities);
public sealed record DashboardNode(string InstanceId, string State, DateTimeOffset? LastCheckInUtc, bool IsCurrentNode);
public sealed record DashboardCapabilities(bool CanOperate, bool CanAdminister, bool Remote, bool HistoryAvailable, string HistoryDescription, string LiveMode);
public sealed class ExecutionFilter
{
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public string? JobName { get; set; }
    public string? JobGroup { get; set; }
    public string? Search { get; set; }
    public string? InstanceId { get; set; }
    public bool FailedOnly { get; set; }
    public int Skip { get; set; }
    public string? TriggerName { get; set; }
    public string? TriggerGroup { get; set; }
}
public sealed record DashboardHistory(DashboardPage<DashboardExecution> Page, bool Available, string Description, string? Message);
public sealed record ScheduledOccurrence(string Id, string SchedulerName, string JobGroup, string JobName, string TriggerGroup, string TriggerName, DateTimeOffset FireUtc, string State);
public sealed record DashboardTimeline(IReadOnlyList<DashboardExecution> Executions, IReadOnlyList<ScheduledOccurrence> Scheduled, DateTimeOffset CapturedUtc, bool Truncated, string Coverage)
{
    public IReadOnlyList<JobSummary> Jobs { get; init; } = [];
    public IReadOnlyList<TriggerSummary> Triggers { get; init; } = [];
    public bool InventoryTruncated { get; init; }
}
public sealed class CalendarDefinition
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Holiday";
    public string? Description { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public List<DateOnly> ExcludedDates { get; set; } = [];
    public List<DayOfWeek> ExcludedDays { get; set; } = [];
    public string? Revision { get; set; }
}
public sealed record CalendarDetails(CalendarDefinition Definition, IReadOnlyList<TriggerSummary> AffectedTriggers, bool Editable);
public sealed record DashboardAuditEntry(string Id, DateTimeOffset TimestampUtc, string Actor, string SchedulerName, string Action, string Target, bool Succeeded, string? Error);
public sealed record DashboardKey(string Group, string Name);
public sealed record BulkResult(string Group, string Name, bool Succeeded, string? Error);
public sealed record DashboardFreshness(string Mode, DateTimeOffset? LastUpdatedUtc, string? Message);
public sealed record DashboardExecution(string Id, string SchedulerName, string JobName, string JobGroup, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, bool IsError)
{
    public bool IsManualRun { get; init; }
    public string TriggerName { get; init; } = "";
    public string TriggerGroup { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string? FireInstanceId { get; init; }
    public DateTimeOffset? ScheduledUtc { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Log { get; init; }
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public string? EntryId { get; init; }
}
