using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl.Calendar;

namespace QuartzSupervisor.Integration;

internal sealed class DashboardBackend(ISchedulerFactory factory, ISchedulerDashboardAccess access, IServiceProvider services,
    SchedulerDashboardListenerRegistration listeners, IOptions<SchedulerDashboardOptions> options)
{
    internal SchedulerDashboardOptions Options => options.Value;
    internal async Task<IScheduler> Scheduler(string name, DashboardPermission permission, CancellationToken ct)
    {
        await access.AuthorizeAsync(name, permission, ct).ConfigureAwait(false);
        return await Resolve(name, ct).ConfigureAwait(false);
    }
    internal async Task<IScheduler> Resolve(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scheduler = (await factory.GetAllSchedulers(ct).ConfigureAwait(false)).FirstOrDefault(x => x.SchedulerName == name)
            ?? throw new KeyNotFoundException($"No registered scheduler named '{name}'.");
        listeners.EnsureAttached(scheduler);
        return scheduler;
    }
    internal IExecutionHistoryStore? History(string name) => services.GetKeyedService<IExecutionHistoryStore>(name)
        ?? (Options.RemoteClients.ContainsKey(name) ? null : services.GetService<IExecutionHistoryStore>());
    internal DashboardJobType? Catalog(IJobDetail job) => Options.JobTypes.FirstOrDefault(x =>
        string.Equals(TypeIdentity(x.TypeName), TypeIdentity(job.JobType.FullName), StringComparison.Ordinal));
    private static string TypeIdentity(string name) => string.Join(',', name.Split(',').Take(2).Select(part => part.Trim()));
    internal static string Text(string value, string label, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new ArgumentException($"{label} must be 1–{max} characters without control characters.");
        return value;
    }
    internal static void Description(string? value)
    {
        if (value?.Length > 2000) throw new ArgumentException("Description is limited to 2000 characters.");
    }
    internal static string Revision<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static void CheckRevision(string? supplied, string current)
    {
        if (string.IsNullOrEmpty(supplied) || !string.Equals(supplied, current, StringComparison.Ordinal))
            throw new InvalidOperationException("This configuration changed. Reload and review before saving.");
    }
    internal static JobDataMap Parameters(DashboardJobType? catalog, IReadOnlyDictionary<string, string> values, bool requireAll)
    {
        if (values.Count > 100 || (catalog is null && values.Count > 0)) throw new ArgumentException("Only approved parameters may be supplied.");
        var result = new JobDataMap();
        foreach (var (name, value) in values)
        {
            var parameter = catalog?.Parameters.SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException("Unknown parameter.");
            if (value.Length > 4096 || (parameter.Required && string.IsNullOrWhiteSpace(value))) throw new ArgumentException($"Invalid value for {parameter.Label}.");
            object parsed;
            try
            {
                parsed = parameter.Type switch
                {
                    "string" => value,
                    "int" => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "long" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "decimal" => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                    "bool" => bool.Parse(value),
                    _ => throw new ArgumentException("Unsupported parameter type.")
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException($"Invalid {parameter.Type} value for {parameter.Label}."); }
            result[name] = parsed;
        }
        if (requireAll && catalog is not null && catalog.Parameters.Any(p => p.Required && !values.ContainsKey(p.Name)))
            throw new ArgumentException("A required parameter is missing.");
        return result;
    }
    internal static TriggerDefinition Definition(ITrigger trigger)
    {
        var d = new TriggerDefinition
        {
            Group = trigger.Key.Group,
            Name = trigger.Key.Name,
            JobGroup = trigger.JobKey.Group,
            JobName = trigger.JobKey.Name,
            Description = trigger.Description,
            StartUtc = trigger.StartTimeUtc,
            EndUtc = trigger.EndTimeUtc,
            Priority = trigger.Priority,
            CalendarName = trigger.CalendarName,
            Kind = "Unsupported"
        };
        if (trigger is ICronTrigger cron)
        {
            d.Kind = "Cron"; d.CronExpression = cron.CronExpressionString ?? ""; d.TimeZoneId = cron.TimeZone.Id;
            d.Misfire = ((CronTriggerMisfireInstruction)trigger.MisfireInstructionCode).ToString();
            if (d.Misfire == "FireAndProceed") d.Misfire = "FireNow";
        }
        else if (trigger is ISimpleTrigger simple)
        {
            d.Kind = simple.RepeatCount == 0 ? "Once" : "Simple";
            d.IntervalSeconds = simple.RepeatInterval.TotalSeconds; d.RepeatCount = simple.RepeatCount;
            d.Misfire = ((SimpleTriggerMisfireInstruction)trigger.MisfireInstructionCode).ToString();
        }
        d.Revision = Revision(d);
        return d;
    }
    internal static bool Editable(ITrigger trigger) => trigger is ICronTrigger or ISimpleTrigger;
    internal static string Schedule(TriggerDefinition d) => d.Kind switch
    {
        "Cron" => $"{d.CronExpression} · {d.TimeZoneId}",
        "Once" => "Once",
        "Simple" => $"Every {d.IntervalSeconds.ToString(CultureInfo.InvariantCulture)} seconds · {(d.RepeatCount < 0 ? "repeat forever" : $"{d.RepeatCount} repeats")}",
        _ => "Unsupported schedule · read only"
    };
    internal static ITrigger Build(TriggerDefinition d, ITrigger? original = null)
    {
        Text(d.Group, "Trigger group"); Text(d.Name, "Trigger name"); Text(d.JobGroup, "Job group"); Text(d.JobName, "Job name"); Description(d.Description);
        if (d.Priority is < 1 or > 10000) throw new ArgumentException("Priority must be between 1 and 10000.");
        var start = d.StartUtc ?? DateTimeOffset.UtcNow;
        if (d.EndUtc <= start) throw new ArgumentException("End must be after start.");
        // Reuse the native builder to preserve retry, overlap, execution routing and trigger data.
        var builder = (original?.GetTriggerBuilder() ?? TriggerBuilder.Create()).WithIdentity(d.Name, d.Group)
            .ForJob(d.JobName, d.JobGroup).WithDescription(d.Description).WithPriority(d.Priority).StartAt(start).EndAt(d.EndUtc);
        builder.WithCalendarName(string.IsNullOrWhiteSpace(d.CalendarName) ? null : Text(d.CalendarName, "Calendar"));
        if (d.Kind == "Cron")
        {
            if (d.CronExpression.Length > 200 || !CronExpression.TryParse(d.CronExpression, out var expression)) throw new ArgumentException("Invalid Quartz cron expression.");
            var misfire = d.Misfire == "FireNow" ? "FireAndProceed" : d.Misfire;
            if (!Enum.TryParse<CronTriggerMisfireInstruction>(misfire, out var policy) || !Enum.IsDefined(policy)) throw new ArgumentException("Invalid cron misfire policy.");
            builder.WithSchedule(CronScheduleBuilder.Create(expression).InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(d.TimeZoneId)).WithMisfireInstruction(policy));
        }
        else if (d.Kind is "Simple" or "Once")
        {
            if (d.Kind == "Simple" && (!double.IsFinite(d.IntervalSeconds) || d.IntervalSeconds < 1 || d.IntervalSeconds > 31536000 || d.RepeatCount < -1)) throw new ArgumentException("Invalid simple repeat interval or count.");
            if (!Enum.TryParse<SimpleTriggerMisfireInstruction>(d.Misfire, out var policy) || !Enum.IsDefined(policy)) throw new ArgumentException("Invalid simple misfire policy.");
            builder.WithSchedule(SimpleScheduleBuilder.Create().WithInterval(TimeSpan.FromSeconds(d.Kind == "Once" ? 1 : d.IntervalSeconds))
                .WithRepeatCount(d.Kind == "Once" ? 0 : d.RepeatCount).WithMisfireInstruction(policy));
        }
        else throw new ArgumentException("This trigger family cannot be edited.");
        return builder.Build();
    }
    internal static IReadOnlyList<DateTimeOffset> Occurrences(ITrigger trigger, ICalendar? calendar, DateTimeOffset from, DateTimeOffset to, int max, CancellationToken ct)
    {
        var result = new List<DateTimeOffset>();
        var cursor = from.AddTicks(-1);
        // Bound work even for calendars excluding every high-frequency occurrence.
        for (var attempts = 0; attempts < 100000 && result.Count < max; attempts++)
        {
            ct.ThrowIfCancellationRequested();
            var next = trigger.GetFireTimeAfter(cursor);
            if (next is null || next > to || next <= cursor) break;
            if (calendar is null || calendar.IsTimeIncluded(next.Value)) result.Add(next.Value);
            else
            {
                var included = calendar.GetNextIncludedTimeUtc(next.Value);
                if (included > next) { cursor = included.AddTicks(-1); continue; }
            }
            cursor = next.Value;
        }
        return result;
    }
    internal static CalendarDefinition Definition(string name, ICalendar calendar)
    {
        var d = new CalendarDefinition { Name = name, Description = calendar.Description, Kind = "Unsupported" };
        if (calendar is BaseCalendar b) d.TimeZoneId = b.TimeZone.Id;
        if (calendar is HolidayCalendar holiday && holiday.CalendarBase is null) { d.Kind = "Holiday"; d.ExcludedDates = holiday.DaysExcluded.Order().ToList(); }
        if (calendar is WeeklyCalendar weekly && weekly.CalendarBase is null) { d.Kind = "Weekly"; d.ExcludedDays = Enum.GetValues<DayOfWeek>().Where(weekly.IsDayExcluded).ToList(); }
        d.Revision = Revision(d);
        return d;
    }
    internal static ICalendar Build(CalendarDefinition d)
    {
        Text(d.Name, "Calendar name"); Description(d.Description);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(d.TimeZoneId);
        if (d.Kind == "Holiday")
        {
            if (d.ExcludedDates.Count > 5000) throw new ArgumentException("At most 5000 excluded dates are allowed.");
            var calendar = new HolidayCalendar { TimeZone = zone, Description = d.Description };
            foreach (var date in d.ExcludedDates) calendar.AddExcludedDay(date);
            return calendar;
        }
        if (d.Kind == "Weekly")
        {
            if (d.ExcludedDays.Any(x => !Enum.IsDefined(x)) || d.ExcludedDays.Distinct().Count() == 7) throw new ArgumentException("At least one valid weekday must remain included.");
            var calendar = new WeeklyCalendar { TimeZone = zone, Description = d.Description };
            foreach (var day in Enum.GetValues<DayOfWeek>()) calendar.RemoveExcludedDay(day);
            foreach (var day in d.ExcludedDays) calendar.AddExcludedDay(day);
            return calendar;
        }
        throw new ArgumentException("This calendar family cannot be edited.");
    }
    internal static string Identity(params string[] parts) => string.Join('.', parts.Select(p => Convert.ToBase64String(Encoding.UTF8.GetBytes(p)).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    internal DashboardExecution Execution(ExecutionHistoryEntry entry) => new(
        Identity(entry.SchedulerName, entry.SchedulerInstanceId, entry.EntryId ?? $"{entry.JobGroup}/{entry.JobName}/{entry.TriggerGroup}/{entry.TriggerName}/{entry.FiredAtUtc:O}", entry.RetryAttempt.ToString(CultureInfo.InvariantCulture)),
        entry.SchedulerName, entry.JobName, entry.JobGroup, entry.FiredAtUtc, entry.FiredAtUtc + entry.Duration, !entry.Succeeded)
    {
        TriggerGroup = entry.TriggerGroup,
        TriggerName = entry.TriggerName,
        InstanceId = entry.SchedulerInstanceId,
        EntryId = entry.EntryId,
        IsManualRun = entry.TriggerGroup == SchedulerDashboardCommands.ManualRunTriggerGroup,
        ErrorMessage = Diagnostic(entry.ExceptionMessage, 4096),
        Log = Diagnostic(entry.Log, 32768)
    };
    internal DashboardExecution Execution(string schedulerName, FireInstance entry) => new(
        Identity(schedulerName, entry.SchedulerInstanceId, entry.JobKey?.Group ?? "", entry.JobKey?.Name ?? "", entry.TriggerKey.Group, entry.TriggerKey.Name, entry.FireTimeUtc.ToString("O"), entry.FireInstanceId),
        schedulerName, entry.JobKey?.Name ?? "", entry.JobKey?.Group ?? "", entry.FireTimeUtc, null, false)
    {
        TriggerGroup = entry.TriggerKey.Group,
        TriggerName = entry.TriggerKey.Name,
        InstanceId = entry.SchedulerInstanceId,
        FireInstanceId = entry.FireInstanceId,
        ScheduledUtc = entry.ScheduledFireTimeUtc,
        Progress = entry.Progress,
        ProgressMessage = Diagnostic(entry.ProgressMessage, 4096),
        IsManualRun = entry.TriggerKey.Group == SchedulerDashboardCommands.ManualRunTriggerGroup
    };
    private string? Diagnostic(string? value, int max)
    {
        if (value is null || Options.RedactDiagnostic is not { } redact) return null;
        var redacted = redact(value);
        return redacted is null ? null : redacted[..Math.Min(max, redacted.Length)];
    }
    internal static TriggerSummary Summary(TriggerHeader t) => new(t.Key.Group, t.Key.Name, t.JobKey.Group, t.JobKey.Name, t.State.ToString(), t.NextFireTimeUtc, t.PreviousFireTimeUtc)
    { IsManualRun = t.Key.Group == SchedulerDashboardCommands.ManualRunTriggerGroup };
    internal static bool Matches(string? filter, params string[] parts) => string.IsNullOrWhiteSpace(filter) || parts.Any(x => x.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
}

internal sealed class DashboardAudit(ILogger<DashboardAudit> logger, IOptions<SchedulerDashboardOptions> options)
{
    private readonly Queue<DashboardAuditEntry> _entries = new();
    // Serializes revision checks and writes made through this dashboard process; external Quartz writers are not transactional participants.
    internal SemaphoreSlim MutationGate { get; } = new(1, 1);
    internal IReadOnlyList<DashboardAuditEntry> Read(string scheduler) { lock (_entries) return _entries.Where(e => e.SchedulerName == scheduler).Reverse().ToArray(); }
    internal async Task Record(string actor, string scheduler, string action, string target, bool succeeded, Exception? error)
    {
        static string Bounded(string text) => new(text.Take(512).Where(c => !char.IsControl(c)).ToArray());
        var entry = new DashboardAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, Bounded(actor), Bounded(scheduler), action, Bounded(target), succeeded, error?.GetType().Name);
        lock (_entries) { _entries.Enqueue(entry); while (_entries.Count > 500) _entries.Dequeue(); }
        logger.LogInformation("Dashboard audit {Actor} {Scheduler} {Action} {Target} {Succeeded} {ErrorType}", entry.Actor, entry.SchedulerName, action, entry.Target, succeeded, entry.Error);
        if (options.Value.AuditSink is { } sink)
        {
            try { await sink(entry, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogError("Dashboard audit sink failed ({ErrorType}); entry {EntryId} remains in bounded memory", ex.GetType().Name, entry.Id); }
        }
    }
}
