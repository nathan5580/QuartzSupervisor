using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Triggers : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<SchedulerSummary> _schedulers = [];
    private DashboardPage<TriggerSummary> _page = new([], false, 0, 50);
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
                await LoadTriggersAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _error = "Trigger information is unavailable."; }
        finally { _loading = false; }
    }

    private Task LoadTriggersAsync() => LoadPageAsync(0);

    private Task SearchTriggersAsync() => LoadPageAsync(0);

    private Task PreviousPageAsync() => LoadPageAsync(Math.Max(0, _page.Skip - _page.PageSize));

    private Task NextPageAsync() => _page.HasMore ? LoadPageAsync(_page.Skip + _page.PageSize) : Task.CompletedTask;

    private async Task LoadPageAsync(int skip)
    {
        _loading = true;
        _error = null;
        try { _page = await Queries.GetTriggersAsync(_selectedName, _filter, skip, _lifetimeCancellation.Token); }
        catch (KeyNotFoundException) { _page = _page with { Items = [], HasMore = false, Skip = 0 }; _error = "Unknown scheduler. Select a registered scheduler."; }
        catch (OperationCanceledException) { }
        catch (Exception) { _page = _page with { Items = [], HasMore = false, Skip = 0 }; _error = "Triggers are unavailable."; }
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

    private Task PauseTriggerAsync(TriggerSummary trigger) =>
        RunCommandAsync(ct => Commands.PauseTriggerAsync(_selectedName, trigger.Group, trigger.Name, ct),
            $"Paused '{trigger.Group}.{trigger.Name}'.");

    private Task ResumeTriggerAsync(TriggerSummary trigger) =>
        RunCommandAsync(ct => Commands.ResumeTriggerAsync(_selectedName, trigger.Group, trigger.Name, ct),
            $"Resumed '{trigger.Group}.{trigger.Name}'.");

    private async Task DeleteTriggerAsync(TriggerSummary trigger)
    {
        if (!await JS.InvokeAsync<bool>("confirm",
                $"Remove trigger '{trigger.Group}.{trigger.Name}'? Quartz may also remove its non-durable job if this is the job's last trigger."))
            return;

        await RunCommandAsync(async ct =>
        {
            if (!await Commands.DeleteTriggerAsync(_selectedName, trigger.Group, trigger.Name, ct))
                throw new InvalidOperationException("The trigger was already removed.");
        }, $"Removed '{trigger.Group}.{trigger.Name}'.");
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
        catch (Exception ex) { _actionError = $"Trigger command failed: {ex.Message}"; }
        finally { _acting = false; }
    }

    private static string FormatTime(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "—";

    public void Dispose()
    {
        Updates.Changed -= OnDashboardChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
