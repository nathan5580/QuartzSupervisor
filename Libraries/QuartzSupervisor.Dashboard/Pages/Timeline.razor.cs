using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Timeline : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private IReadOnlyList<DashboardActivity> _activity = [];
    private string _selectedName = "";
    private string? _error;
    private bool _loading = true;
    private ElementReference _timelineScroll;
    private bool _rendered;
    private bool _scrollToLatest = true;

    protected override async Task OnInitializedAsync()
    {
        Updates.Changed += OnDashboardChanged;
        try
        {
            _schedulers = await Queries.GetSchedulersAsync(_lifetimeCancellation.Token);
            await RefreshActivity();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _error = "Scheduler activity is unavailable."; }
        finally { _loading = false; }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _rendered = true;
        if (!_scrollToLatest || _activity.Count == 0)
            return;

        _scrollToLatest = false;
        await JS.InvokeVoidAsync("quartzSupervisorTimeline.scrollToLatest", _timelineScroll);
    }

    private Task RefreshActivity()
    {
        _activity = Updates.GetRecentActivity(string.IsNullOrEmpty(_selectedName) ? null : _selectedName);
        _scrollToLatest = true;
        return Task.CompletedTask;
    }

    private void OnDashboardChanged(string schedulerName)
    {
        if (!string.IsNullOrEmpty(_selectedName) && !string.Equals(_selectedName, schedulerName, StringComparison.Ordinal))
            return;

        _ = InvokeAsync(async () =>
        {
            var followLatest = !_rendered || _activity.Count == 0 ||
                await JS.InvokeAsync<bool>("quartzSupervisorTimeline.isAtEnd", _timelineScroll);
            await RefreshActivity();
            _scrollToLatest = followLatest;
            StateHasChanged();
        });
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    public void Dispose()
    {
        Updates.Changed -= OnDashboardChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
