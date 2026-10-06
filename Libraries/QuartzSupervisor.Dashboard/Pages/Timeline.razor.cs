using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Timeline : IAsyncDisposable
{
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(10);
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private IReadOnlyList<DashboardExecution> _executions = [];
    private IReadOnlyList<DashboardExecution> _visibleExecutions = [];
    private ElementReference _timelineContainer;
    private IJSObjectReference? _timelineModule;
    private string _selectedName = "";
    private string? _error;
    private bool _loading = true;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DateTimeOffset WindowStart => _now - _window;

    protected override async Task OnInitializedAsync()
    {
        Updates.Changed += OnDashboardChanged;
        try
        {
            _schedulers = await Queries.GetSchedulersAsync(_lifetimeCancellation.Token);
            await RefreshExecutions();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _error = "Scheduler executions are unavailable."; }
        finally { _loading = false; }

        _ = UpdateClockAsync(_lifetimeCancellation.Token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loading || _error is not null || _schedulers.Count == 0 ||
            (_visibleExecutions.Count == 0 && _timelineModule is null))
            return;

        _timelineModule ??= await JS.InvokeAsync<IJSObjectReference>(
            "import", "/_content/QuartzSupervisor/dashboard-timeline.js");
        await _timelineModule.InvokeVoidAsync("render", _timelineContainer, CreateTimelineData());
    }

    private Task RefreshExecutions()
    {
        _now = DateTimeOffset.UtcNow;
        _executions = Updates.GetRecentExecutions(string.IsNullOrEmpty(_selectedName) ? null : _selectedName);
        _visibleExecutions = _executions
            .Where(run => run.CompletedUtc is null || run.CompletedUtc >= WindowStart)
            .ToArray();
        return Task.CompletedTask;
    }

    private object CreateTimelineData() => new
    {
        now = _now,
        groups = _visibleExecutions
            .GroupBy(GroupId)
            .Select(group =>
            {
                var run = group.First();
                return new { id = group.Key, name = run.JobName, jobGroup = run.JobGroup, schedulerName = run.SchedulerName };
            })
            .ToArray(),
        items = _visibleExecutions.Select(run => new
        {
            id = run.Id,
            group = GroupId(run),
            start = run.StartedUtc,
            end = run.CompletedUtc ?? _now,
            className = $"{(run.CompletedUtc is null ? "execution-running" : run.IsError ? "execution-failed" : "execution-completed")}{(run.IsManualRun ? " execution-manual" : "")}",
            title = RunTitle(run)
        }).ToArray()
    };

    private static string GroupId(DashboardExecution run) =>
        JsonSerializer.Serialize(new[] { run.SchedulerName, run.JobGroup, run.JobName });

    private void OnDashboardChanged(string schedulerName)
    {
        if (!string.IsNullOrEmpty(_selectedName) && !string.Equals(_selectedName, schedulerName, StringComparison.Ordinal))
            return;

        _ = InvokeAsync(async () =>
        {
            await RefreshExecutions();
            StateHasChanged();
        });
    }

    private async Task UpdateClockAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _now = DateTimeOffset.UtcNow;
                _visibleExecutions = _executions
                    .Where(run => run.CompletedUtc is null || run.CompletedUtc >= WindowStart)
                    .ToArray();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
    }

    private string Duration(DashboardExecution run)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, ((run.CompletedUtc ?? _now) - run.StartedUtc).TotalSeconds));
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
    }

    private string RunTitle(DashboardExecution run) =>
        $"{run.JobGroup}.{run.JobName} · {run.SchedulerName} · started {run.StartedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC · {Duration(run)} · " +
        (run.IsManualRun ? "manual run · " : "") +
        (run.CompletedUtc is null ? "running" : run.IsError ? "failed" : "completed");

    private async Task ZoomAsync(double factor)
    {
        if (_timelineModule is not null)
            await _timelineModule.InvokeVoidAsync("zoom", _timelineContainer, factor);
    }

    private async Task GoLiveAsync()
    {
        if (_timelineModule is not null)
            await _timelineModule.InvokeVoidAsync("goLive", _timelineContainer, _now);
    }

    public async ValueTask DisposeAsync()
    {
        Updates.Changed -= OnDashboardChanged;
        _lifetimeCancellation.Cancel();
        if (_timelineModule is not null)
        {
            try
            {
                await _timelineModule.InvokeVoidAsync("destroy", _timelineContainer);
                await _timelineModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
        _lifetimeCancellation.Dispose();
    }
}

