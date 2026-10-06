using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Jobs : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private DashboardPage<JobSummary> _page = new([], false, 0, 50);
    private string _selectedName = "";
    private string _filter = "";
    private string? _error;
    private bool _loading = true;
    private string? _actionMessage;
    private string? _actionError;
    private bool _acting;
    private int PageNumber => _page.Skip / _page.PageSize + 1;

    protected override async Task OnInitializedAsync()
    {
        Updates.Changed += OnDashboardChanged;
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

    private void OnDashboardChanged(string schedulerName)
    {
        if (!string.Equals(_selectedName, schedulerName, StringComparison.Ordinal))
            return;

        _ = InvokeAsync(async () =>
        {
            if (_loading || _acting)
                return;
            await LoadPageAsync(_page.Skip);
            StateHasChanged();
        });
    }

    private Task TriggerJobAsync(JobSummary job) =>
        RunCommandAsync(ct => Commands.TriggerJobAsync(_selectedName, job.Group, job.Name, ct),
            $"Triggered {job.Group}.{job.Name}.");

    private Task PauseJobAsync(JobSummary job) =>
        RunCommandAsync(ct => Commands.PauseJobAsync(_selectedName, job.Group, job.Name, ct),
            $"Paused '{job.Group}.{job.Name}'.");

    private Task ResumeJobAsync(JobSummary job) =>
        RunCommandAsync(ct => Commands.ResumeJobAsync(_selectedName, job.Group, job.Name, ct),
            $"Resumed '{job.Group}.{job.Name}'.");

    private async Task DeleteJobAsync(JobSummary job)
    {
        if (!await JS.InvokeAsync<bool>("confirm",
                $"Delete job '{job.Group}.{job.Name}' and all its associated triggers? This cannot be undone."))
            return;

        await RunCommandAsync(async ct =>
        {
            if (!await Commands.DeleteJobAsync(_selectedName, job.Group, job.Name, ct))
                throw new InvalidOperationException("The job was already removed.");
        }, $"Deleted '{job.Group}.{job.Name}' and its associated triggers.");
    }

    private async Task RunCommandAsync(Func<CancellationToken, Task> command, string successMessage)
    {
        _acting = true;
        _actionMessage = null;
        _actionError = null;
        try
        {
            await command(_lifetimeCancellation.Token);
            _actionMessage = successMessage;
            await LoadPageAsync(_page.Skip);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _actionError = $"Job command failed: {ex.Message}"; }
        finally { _acting = false; }
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    public void Dispose()
    {
        Updates.Changed -= OnDashboardChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
