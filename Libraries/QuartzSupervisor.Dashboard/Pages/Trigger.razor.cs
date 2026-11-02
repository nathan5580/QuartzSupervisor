using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Trigger : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "group")] public string? Group { get; set; }
    [SupplyParameterFromQuery(Name = "name")] public string? Name { get; set; }
    [SupplyParameterFromQuery(Name = "create")] public bool Create { get; set; }
    [SupplyParameterFromQuery(Name = "jobGroup")] public string? JobGroup { get; set; }
    [SupplyParameterFromQuery(Name = "jobName")] public string? JobName { get; set; }
    [SupplyParameterFromQuery(Name = "scheduler")] public string? Scheduler { get; set; }
    [SupplyParameterFromQuery(Name = "historySkip")] public int HistorySkip { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private TriggerDetails? _trigger;
    private IReadOnlyList<CalendarDetails> _calendars = [];
    private IReadOnlyList<DateTimeOffset> _preview = [];
    private IReadOnlyList<DashboardExecution> _running = [];
    private DashboardHistory? _history;
    private TriggerDefinition _draft = new();
    private TriggerDefinition? _before, _review;
    private DateTime? _start, _end;
    private (string Scheduler, string? Group, string? Name, bool Create)? _identity;
    private bool _loading, _acting, _operate, _admin, _editing;
    private string? _error, _message;
    private int _request;
    private static readonly string[] _timeZones = TimeZoneInfo.GetSystemTimeZones().Select(x => x.Id).ToArray();
    private IEnumerable<string> Misfires => _draft.Kind == "Cron"
        ? ["SmartPolicy", "IgnoreMisfires", "FireNow", "DoNothing"]
        : ["SmartPolicy", "IgnoreMisfires", "FireNow", "NextWithRemainingCount", "NextWithExistingCount", "NowWithRemainingCount", "NowWithExistingCount"];
    protected override void OnInitialized() { Updates.Changed += OnUpdate; Context.Changed += OnContext; }
    protected override Task OnParametersSetAsync() => LoadAsync();
    private void OnContext() => OnUpdate(Context.SchedulerName);
    private void OnUpdate(string scheduler)
    {
        if (scheduler != Context.SchedulerName || _acting || _loading || _lifetime.IsCancellationRequested) return;
        _ = InvokeAsync(async () => { await LoadAsync(); StateHasChanged(); });
    }
    private async Task LoadAsync()
    {
        var identity = (Context.SchedulerName, Group, Name, Create);
        var changed = _identity != identity;
        if (changed)
        {
            _identity = identity; _trigger = null; _before = _review = null; _preview = [];
            _draft = new() { JobGroup = JobGroup ?? "DEFAULT", JobName = JobName ?? "" };
            _editing = Create; _operate = _admin = false; _message = null; _start = _end = null;
            _history = null; _running = [];
        }
        var request = ++_request;
        if (string.IsNullOrEmpty(identity.SchedulerName)) return;
        _loading = true; _error = null;
        try
        {
            var operate = await Context.CanAsync(DashboardPermission.Operate, identity.SchedulerName, _lifetime.Token);
            var admin = await Context.CanAsync(DashboardPermission.Administer, identity.SchedulerName, _lifetime.Token);
            var trigger = !Create && !string.IsNullOrEmpty(Name) ? await Queries.GetTriggerAsync(identity.SchedulerName, Group ?? "DEFAULT", Name, _lifetime.Token) : null;
            var calendars = admin ? await Queries.GetCalendarsAsync(identity.SchedulerName, _lifetime.Token) : [];
            var running = trigger is null ? [] : await Queries.GetRunningAsync(identity.SchedulerName, _lifetime.Token);
            var history = trigger is null ? null : await Queries.GetHistoryAsync(identity.SchedulerName,
                new ExecutionFilter { TriggerGroup = trigger.Summary.Group, TriggerName = trigger.Summary.Name, Skip = Math.Max(0, HistorySkip) }, _lifetime.Token);
            if (request != _request || identity.SchedulerName != Context.SchedulerName) return;
            _operate = operate; _admin = admin; _trigger = trigger; _calendars = calendars;
            _history = history;
            _running = trigger is null ? [] : running.Where(x => x.TriggerGroup == trigger.Summary.Group && x.TriggerName == trigger.Summary.Name).ToArray();
            if (!Create && trigger is null) _error = "Trigger not found. It may have completed or been removed; return to Triggers.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == _request) _error = $"Trigger could not be refreshed: {ex.Message}"; }
        finally { if (request == _request) _loading = false; }
    }
    private void BeginEdit()
    {
        if (_trigger is null || !_trigger.Editable) return;
        _before = Copy(_trigger.Definition); _draft = Copy(_trigger.Definition);
        _start = _draft.StartUtc?.UtcDateTime; _end = _draft.EndUtc?.UtcDateTime;
        _review = null; _preview = []; _editing = true; _error = _message = null;
    }
    private void CancelEdit() { _editing = false; _review = null; _preview = []; }
    private void KindChanged() { _draft.Misfire = "SmartPolicy"; _review = null; _preview = []; }
    private async Task PreviewAsync()
    {
        if (_acting) return;
        var identity = _identity; var scheduler = Context.SchedulerName;
        _acting = true; _error = null; _review = null;
        try
        {
            _draft.StartUtc = _start is null ? DateTimeOffset.UtcNow : new DateTimeOffset(DateTime.SpecifyKind(_start.Value, DateTimeKind.Utc));
            _draft.EndUtc = _end is null ? null : new DateTimeOffset(DateTime.SpecifyKind(_end.Value, DateTimeKind.Utc));
            _draft.CalendarName = string.IsNullOrEmpty(_draft.CalendarName) ? null : _draft.CalendarName;
            var review = Copy(_draft);
            var preview = await Queries.PreviewTriggerAsync(scheduler, review, _lifetime.Token);
            if (identity != _identity || scheduler != Context.SchedulerName) return;
            _review = review; _preview = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (identity == _identity) _error = $"Schedule could not be previewed: {ex.Message} Correct the fields and preview again."; }
        finally { _acting = false; }
    }
    private Task SaveAsync() => ActAsync(async scheduler =>
    {
        var review = _review ?? throw new InvalidOperationException("Preview and review the schedule before saving.");
        await Commands.SaveTriggerAsync(scheduler, review, !Create, _lifetime.Token);
        if (scheduler != Context.SchedulerName) return;
        _editing = false; _review = null;
        if (Create) Navigation.NavigateTo(EntityUrl("trigger", review.Group, review.Name));
    }, "Schedule saved. The next fire time reflects Quartz state.");
    private Task PauseResumeAsync() => ActAsync(async scheduler =>
    {
        var trigger = _trigger!;
        if (trigger.Summary.State == "Paused") await Commands.ResumeTriggerAsync(scheduler, trigger.Summary.Group, trigger.Summary.Name, _lifetime.Token);
        else await Commands.PauseTriggerAsync(scheduler, trigger.Summary.Group, trigger.Summary.Name, _lifetime.Token);
    }, "Trigger state updated.");
    private Task ResetAsync() => ActAsync(scheduler => Commands.ResetTriggerAsync(scheduler, _trigger!.Summary.Group, _trigger.Summary.Name, _lifetime.Token),
        "Error state reset. Quartz will reevaluate the trigger; inspect the job history if it fails again.");
    private async Task DeleteAsync()
    {
        if (_trigger is null || _acting) return;
        var trigger = _trigger.Summary; var scheduler = Context.SchedulerName;
        if (!await JS.InvokeAsync<bool>("confirm", $"Delete {trigger.Group} / {trigger.Name} from {scheduler}? Quartz may remove its non-durable job if this is its last trigger. This cannot be undone.") || scheduler != Context.SchedulerName) return;
        await ActAsync(async selected =>
        {
            if (!await Commands.DeleteTriggerAsync(selected, trigger.Group, trigger.Name, _lifetime.Token)) throw new InvalidOperationException("Trigger was already removed.");
            if (selected == Context.SchedulerName) Navigation.NavigateTo(Context.Url("triggers"));
        }, "Trigger deleted.");
    }
    private async Task ActAsync(Func<string, Task> command, string message)
    {
        if (_acting) return;
        _acting = true; _error = _message = null;
        var identity = _identity; var scheduler = Context.SchedulerName;
        try
        {
            await command(scheduler);
            if (identity == _identity && scheduler == Context.SchedulerName) { _message = message; await LoadAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (identity == _identity) _error = $"Action failed: {ex.Message} Your draft has been kept. For a revision conflict, cancel editing, refresh, and reapply your changes."; }
        finally { _acting = false; }
    }
    private string EntityUrl(string page, string group, string name) => Context.Url(page, query: new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private string ExecutionUrl(DashboardExecution execution) => Context.Url("execution", query: new Dictionary<string, string?> { ["id"] = execution.Id });
    private string HistoryUrl(int skip) => Context.Url("trigger", query: new Dictionary<string, string?> { ["group"] = Group, ["name"] = Name, ["historySkip"] = skip.ToString() });
    private static string Time(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "—";
    private static TriggerDefinition Copy(TriggerDefinition value) => new()
    {
        Group = value.Group,
        Name = value.Name,
        JobGroup = value.JobGroup,
        JobName = value.JobName,
        Kind = value.Kind,
        Description = value.Description,
        CronExpression = value.CronExpression,
        IntervalSeconds = value.IntervalSeconds,
        RepeatCount = value.RepeatCount,
        TimeZoneId = value.TimeZoneId,
        StartUtc = value.StartUtc,
        EndUtc = value.EndUtc,
        Priority = value.Priority,
        CalendarName = value.CalendarName,
        Misfire = value.Misfire,
        Revision = value.Revision
    };
    private static IReadOnlyList<(string Label, string Value)> Configuration(TriggerDefinition value) =>
    [
        ("Trigger", $"{value.Group} / {value.Name}"), ("Job", $"{value.JobGroup} / {value.JobName}"), ("Kind", value.Kind),
        ("Description", value.Description ?? "—"),
        ("Schedule", value.Kind == "Cron" ? value.CronExpression : value.Kind == "Once" ? "Once" : value.Kind == "Simple" ? $"Every {value.IntervalSeconds.ToString(CultureInfo.InvariantCulture)} seconds; repeats {(value.RepeatCount == -1 ? "forever" : value.RepeatCount.ToString(CultureInfo.InvariantCulture))}" : "Unsupported native schedule"),
        ("Time zone", value.Kind == "Cron" ? value.TimeZoneId : "UTC (absolute elapsed time)"), ("Start", Time(value.StartUtc)),
        ("End", value.EndUtc is null ? "No end" : Time(value.EndUtc)), ("Priority", value.Priority.ToString(CultureInfo.InvariantCulture)),
        ("Calendar", value.CalendarName ?? "None"), ("Misfire policy", value.Misfire)
    ];
    public void Dispose() { Updates.Changed -= OnUpdate; Context.Changed -= OnContext; ++_request; _lifetime.Cancel(); _lifetime.Dispose(); }
}
