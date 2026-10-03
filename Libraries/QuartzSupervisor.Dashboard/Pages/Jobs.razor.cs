using Microsoft.AspNetCore.Components;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Jobs : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private DashboardPage<JobSummary> _page = new([], false, 0, 50);
    private string _selectedName = "";
    private string _filter = "";
    private string? _error;
    private bool _loading = true;
    private int PageNumber => _page.Skip / _page.PageSize + 1;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _schedulers = await Queries.GetSchedulersAsync(_lifetimeCancellation.Token);
            if (_schedulers.Count > 0)
            {
                _selectedName = _schedulers[0].Name;
                await LoadJobsAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _error = "Job information is unavailable."; }
        finally { _loading = false; }
    }

    private Task LoadJobsAsync() => LoadPageAsync(0);

    private Task SearchJobsAsync() => LoadPageAsync(0);

    private Task PreviousPageAsync() => LoadPageAsync(Math.Max(0, _page.Skip - _page.PageSize));

    private Task NextPageAsync() => _page.HasMore ? LoadPageAsync(_page.Skip + _page.PageSize) : Task.CompletedTask;

    private async Task LoadPageAsync(int skip)
    {
        _loading = true;
        _error = null;
        try { _page = await Queries.GetJobsAsync(_selectedName, _filter, skip, _lifetimeCancellation.Token); }
        catch (KeyNotFoundException) { _page = _page with { Items = [], HasMore = false, Skip = 0 }; _error = "Unknown scheduler. Select a registered scheduler."; }
        catch (OperationCanceledException) { }
        catch (Exception) { _page = _page with { Items = [], HasMore = false, Skip = 0 }; _error = "Jobs are unavailable."; }
        finally { _loading = false; }
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
