using Microsoft.AspNetCore.Components;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Overview : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private DashboardOverview? _overview;
    private string _selectedName = "";
    private string? _error;
    private bool _loading = true;
    private string? _actionMessage;
    private string? _actionError;
    private bool _acting;

    protected override async Task OnInitializedAsync()
    {
        Updates.Changed += OnDashboardChanged;
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


    private void OnDashboardChanged(string schedulerName)
    {
        if (!string.Equals(_selectedName, schedulerName, StringComparison.Ordinal))
            return;

        _ = InvokeAsync(async () =>
        {
            if (_loading || _acting)
                return;
            await LoadOverviewAsync();
            StateHasChanged();
        });
    }

    private Task StartSchedulerAsync() =>
        RunSchedulerCommandAsync(Commands.StartSchedulerAsync, "Scheduler started.");

    private Task StandbySchedulerAsync() =>
        RunSchedulerCommandAsync(Commands.StandbySchedulerAsync, "Scheduler is in standby.");

    private async Task RunSchedulerCommandAsync(
        Func<string, CancellationToken, Task> command,
        string successMessage)
    {
        _acting = true;
        _actionMessage = null;
        _actionError = null;
        try
        {
            await command(_selectedName, _lifetimeCancellation.Token);
            _actionMessage = successMessage;
            await LoadOverviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _actionError = $"Scheduler command failed: {ex.Message}"; }
        finally { _acting = false; }
    }

    private static string StatusLabel(string status) => status switch
    {
        "Running" => "Running",
        "Standby" => "Standby",
        "Created" => "Not started",
        "ShuttingDown" => "Shutting down",
        _ => status
    };

    public void Dispose()
    {
        Updates.Changed -= OnDashboardChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
