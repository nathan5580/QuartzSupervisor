using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Overview : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "scheduler")] public string? SchedulerName { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private DashboardOverview? _overview;
    private SchedulerDetails? _details;
    private IReadOnlyList<DashboardExecution> _running = [];
    private DashboardHistory? _failures;
    private string _scheduler = "";
    private string? _error, _message;
    private bool _loading = true, _acting, _refreshQueued, _refreshPending;
    private int _version;
    private DateTimeOffset _captured;

    protected override void OnInitialized()
    {
        Updates.Changed += Changed;
        _ = PollAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        var parametersVersion = ++_version;
        var name = SchedulerName ?? Context.SchedulerName;
        if (string.IsNullOrEmpty(name))
        {
            try { name = (await Queries.GetSchedulersAsync(_lifetime.Token)).FirstOrDefault()?.Name ?? ""; }
            catch (Exception ex) when (ex is not OperationCanceledException) { _error = ex.Message; _loading = false; return; }
        }
        if (parametersVersion != _version || _lifetime.IsCancellationRequested) return;
        if (_scheduler != name) { _overview = null; _details = null; _message = null; }
        _scheduler = name;
        if (name.Length == 0) { _loading = false; return; }
        Context.SchedulerName = name;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        var scheduler = _scheduler;
        try
        {
            var overview = await Queries.GetOverviewAsync(scheduler, _lifetime.Token);
            var details = await Queries.GetSchedulerDetailsAsync(scheduler, _lifetime.Token);
            var running = await Queries.GetRunningAsync(scheduler, _lifetime.Token);
            var failures = await Queries.GetHistoryAsync(scheduler, new ExecutionFilter { FailedOnly = true, FromUtc = DateTimeOffset.UtcNow.AddDays(-1) }, _lifetime.Token);
            if (version != _version || _lifetime.IsCancellationRequested) return;
            _overview = overview; _details = details; _running = running; _failures = failures;
            _captured = DateTimeOffset.UtcNow; _error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _version) _error = ex.Message; }
        finally { if (version == _version) _loading = false; }
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) if (_details?.Capabilities.Remote == true) Changed(_scheduler); }
        catch (OperationCanceledException) { }
    }

    private async Task ChangeStatusAsync()
    {
        if (_overview is null || _acting) return;
        var scheduler = _scheduler;
        var standby = _overview.Scheduler.Status == "Running";
        if (standby && !await JS.InvokeAsync<bool>("confirm", "Put this scheduler in standby? Future firings pause; running jobs continue.")) return;
        _acting = true; _message = null;
        try
        {
            if (standby) await Commands.StandbySchedulerAsync(scheduler, _lifetime.Token);
            else await Commands.StartSchedulerAsync(scheduler, _lifetime.Token);
            if (scheduler != _scheduler) return;
            _message = standby ? "Scheduler is in standby. Running jobs are not cancelled." : "Scheduler started.";
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (scheduler == _scheduler) _error = ex.Message; }
        finally { _acting = false; }
    }

    private string EntityUrl(string page, string group, string name) => Context.Url(page, _scheduler, new Dictionary<string, string?> { ["group"] = group, ["name"] = name });
    private string RunUrl(DashboardExecution run) => Context.Url("execution", _scheduler, new Dictionary<string, string?> { ["id"] = run.Id });
    private static string Time(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Not available";
    public void Dispose() { ++_version; Updates.Changed -= Changed; _lifetime.Cancel(); _lifetime.Dispose(); }
}
