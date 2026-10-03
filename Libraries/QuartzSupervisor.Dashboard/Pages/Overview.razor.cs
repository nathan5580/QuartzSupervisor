using Microsoft.AspNetCore.Components;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Overview : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private DashboardOverview? _overview;
    private string _selectedName = "";
    private string? _error;
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _schedulers = await Queries.GetSchedulersAsync(_lifetimeCancellation.Token);
            if (_schedulers.Count > 0)
            {
                _selectedName = _schedulers[0].Name;
                await LoadOverviewAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _error = "Scheduler information is unavailable."; }
        finally { _loading = false; }
    }

    private async Task LoadOverviewAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _overview = await Queries.GetOverviewAsync(_selectedName, _lifetimeCancellation.Token);
        }
        catch (KeyNotFoundException)
        {
            _overview = null;
            _error = "Unknown scheduler. Select a registered scheduler.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _overview = null; _error = "Scheduler status is unavailable."; }
        finally { _loading = false; }
    }


    private static string StatusLabel(string status) => status switch
    {
        "Started" => "Running",
        "InStandbyMode" => "Standby",
        _ => status
    };

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
