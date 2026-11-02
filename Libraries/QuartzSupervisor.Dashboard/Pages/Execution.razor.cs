using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Execution : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "scheduler")] public string? SchedulerName { get; set; }
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private DashboardExecution? _run;
    private SchedulerDetails? _details;
    private string _scheduler = "";
    private string? _loadedId, _error, _message;
    private bool _loading = true, _acting, _requested, _refreshQueued, _refreshPending;
    private int _version;
    private DateTimeOffset _captured;
    private bool CanCancel => !_requested && _run is { CompletedUtc: null, FireInstanceId: not null } && _details?.Capabilities.CanOperate == true && _run.InstanceId == _details.InstanceId;
    protected override void OnInitialized() { Updates.Changed += Changed; _ = PollAsync(); }
    protected override async Task OnParametersSetAsync()
    {
        ++_version;
        var name = SchedulerName ?? Context.SchedulerName;
        if (name != _scheduler || Id != _loadedId) { _run = null; _details = null; _message = null; _requested = false; }
        _scheduler = name; _loadedId = Id;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(Id)) { _error = "Choose an execution from a scheduler's timeline."; _loading = false; return; }
        Context.SchedulerName = name;
        await LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(_loadedId)) return;
        var version = ++_version;
        var scheduler = _scheduler;
        var id = _loadedId;
        try
        {
            var details = await Queries.GetSchedulerDetailsAsync(scheduler, _lifetime.Token);
            var run = await Queries.GetExecutionAsync(scheduler, id, _lifetime.Token);
            if (version != _version || _lifetime.IsCancellationRequested) return;
            _details = details; _run = run; _captured = DateTimeOffset.UtcNow; _error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _version) _error = ex.Message; }
        finally { if (version == _version) _loading = false; }
    }
    private async Task CancelAsync()
    {
        if (!CanCancel || _acting) return;
        var scheduler = _scheduler;
        var id = _loadedId;
        var fireId = _run!.FireInstanceId!;
        if (!await JS.InvokeAsync<bool>("confirm", "Request cooperative cancellation on this scheduler node? The job must observe its cancellation token; this does not force-stop the job or undo its work.")) return;
        _acting = true;
        try
        {
            var accepted = await Commands.InterruptExecutionAsync(scheduler, fireId, _lifetime.Token);
            if (scheduler != _scheduler || id != _loadedId) return;
            _requested = accepted;
            _message = accepted ? "Cancellation requested on this node. This does not confirm termination; the native execution outcome is shown below." : "No matching running execution accepted cancellation on this node. It may have finished or be executing on another node.";
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (scheduler == _scheduler && id == _loadedId) _error = ex.Message; }
        finally { _acting = false; }
    }
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
                    await Task.Delay(250, _lifetime.Token);
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) if (_run is { CompletedUtc: null }) Changed(_scheduler); }
        catch (OperationCanceledException) { }
    }
    private string EntityUrl(string page, string group, string name) => Context.Url(page, _scheduler, new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private static string Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'") ?? "Not reported";
    public void Dispose() { ++_version; Updates.Changed -= Changed; _lifetime.Cancel(); _lifetime.Dispose(); }
}
