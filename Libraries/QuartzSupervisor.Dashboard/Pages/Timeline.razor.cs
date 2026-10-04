using Microsoft.AspNetCore.Components;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Timeline : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private IReadOnlyList<DashboardActivity> _activity = [];
    private string _selectedName = "";
    private string? _error;
    private bool _loading = true;

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

    private Task RefreshActivity()
    {
        _activity = Updates.GetRecentActivity(string.IsNullOrEmpty(_selectedName) ? null : _selectedName);
        return Task.CompletedTask;
    }

    private void OnDashboardChanged(string schedulerName)
    {
        if (!string.IsNullOrEmpty(_selectedName) && !string.Equals(_selectedName, schedulerName, StringComparison.Ordinal))
            return;

        _ = InvokeAsync(async () =>
        {
            await RefreshActivity();
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
