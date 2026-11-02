using Microsoft.Extensions.Options;
using Quartz;

namespace QuartzSupervisor.Integration;

internal sealed class SchedulerDashboardQueries(DashboardBackend backend, ISchedulerFactory factory, ISchedulerDashboardAccess access,
    DashboardAudit audit, ISchedulerDashboardUpdates updates, IOptions<ExecutionHistoryOptions> historyOptions) : ISchedulerDashboardQueries
{
    public async Task<IReadOnlyList<SchedulerSummary>> GetSchedulersAsync(CancellationToken ct)
    {
        var result = new List<SchedulerSummary>();
        foreach (var scheduler in await factory.GetAllSchedulers(ct).ConfigureAwait(false))
        {
            try
            {
                await access.AuthorizeAsync(scheduler.SchedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
                await backend.Resolve(scheduler.SchedulerName, ct).ConfigureAwait(false);
                result.Add(new(scheduler.SchedulerName, (await scheduler.GetStatus(ct).ConfigureAwait(false)).ToString()));
            }
            catch (UnauthorizedAccessException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SchedulerException or HttpRequestException or OperationCanceledException) { result.Add(new(scheduler.SchedulerName, "Unavailable")); }
        }
        return result.OrderBy(x => x.Status == "Unavailable").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<DashboardOverview> GetOverviewAsync(string schedulerName, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var jobs = await scheduler.QueryJobs(new JobQuery { Take = 0, IncludeTotalCount = true }, ct).ConfigureAwait(false);
        var triggers = await scheduler.QueryTriggers(new TriggerQuery { Take = PagedQuery.All }, ct).ConfigureAwait(false);
        return new(new(schedulerName, (await scheduler.GetStatus(ct).ConfigureAwait(false)).ToString()), jobs.TotalCount ?? 0, triggers.Items.Count,
            triggers.Items.Where(t => t.NextFireTimeUtc is not null && t.State is TriggerState.Normal or TriggerState.Executing or TriggerState.Blocked)
                .OrderBy(t => t.NextFireTimeUtc).ThenBy(t => t.Key.Group, StringComparer.Ordinal).ThenBy(t => t.Key.Name, StringComparer.Ordinal).Take(5).Select(DashboardBackend.Summary).ToArray());
    }
    public async Task<DashboardPage<JobSummary>> GetJobsAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct, string? groupFilter = null, string? stateFilter = null)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        var result = new List<JobSummary>();
        var offset = string.IsNullOrWhiteSpace(stateFilter) ? skip : 0;
        var matched = 0;
        bool more;
        do
        {
            var page = await scheduler.QueryJobs(new JobQuery
            {
                Name = string.IsNullOrWhiteSpace(nameFilter) ? null : NameMatcher<JobKey>.NameContains(nameFilter.Trim()),
                Group = string.IsNullOrWhiteSpace(groupFilter) ? null : GroupMatcher<JobKey>.GroupEquals(groupFilter),
                Skip = offset,
                Take = backend.Options.PageSize
            }, ct).ConfigureAwait(false);
            foreach (var job in page.Items)
            {
                var summary = await JobSummary(scheduler, job.Key, job.Description, job.Durable, job.RequestsRecovery, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stateFilter) && !string.Equals(stateFilter, summary.State, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(stateFilter) && matched++ < skip) continue;
                result.Add(summary);
                if (result.Count > backend.Options.PageSize) break;
            }
            offset += page.Items.Count;
            more = page.HasMore;
            if (string.IsNullOrWhiteSpace(stateFilter)) return new(result, more, skip, backend.Options.PageSize);
        } while (more && result.Count <= backend.Options.PageSize);
        return new(result.Take(backend.Options.PageSize).ToArray(), result.Count > backend.Options.PageSize, skip, backend.Options.PageSize);
    }
    private async Task<JobSummary> JobSummary(IScheduler scheduler, JobKey key, string? description, bool durable, bool recovery, CancellationToken ct)
    {
        var triggers = await scheduler.QueryTriggers(new TriggerQuery { Job = key, Take = PagedQuery.All }, ct).ConfigureAwait(false);
        var running = await scheduler.QueryFireInstances(new FireInstanceQuery { Job = key, Take = 0, IncludeTotalCount = true }, ct).ConfigureAwait(false);
        var history = await History(scheduler.SchedulerName, new ExecutionFilter { JobGroup = key.Group, JobName = key.Name }, 1, ct).ConfigureAwait(false);
        var last = history.Page.Items.FirstOrDefault();
        var count = running.TotalCount ?? 0;
        return new(key.Group, key.Name, description, durable, recovery)
        {
            RunningCount = count,
            TriggerCount = triggers.Items.Count,
            State = count > 0 ? "Running" : triggers.Items.Count == 0 ? "Unscheduled" : triggers.Items.Any(t => t.State == TriggerState.Error) ? "Error" : triggers.Items.All(t => t.State == TriggerState.Paused) ? "Paused" : triggers.Items.All(t => t.State == TriggerState.Complete) ? "Complete" : triggers.Items.Any(t => t.State == TriggerState.Blocked) ? "Blocked" : "Normal",
            NextFireTimeUtc = triggers.Items.Where(t => t.State is TriggerState.Normal or TriggerState.Executing or TriggerState.Blocked).Select(t => t.NextFireTimeUtc).Min(),
            LastCompletedUtc = last?.CompletedUtc,
            LastRunFailed = last?.IsError
        };
    }
    public async Task<DashboardPage<TriggerSummary>> GetTriggersAsync(string schedulerName, string? nameFilter, int skip, CancellationToken ct, string? groupFilter = null, string? stateFilter = null)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        TriggerState? state = null;
        if (!string.IsNullOrWhiteSpace(stateFilter)) state = Enum.TryParse<TriggerState>(stateFilter, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("Invalid trigger state.");
        var page = await scheduler.QueryTriggers(new TriggerQuery
        {
            Name = string.IsNullOrWhiteSpace(nameFilter) ? null : NameMatcher<TriggerKey>.NameContains(nameFilter.Trim()),
            Group = string.IsNullOrWhiteSpace(groupFilter) ? null : GroupMatcher<TriggerKey>.GroupEquals(groupFilter),
            State = state,
            Skip = skip,
            Take = backend.Options.PageSize
        }, ct).ConfigureAwait(false);
        return new(page.Items.Select(DashboardBackend.Summary).ToArray(), page.HasMore, skip, backend.Options.PageSize);
    }
    public async Task<JobDetails?> GetJobAsync(string schedulerName, string group, string name, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var job = await scheduler.GetJobDetail(new JobKey(name, group), ct).ConfigureAwait(false);
        if (job is null) return null;
        var catalog = backend.Catalog(job);
        var values = new Dictionary<string, string>();
        foreach (var p in catalog?.Parameters ?? [])
            if (!p.Sensitive && job.JobDataMap.TryGetValue(p.Name, out var value)) values[p.Name] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var triggers = await scheduler.QueryTriggers(new TriggerQuery { Job = job.Key, Take = PagedQuery.All }, ct).ConfigureAwait(false);
        var summary = await JobSummary(scheduler, job.Key, job.Description, job.Durable, job.RequestsRecovery, ct).ConfigureAwait(false);
        return new(summary, job.JobType.FullName, job.ConcurrentExecutionDisallowed, job.PersistJobDataAfterExecution, triggers.Items.Select(DashboardBackend.Summary).ToArray(), values,
            catalog?.Parameters ?? [], DashboardBackend.Revision(new { job.Description, job.Durable, job.RequestsRecovery, Type = job.JobType.FullName, Parameters = values }));
    }
    public async Task<TriggerDetails?> GetTriggerAsync(string schedulerName, string group, string name, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var trigger = await scheduler.GetTrigger(new TriggerKey(name, group), ct).ConfigureAwait(false);
        if (trigger is null) return null;
        var definition = DashboardBackend.Definition(trigger);
        var summary = new TriggerSummary(group, name, trigger.JobKey.Group, trigger.JobKey.Name, (await scheduler.GetTriggerState(trigger.Key, ct).ConfigureAwait(false)).ToString(), trigger.NextFireTimeUtc, trigger.PreviousFireTimeUtc)
        { IsManualRun = group == SchedulerDashboardCommands.ManualRunTriggerGroup, ScheduleDescription = DashboardBackend.Schedule(definition) };
        var calendar = trigger.CalendarName is null ? null : await scheduler.GetCalendar(trigger.CalendarName, ct).ConfigureAwait(false);
        return new(summary, definition, DashboardBackend.Occurrences(trigger, calendar, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5), 10, ct), DashboardBackend.Editable(trigger), DashboardBackend.Schedule(definition));
    }
    public async Task<SchedulerDetails> GetSchedulerDetailsAsync(string schedulerName, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var metadata = await scheduler.GetMetadata(ct).ConfigureAwait(false);
        var nodes = await scheduler.QueryClusterNodes(ct).ConfigureAwait(false);
        var history = await History(schedulerName, new(), 1, ct).ConfigureAwait(false);
        async Task<bool> Can(DashboardPermission p) { try { await access.AuthorizeAsync(schedulerName, p, ct); return true; } catch (UnauthorizedAccessException) { return false; } }
        return new(new(schedulerName, metadata.Status.ToString()), metadata.SchedulerInstanceId, metadata.Version, metadata.JobStoreTypeName, metadata.JobStorePersistent, metadata.JobStoreClustered,
            metadata.ThreadPoolSize, nodes.Select(n => new DashboardNode(n.InstanceId, n.State.ToString(), n.LastCheckInUtc, n.IsCurrentNode)).ToArray(),
            new(await Can(DashboardPermission.Operate), await Can(DashboardPermission.Administer), metadata.IsProxy, history.Available, history.Description, updates.GetFreshness(schedulerName).Mode));
    }
    public async Task<IReadOnlyList<DashboardExecution>> GetRunningAsync(string schedulerName, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var page = await scheduler.QueryFireInstances(new FireInstanceQuery { Take = PagedQuery.All }, ct).ConfigureAwait(false);
        return page.Items.Select(e => backend.Execution(schedulerName, e)).ToArray();
    }
    public async Task<DashboardHistory> GetHistoryAsync(string schedulerName, ExecutionFilter filter, CancellationToken ct)
    {
        await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        return await History(schedulerName, filter, backend.Options.PageSize, ct).ConfigureAwait(false);
    }
    private async Task<DashboardHistory> History(string schedulerName, ExecutionFilter filter, int take, CancellationToken ct, bool overlapping = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(filter.Skip);
        if (filter.FromUtc > filter.ToUtc) throw new ArgumentException("History range is reversed.");
        var store = backend.History(schedulerName);
        var remote = backend.Options.RemoteClients.ContainsKey(schedulerName);
        var description = remote ? "Native remote history; retention is controlled by the remote host." : $"Native Quartz history; configured retention {historyOptions.Value.Retention}, up to {historyOptions.Value.MaxEntriesPerScheduler} entries per scheduler. In-memory stores clear on restart; durable stores retain history.";
        if (store is null || (!remote && historyOptions.Value.MaxEntriesPerScheduler == 0)) return new(new([], false, filter.Skip, take), false, description, "Execution history is not enabled for this scheduler.");
        try
        {
            var result = new List<DashboardExecution>();
            int offset = 0, matched = 0;
            bool more;
            do
            {
                var page = await store.QueryExecutions(new ExecutionHistoryQuery
                {
                    SchedulerName = schedulerName,
                    SchedulerInstanceId = filter.InstanceId,
                    JobContains = filter.JobName,
                    TriggerContains = filter.TriggerName,
                    Skip = offset,
                    Take = 200
                }, ct).ConfigureAwait(false);
                foreach (var e in page.Items)
                {
                    if (filter.FromUtc is { } from && (overlapping ? e.FiredAtUtc + e.Duration : e.FiredAtUtc) < from || filter.ToUtc is { } to && e.FiredAtUtc > to || filter.FailedOnly && e.Succeeded ||
                        filter.JobName is { } job && e.JobName != job || filter.JobGroup is { } group && e.JobGroup != group ||
                        filter.TriggerName is { } trigger && e.TriggerName != trigger || filter.TriggerGroup is { } triggerGroup && e.TriggerGroup != triggerGroup ||
                        !DashboardBackend.Matches(filter.Search, e.JobName, e.JobGroup, e.TriggerName, e.TriggerGroup)) continue;
                    if (matched++ < filter.Skip) continue;
                    result.Add(backend.Execution(e));
                    if (result.Count > take) break;
                }
                offset += page.Items.Count;
                more = page.HasMore && page.Items.Count > 0;
            } while (more && result.Count <= take);
            return new(new(result.Take(take).ToArray(), result.Count > take, filter.Skip, take), true, description, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or HttpRequestException or SchedulerException or OperationCanceledException)
        { return new(new([], false, filter.Skip, take), false, description, ex is NotSupportedException ? "This target does not expose execution history." : "Execution history is temporarily unavailable; retry after connectivity recovers."); }
    }
    public async Task<DashboardExecution?> GetExecutionAsync(string schedulerName, string id, CancellationToken ct)
    {
        await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var running = (await GetRunningAsync(schedulerName, ct).ConfigureAwait(false)).FirstOrDefault(e => e.Id == id);
        if (running is not null) return running;
        var store = backend.History(schedulerName);
        if (store is null) return null;
        if (id.Length > 8192) return null;
        var parts = id.Split('.');
        if (parts.Length is not (4 or 8)) return null;
        static string Decode(string part)
        {
            var encoded = part.Replace('-', '+').Replace('_', '/');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        }
        try { parts = parts.Select(Decode).ToArray(); }
        catch (FormatException) { return null; }
        if (parts[0] != schedulerName) return null;
        if (parts.Length == 4)
        {
            var entry = await store.GetExecution(schedulerName, parts[2], ct).ConfigureAwait(false);
            var execution = entry is null ? null : backend.Execution(entry);
            return execution?.Id == id ? execution : null;
        }
        // The native history schema has no fire-instance ID. Resolve a completed live link only
        // by the exact node/key/time tuple, and refuse ambiguous retries rather than guessing.
        if (!DateTimeOffset.TryParseExact(parts[6], "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fired)) return null;
        ExecutionHistoryEntry? match = null;
        var offset = 0;
        while (true)
        {
            var page = await store.QueryExecutions(new ExecutionHistoryQuery
            {
                SchedulerName = schedulerName,
                SchedulerInstanceId = parts[1],
                JobContains = parts[3],
                TriggerContains = parts[5],
                Skip = offset,
                Take = 200
            }, ct).ConfigureAwait(false);
            foreach (var candidate in page.Items)
            {
                if (candidate.SchedulerInstanceId != parts[1] || candidate.JobGroup != parts[2] || candidate.JobName != parts[3] ||
                    candidate.TriggerGroup != parts[4] || candidate.TriggerName != parts[5] || candidate.FiredAtUtc != fired) continue;
                if (match is not null) return null;
                match = candidate;
            }
            if (!page.HasMore || page.Items.Count == 0) break;
            offset += page.Items.Count;
        }
        if (match?.EntryId is { } entryId) match = await store.GetExecution(schedulerName, entryId, ct).ConfigureAwait(false);
        return match is null ? null : backend.Execution(match);
    }
    public async Task<DashboardTimeline> GetTimelineAsync(string schedulerName, DateTimeOffset fromUtc, DateTimeOffset toUtc, string? filter, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(366)) throw new ArgumentException("Timeline range must be positive and at most one year.");
        var limit = backend.Options.MaxTimelineItems;
        var history = await History(schedulerName, new() { FromUtc = fromUtc, ToUtc = toUtc, Search = filter }, limit, ct, overlapping: true).ConfigureAwait(false);
        var running = (await GetRunningAsync(schedulerName, ct).ConfigureAwait(false)).Where(e => e.StartedUtc <= toUtc && DashboardBackend.Matches(filter, e.JobName, e.JobGroup, e.TriggerName, e.TriggerGroup));
        var combined = running.Concat(history.Page.Items).OrderByDescending(e => e.StartedUtc).Take(limit + 1).ToArray();
        var executions = combined.Take(limit).ToArray();
        var scheduled = new List<ScheduledOccurrence>();
        var truncated = history.Page.HasMore || combined.Length > limit;
        var headers = await scheduler.QueryTriggers(new TriggerQuery { Take = limit + 1 }, ct).ConfigureAwait(false);
        var jobs = await scheduler.QueryJobs(new JobQuery { Take = limit + 1 }, ct).ConfigureAwait(false);
        truncated |= headers.HasMore || headers.Items.Count > limit;
        var projectionFrom = fromUtc > DateTimeOffset.UtcNow ? fromUtc : DateTimeOffset.UtcNow;
        foreach (var h in headers.Items.Take(limit))
        {
            if (h.State is not (TriggerState.Normal or TriggerState.Executing or TriggerState.Blocked) || !DashboardBackend.Matches(filter, h.JobKey.Name, h.JobKey.Group, h.Key.Name, h.Key.Group)) continue;
            var trigger = await scheduler.GetTrigger(h.Key, ct).ConfigureAwait(false);
            if (trigger is null || trigger.NextFireTimeUtc is null) continue;
            var calendar = trigger.CalendarName is null ? null : await scheduler.GetCalendar(trigger.CalendarName, ct).ConfigureAwait(false);
            var start = trigger.NextFireTimeUtc > projectionFrom ? trigger.NextFireTimeUtc.Value : projectionFrom;
            var times = DashboardBackend.Occurrences(trigger, calendar, start, toUtc, limit - executions.Length - scheduled.Count + 1, ct);
            foreach (var time in times)
            {
                if (executions.Length + scheduled.Count >= limit) { truncated = true; break; }
                scheduled.Add(new(DashboardBackend.Identity(schedulerName, h.Key.Group, h.Key.Name, time.ToString("O")), schedulerName, h.JobKey.Group, h.JobKey.Name, h.Key.Group, h.Key.Name, time, h.State.ToString()));
            }
            if (truncated && executions.Length + scheduled.Count >= limit) break;
        }
        return new(executions, scheduled.OrderBy(x => x.FireUtc).ToArray(), DateTimeOffset.UtcNow, truncated,
            $"{history.Description} Future markers are bounded schedule projections, not guaranteed runs; paused triggers are excluded. Past schedule gaps are not reconstructed. {history.Message}")
        {
            Jobs = jobs.Items.Take(limit).Select(j => new JobSummary(j.Key.Group, j.Key.Name, j.Description, j.Durable, j.RequestsRecovery)).ToArray(),
            Triggers = headers.Items.Take(limit).Select(DashboardBackend.Summary).ToArray(),
            InventoryTruncated = jobs.HasMore || jobs.Items.Count > limit || headers.HasMore || headers.Items.Count > limit
        };
    }
    public async Task<IReadOnlyList<DateTimeOffset>> PreviewTriggerAsync(string schedulerName, TriggerDefinition definition, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var trigger = DashboardBackend.Build(definition);
        var calendar = definition.CalendarName is null ? null : await scheduler.GetCalendar(definition.CalendarName, ct).ConfigureAwait(false) ?? throw new ArgumentException("Calendar does not exist.");
        return DashboardBackend.Occurrences(trigger, calendar, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5), 10, ct);
    }
    public async Task<IReadOnlyList<TriggerSummary>> PreviewCalendarAsync(string schedulerName, CalendarDefinition definition, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var calendar = DashboardBackend.Build(definition);
        var headers = await scheduler.QueryTriggers(new TriggerQuery { CalendarName = definition.Name, Take = PagedQuery.All }, ct).ConfigureAwait(false);
        var result = new List<TriggerSummary>();
        foreach (var header in headers.Items)
        {
            var trigger = await scheduler.GetTrigger(header.Key, ct).ConfigureAwait(false);
            if (trigger is null) continue;
            var times = DashboardBackend.Occurrences(trigger, calendar, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5), 1, ct);
            result.Add(DashboardBackend.Summary(header) with { NextFireTimeUtc = times.Count == 0 ? null : times[0] });
        }
        return result;
    }
    public async Task<IReadOnlyList<CalendarDetails>> GetCalendarsAsync(string schedulerName, CancellationToken ct)
    {
        var scheduler = await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        var result = new List<CalendarDetails>();
        foreach (var name in await scheduler.GetCalendarNames(ct).ConfigureAwait(false))
        {
            var calendar = await scheduler.GetCalendar(name, ct).ConfigureAwait(false);
            if (calendar is null) continue;
            var definition = DashboardBackend.Definition(name, calendar);
            var triggers = await scheduler.QueryTriggers(new TriggerQuery { CalendarName = name, Take = PagedQuery.All }, ct).ConfigureAwait(false);
            result.Add(new(definition, triggers.Items.Select(DashboardBackend.Summary).ToArray(), definition.Kind != "Unsupported"));
        }
        return result.OrderBy(x => x.Definition.Name, StringComparer.Ordinal).ToArray();
    }
    public async Task<IReadOnlyList<DashboardAuditEntry>> GetAuditAsync(string schedulerName, CancellationToken ct)
    {
        await backend.Scheduler(schedulerName, DashboardPermission.Administer, ct).ConfigureAwait(false);
        return audit.Read(schedulerName);
    }
    public async Task<IReadOnlyList<DashboardJobType>> GetJobTypesAsync(string schedulerName, CancellationToken ct)
    {
        await backend.Scheduler(schedulerName, DashboardPermission.View, ct).ConfigureAwait(false);
        return backend.Options.JobTypes.ToArray();
    }
}
