using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Triggers : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "search")] public string? Search { get; set; }
    [SupplyParameterFromQuery(Name = "group")] public string? Group { get; set; }
    [SupplyParameterFromQuery(Name = "state")] public string? State { get; set; }
    [SupplyParameterFromQuery(Name = "skip")] public int Skip { get; set; }
    [SupplyParameterFromQuery(Name = "scheduler")] public string? Scheduler { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private DashboardPage<TriggerSummary> _page = new([], false, 0, 50);
    private readonly HashSet<DashboardKey> _selected = [];
    private IReadOnlyList<BulkResult> _outcomes = [];
    private string _search = "", _group = "", _state = "", _scheduler = "";
    private string? _error, _message;
    private bool _loading, _acting, _operate, _admin;
    private int _request;
    protected override void OnInitialized() { Updates.Changed += OnUpdate; Context.Changed += OnContext; }
    protected override async Task OnParametersSetAsync()
    {
        _search = Search ?? ""; _group = Group ?? ""; _state = State ?? "";
        _selected.Clear(); _outcomes = [];
        await LoadAsync();
    }
    private void OnContext() => OnUpdate(Context.SchedulerName);
    private void OnUpdate(string scheduler)
    {
        if (scheduler != Context.SchedulerName || _acting || _lifetime.IsCancellationRequested) return;
        _ = InvokeAsync(async () => { await LoadAsync(); StateHasChanged(); });
    }
    private async Task LoadAsync()
    {
        var request = ++_request;
        var scheduler = Context.SchedulerName;
        if (_scheduler != scheduler)
        {
            _scheduler = scheduler; _page = new([], false, 0, 50); _selected.Clear();
            _operate = _admin = false; _message = null; _outcomes = [];
        }
        if (string.IsNullOrEmpty(scheduler)) return;
        _loading = true; _error = null;
        try
        {
            var operate = await Context.CanAsync(DashboardPermission.Operate, scheduler, _lifetime.Token);
            var admin = await Context.CanAsync(DashboardPermission.Administer, scheduler, _lifetime.Token);
            var page = await Queries.GetTriggersAsync(scheduler, Search, Math.Max(0, Skip), _lifetime.Token, Group, State);
            if (request != _request || scheduler != Context.SchedulerName) return;
            _operate = operate; _admin = admin; _page = page;
            _selected.IntersectWith(page.Items.Where(x => !x.IsManualRun).Select(x => new DashboardKey(x.Group, x.Name)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == _request) _error = $"Triggers could not be refreshed: {ex.Message}"; }
        finally { if (request == _request) _loading = false; }
    }
    private string PageUrl(int skip) => Context.Url("triggers", query: new Dictionary<string, string?>
    { ["search"] = _search, ["group"] = _group, ["state"] = _state, ["skip"] = skip.ToString() });
    private void SearchTriggers() => Navigation.NavigateTo(PageUrl(0));
    private string EntityUrl(string page, string group, string name) => Context.Url(page, query: new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private void Select(TriggerSummary trigger, ChangeEventArgs e)
    {
        var key = new DashboardKey(trigger.Group, trigger.Name);
        if (e.Value is true && _selected.Count < 100) _selected.Add(key); else _selected.Remove(key);
    }
    private void SelectAll(ChangeEventArgs e)
    {
        _selected.Clear();
        if (e.Value is true) foreach (var trigger in _page.Items.Where(x => !x.IsManualRun).Take(100)) _selected.Add(new(trigger.Group, trigger.Name));
    }
    private async Task BulkAsync(string action, TriggerSummary? single = null)
    {
        if (_acting) return;
        var keys = single is null ? _selected.ToArray() : [new DashboardKey(single.Group, single.Name)];
        var scheduler = _scheduler;
        if (keys.Length == 0) return;
        if (action == "delete" && !await JS.InvokeAsync<bool>("confirm", $"Delete {keys.Length} trigger(s) from {scheduler}? A non-durable job may also be removed when its last trigger is deleted. This cannot be undone.")) return;
        if (scheduler != Context.SchedulerName) return;
        _acting = true; _error = _message = null;
        try
        {
            var outcomes = await Commands.BulkTriggersAsync(scheduler, keys, action, _lifetime.Token);
            if (scheduler != Context.SchedulerName) return;
            _outcomes = outcomes;
            _message = $"{_outcomes.Count(x => x.Succeeded)} of {_outcomes.Count} trigger actions succeeded.";
            foreach (var result in _outcomes.Where(x => x.Succeeded)) _selected.Remove(new(result.Group, result.Name));
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (scheduler == Context.SchedulerName) _error = $"Action failed: {ex.Message}"; }
        finally { _acting = false; }
    }
    private static string Time(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "—";
    public void Dispose()
    {
        Updates.Changed -= OnUpdate; Context.Changed -= OnContext;
        ++_request; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
