using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Job : IDisposable
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
    [SupplyParameterFromQuery(Name = "run")] public bool Run { get; set; }
    [SupplyParameterFromQuery(Name = "scheduler")] public string? Scheduler { get; set; }
    [SupplyParameterFromQuery(Name = "historySkip")] public int HistorySkip { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private JobDetails? _job;
    private IReadOnlyList<DashboardJobType> _types = [];
    private IReadOnlyList<DashboardExecution> _running = [];
    private DashboardHistory? _history;
    private JobDefinition _draft = new();
    private Dictionary<string, string> _parameters = [];
    private (string Scheduler, string? Group, string? Name, bool Create)? _identity;
    private bool _loading, _acting, _operate, _admin;
    private string? _error, _message;
    private int _request;
    private IReadOnlyList<DashboardParameter> CreationSchema => _types.FirstOrDefault(x => x.Key == _draft.JobTypeKey)?.Parameters ?? [];
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
            _identity = identity; _job = null; _history = null; _running = []; _types = [];
            _draft = new(); _parameters = []; _operate = _admin = false; _message = null;
        }
        var request = ++_request;
        if (string.IsNullOrEmpty(identity.SchedulerName)) return;
        _loading = true; _error = null;
        try
        {
            var operate = await Context.CanAsync(DashboardPermission.Operate, identity.SchedulerName, _lifetime.Token);
            var admin = await Context.CanAsync(DashboardPermission.Administer, identity.SchedulerName, _lifetime.Token);
            var types = Create && admin ? await Queries.GetJobTypesAsync(identity.SchedulerName, _lifetime.Token) : [];
            var job = !Create && !string.IsNullOrEmpty(Name)
                ? await Queries.GetJobAsync(identity.SchedulerName, Group ?? "DEFAULT", Name, _lifetime.Token) : null;
            var running = job is null ? [] : await Queries.GetRunningAsync(identity.SchedulerName, _lifetime.Token);
            var history = job is null ? null : await Queries.GetHistoryAsync(identity.SchedulerName,
                new ExecutionFilter { JobGroup = job.Summary.Group, JobName = job.Summary.Name, Skip = Math.Max(0, HistorySkip) }, _lifetime.Token);
            if (request != _request || identity.SchedulerName != Context.SchedulerName) return;
            _operate = operate; _admin = admin; _types = types; _job = job; _history = history;
            _running = job is null ? [] : running.Where(x => x.JobGroup == job.Summary.Group && x.JobName == job.Summary.Name).ToArray();
            if (changed && job is not null) _parameters = job.Parameters.ToDictionary(x => x.Key, x => x.Value);
            if (string.IsNullOrEmpty(_draft.JobTypeKey) && types.Count > 0) _draft.JobTypeKey = types[0].Key;
            if (!Create && job is null) _error = "Job not found. It may have been removed; return to Jobs or select another scheduler.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == _request) _error = $"Job could not be refreshed: {ex.Message}"; }
        finally { if (request == _request) _loading = false; }
    }
    private void TypeChanged() => _draft.Parameters.Clear();
    private string EntityUrl(string page, string group, string name) => Context.Url(page, query: new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private string ExecutionUrl(DashboardExecution execution) => Context.Url("execution", query: new Dictionary<string, string?> { ["id"] = execution.Id });
    private string HistoryUrl(int skip) => Context.Url("job", query: new Dictionary<string, string?> { ["group"] = Group, ["name"] = Name, ["historySkip"] = skip.ToString() });
    private string NewTriggerUrl() => Context.Url("trigger", query: new Dictionary<string, string?> { ["create"] = "true", ["jobGroup"] = _job!.Summary.Group, ["jobName"] = _job.Summary.Name });
    private Task CreateAsync() => ActAsync(async scheduler =>
    {
        await Commands.CreateJobAsync(scheduler, _draft, _lifetime.Token);
        if (scheduler == Context.SchedulerName) Navigation.NavigateTo(EntityUrl("job", _draft.Group, _draft.Name));
    }, "Job created.");
    private Task RunAsync() => ActAsync(async scheduler =>
    {
        var job = _job ?? throw new InvalidOperationException("Refresh this job before running it.");
        await Commands.TriggerJobAsync(scheduler, job.Summary.Group, job.Summary.Name, _lifetime.Token, _parameters);
        foreach (var field in job.ParameterSchema.Where(x => x.Sensitive)) _parameters.Remove(field.Name);
    }, "Run requested. Running executions and native history below show the outcome.");
    private bool SchedulesPaused => _job is not null && _job.Triggers.Count > 0 && _job.Triggers.All(x => x.State == "Paused");
    private Task PauseResumeAsync() => ActAsync(async scheduler =>
    {
        var job = _job!;
        if (SchedulesPaused) await Commands.ResumeJobAsync(scheduler, job.Summary.Group, job.Summary.Name, _lifetime.Token);
        else await Commands.PauseJobAsync(scheduler, job.Summary.Group, job.Summary.Name, _lifetime.Token);
    }, "Job schedule state updated.");
    private async Task DeleteAsync()
    {
        if (_job is null || _acting) return;
        var job = _job.Summary; var scheduler = Context.SchedulerName;
        if (!await JS.InvokeAsync<bool>("confirm", $"Delete {job.Group} / {job.Name} and all its triggers from {scheduler}? This cannot be undone.") || scheduler != Context.SchedulerName) return;
        await ActAsync(async selected =>
        {
            if (!await Commands.DeleteJobAsync(selected, job.Group, job.Name, _lifetime.Token)) throw new InvalidOperationException("Job was already removed.");
            if (selected == Context.SchedulerName) Navigation.NavigateTo(Context.Url("jobs"));
        }, "Job deleted.");
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
        catch (Exception ex) { if (identity == _identity) _error = $"Action failed: {ex.Message}"; }
        finally { _acting = false; }
    }
    private static string Time(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "—";
    public void Dispose() { Updates.Changed -= OnUpdate; Context.Changed -= OnContext; ++_request; _lifetime.Cancel(); _lifetime.Dispose(); }
}
