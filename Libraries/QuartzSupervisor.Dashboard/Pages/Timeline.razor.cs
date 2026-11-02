using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Timeline : IAsyncDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "scheduler")] public string? SchedulerName { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? Search { get; set; }
    [SupplyParameterFromQuery(Name = "zone")] public string? Zone { get; set; }
    [SupplyParameterFromQuery(Name = "view")] public string? View { get; set; }
    [SupplyParameterFromQuery(Name = "follow")] public string? Follow { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private string _scheduler = "", _search = "", _zone = "utc", _view = "chart";
    private string? _error, _chartError;
    private bool _loading = true, _refreshQueued, _refreshPending, _follow, _inventoryTruncated;
    private int _version;
    private DateTimeOffset _from = DateTimeOffset.UtcNow.AddMinutes(-5), _to = DateTimeOffset.UtcNow.AddMinutes(5);
    private DateTime _inputFrom, _inputTo;
    private TimeZoneInfo _localZone = TimeZoneInfo.Utc;
    private DashboardTimeline? _data;
    private IReadOnlyList<JobSummary> _jobs = [];
    private IReadOnlyList<TriggerSummary> _triggers = [];
    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<Timeline>? _reference;

    protected override void OnInitialized() { Updates.Changed += Changed; _ = PollAsync(); }
    protected override async Task OnParametersSetAsync()
    {
        var parametersVersion = ++_version;
        var name = SchedulerName ?? Context.SchedulerName;
        try
        {
            if (string.IsNullOrEmpty(name)) name = (await Queries.GetSchedulersAsync(_lifetime.Token)).FirstOrDefault()?.Name ?? "";
            if (parametersVersion != _version || _lifetime.IsCancellationRequested) return;
            if (_scheduler != name)
            {
                if (_module is not null && _data is not null) await _module.InvokeVoidAsync("destroy", _container);
                _data = null; _jobs = []; _triggers = []; _chartError = null;
            }
            _scheduler = name;
            if (name.Length == 0) { _loading = false; return; }
            Context.SchedulerName = name;
            _search = Search ?? ""; _zone = Zone == "local" ? "local" : "utc"; _view = View == "list" ? "list" : "chart"; _follow = Follow == "true";
            if (From is not null && DateTimeOffset.TryParse(From, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from)) _from = from;
            if (To is not null && DateTimeOffset.TryParse(To, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var to)) _to = to;
            _inputFrom = _from.UtcDateTime; _inputTo = _to.UtcDateTime;
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _error = ex.Message; _loading = false; }
    }

    private async Task LoadAsync()
    {
        if (_scheduler.Length == 0) return;
        if (_to <= _from || _to - _from > TimeSpan.FromDays(31)) { _error = "Choose an end after the start, with a range of at most 31 days."; _loading = false; return; }
        if (_follow)
        {
            var half = (_to - _from) / 2;
            _from = DateTimeOffset.UtcNow - half;
            _to = _from + half * 2;
        }
        var version = ++_version;
        var scheduler = _scheduler;
        try
        {
            var data = await Queries.GetTimelineAsync(scheduler, _from, _to, _search, _lifetime.Token);
            if (version != _version || _lifetime.IsCancellationRequested) return;
            _data = data; _jobs = data.Jobs; _triggers = data.Triggers;
            _inventoryTruncated = data.InventoryTruncated; _error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _version) _error = ex.Message; }
        finally { if (version == _version) _loading = false; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_data is null) return;
        try
        {
            if (_module is null)
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", Context.Asset("dashboard-timeline.js"));
                _reference = DotNetObjectReference.Create(this);
                var zone = await _module.InvokeAsync<string>("localTimeZone");
                try { _localZone = TimeZoneInfo.FindSystemTimeZoneById(zone); }
                catch (TimeZoneNotFoundException) { _localZone = TimeZoneInfo.Utc; }
                if (View is null && await _module.InvokeAsync<bool>("prefersList")) _view = "list";
                StateHasChanged();
            }
            await _module.InvokeVoidAsync("render", _container, CreateData(), _reference);
        }
        catch (JSDisconnectedException) { }
        catch (JSException ex) { if (_chartError != ex.Message) { _chartError = ex.Message; StateHasChanged(); } }
    }

    private IEnumerable<(string Group, string Name)> VisibleJobs() => _jobs.Select(j => (j.Group, j.Name))
        .Concat(_data?.Executions.Select(e => (Group: e.JobGroup, Name: e.JobName)) ?? [])
        .Concat(_data?.Scheduled.Select(e => (Group: e.JobGroup, Name: e.JobName)) ?? [])
        .Distinct().Where(j => Matches(j.Group, j.Name)
            || _triggers.Any(t => t.JobGroup == j.Group && t.JobName == j.Name && Matches(t.Group, t.Name))
            || (_data?.Executions.Any(e => e.JobGroup == j.Group && e.JobName == j.Name && Matches(e.TriggerGroup, e.TriggerName)) ?? false)
            || (_data?.Scheduled.Any(e => e.JobGroup == j.Group && e.JobName == j.Name && Matches(e.TriggerGroup, e.TriggerName)) ?? false))
        .OrderBy(j => j.Group).ThenBy(j => j.Name);
    private bool Matches(string group, string name) => string.IsNullOrWhiteSpace(_search) || $"{group}/{name}".Contains(_search, StringComparison.OrdinalIgnoreCase);
    private static string Lane(string group, string name) => JsonSerializer.Serialize(new[] { group, name });
    private static string TriggerLane(string jobGroup, string jobName, string group, string name) => JsonSerializer.Serialize(new[] { jobGroup, jobName, group, name });
    private object CreateData()
    {
        var groups = new List<object>();
        var items = new List<object>();
        var triggersByJob = _triggers.ToLookup(t => (t.JobGroup, t.JobName));
        var executionsByJob = _data!.Executions.ToLookup(e => (e.JobGroup, e.JobName));
        var scheduledByJob = _data.Scheduled.ToLookup(e => (e.JobGroup, e.JobName));
        foreach (var job in VisibleJobs())
        {
            var lane = Lane(job.Group, job.Name);
            var children = triggersByJob[job].Select(t => (t.Group, t.Name))
                .Concat(executionsByJob[job].Where(e => !string.IsNullOrEmpty(e.TriggerName)).Select(e => (e.TriggerGroup, e.TriggerName)))
                .Concat(scheduledByJob[job].Select(e => (e.TriggerGroup, e.TriggerName))).Distinct().ToArray();
            groups.Add(new { id = lane, name = job.Name, detail = job.Group, url = EntityUrl("job", job.Group, job.Name), nestedGroups = children.Select(t => TriggerLane(job.Group, job.Name, t.Item1, t.Item2)).ToArray(), showNested = false });
            foreach (var trigger in children)
                groups.Add(new { id = TriggerLane(job.Group, job.Name, trigger.Item1, trigger.Item2), name = trigger.Item2, detail = trigger.Item1, url = EntityUrl("trigger", trigger.Item1, trigger.Item2) });
            foreach (var run in executionsByJob[job])
            {
                AddExecution(lane, "job:", run);
                if (!string.IsNullOrEmpty(run.TriggerName)) AddExecution(TriggerLane(job.Group, job.Name, run.TriggerGroup, run.TriggerName), "trigger:", run);
            }
            foreach (var occurrence in scheduledByJob[job])
            {
                AddScheduled(lane, "job:", occurrence);
                AddScheduled(TriggerLane(job.Group, job.Name, occurrence.TriggerGroup, occurrence.TriggerName), "trigger:", occurrence);
            }
        }
        return new
        {
            now = _data!.CapturedUtc,
            start = _from,
            end = _to,
            viewKey = $"{_scheduler}|{_from:O}|{_to:O}",
            zone = _zone,
            follow = _follow,
            visible = _view == "chart",
            groups,
            items,
            scriptUrl = Context.Asset("vendor/vis-timeline/standalone/umd/vis-timeline-graph2d.min.js"),
            styleUrl = Context.Asset("vendor/vis-timeline/styles/vis-timeline-graph2d.min.css")
        };
        void AddExecution(string group, string prefix, DashboardExecution run) => items.Add(new
        {
            id = prefix + run.Id,
            group,
            start = run.StartedUtc,
            end = run.CompletedUtc ?? _data!.CapturedUtc,
            type = "range",
            running = run.CompletedUtc is null,
            className = $"execution-{(run.CompletedUtc is null ? "running" : run.IsError ? "failed" : "completed")}{(run.IsManualRun ? " execution-manual" : "")}",
            title = RunTitle(run),
            content = "",
            url = RunUrl(run)
        });
        void AddScheduled(string group, string prefix, ScheduledOccurrence occurrence) => items.Add(new { id = prefix + "scheduled:" + occurrence.Id, group, start = occurrence.FireUtc, type = "point", running = false, className = "execution-scheduled", content = "", title = $"{occurrence.TriggerGroup}/{occurrence.TriggerName} · {Time(occurrence.FireUtc)} · {occurrence.State} · forecast, not a guaranteed run", url = EntityUrl("trigger", occurrence.TriggerGroup, occurrence.TriggerName) });
    }

    private void ApplyFilters()
    {
        _from = new DateTimeOffset(DateTime.SpecifyKind(_inputFrom, DateTimeKind.Utc));
        _to = new DateTimeOffset(DateTime.SpecifyKind(_inputTo, DateTimeKind.Utc));
        NavigateFilters();
    }
    private void Preset(double hours) { _from = DateTimeOffset.UtcNow.AddHours(-hours); _to = DateTimeOffset.UtcNow.AddHours(Math.Min(hours, 24)); NavigateFilters(); }
    private void ChangeRange(double scale, double shift)
    {
        var span = _to - _from;
        var midpoint = _from + span / 2 + span * shift;
        var half = TimeSpan.FromSeconds(Math.Clamp(span.TotalSeconds * scale, 60, TimeSpan.FromDays(31).TotalSeconds)) / 2;
        _from = midpoint - half; _to = midpoint + half; _follow = false;
        NavigateFilters();
    }
    private void NavigateFilters() => Navigation.NavigateTo(Context.Url("timeline", _scheduler, new Dictionary<string, string?> { ["from"] = _from.ToString("O"), ["to"] = _to.ToString("O"), ["q"] = _search, ["zone"] = _zone, ["view"] = _view, ["follow"] = _follow ? "true" : "false" }), replace: true);
    private void ToggleView() { _view = _view == "chart" ? "list" : "chart"; NavigateFilters(); }
    private void FollowNow() { _follow = !_follow; if (_follow) { var span = _to - _from; _from = DateTimeOffset.UtcNow - span / 2; _to = DateTimeOffset.UtcNow + span / 2; } NavigateFilters(); }
    [JSInvokable]
    public Task RangeChanged(string from, string to)
    {
        if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start) && DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end))
        { _from = start; _to = end; _follow = false; NavigateFilters(); }
        return Task.CompletedTask;
    }
    private string EntityUrl(string page, string group, string name) => Context.Url(page, _scheduler, new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private string RunUrl(DashboardExecution run) => Context.Url("execution", _scheduler, new Dictionary<string, string?> { ["id"] = run.Id });
    private string Time(DateTimeOffset value) => _zone == "local" ? TimeZoneInfo.ConvertTime(value, _localZone).ToString("yyyy-MM-dd HH:mm:ss zzz") : value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    private string Duration(DashboardExecution run) => run.CompletedUtc is { } completed ? (completed - run.StartedUtc).ToString(@"d\.hh\:mm\:ss") : $"Running at snapshot ({Time(_data!.CapturedUtc)})";
    private string RunTitle(DashboardExecution run) => $"{run.JobGroup}/{run.JobName} · {Time(run.StartedUtc)} · {Duration(run)} · {(run.IsManualRun ? "manual · " : "")}{(run.CompletedUtc is null ? "Running" : run.IsError ? "Failed" : "Completed")}";
    private void Changed(string name)
    {
        if (name != _scheduler || _lifetime.IsCancellationRequested) return;
        _ = InvokeAsync(async () =>
        {
            _refreshPending = true;
            if (_refreshQueued) return;
            _refreshQueued = true;
            try
            {
                while (_refreshPending && !_lifetime.IsCancellationRequested)
                {
                    _refreshPending = false;
                    await Task.Delay(300, _lifetime.Token);
                    await LoadAsync();
                    StateHasChanged();
                }
            }
            catch (OperationCanceledException) { }
            finally { _refreshQueued = false; }
        });
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) if (_data is not null) Changed(_scheduler); }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        ++_version; Updates.Changed -= Changed; _lifetime.Cancel();
        if (_module is not null) { try { await _module.InvokeVoidAsync("destroy", _container); await _module.DisposeAsync(); } catch (JSDisconnectedException) { } }
        _reference?.Dispose(); _lifetime.Dispose();
    }
}
