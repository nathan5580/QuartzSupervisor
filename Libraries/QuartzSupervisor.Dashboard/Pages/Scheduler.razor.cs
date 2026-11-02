using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Pages;

public partial class Scheduler : IDisposable
{
    [Inject] public ISchedulerDashboardQueries Queries { get; set; } = default!;
    [Inject] public ISchedulerDashboardCommands Commands { get; set; } = default!;
    [Inject] public ISchedulerDashboardUpdates Updates { get; set; } = default!;
    [Inject] public DashboardContext Context { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "scheduler")] public string? SchedulerName { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private SchedulerDetails? _details;
    private IReadOnlyList<CalendarDetails> _calendars = [];
    private IReadOnlyList<DashboardAuditEntry> _audit = [];
    private IReadOnlyList<TriggerSummary> _affected = [];
    private IReadOnlyList<TriggerSummary> _prospective = [];
    private CalendarDefinition? _editor;
    private string _scheduler = "", _dates = "";
    private string? _error, _message, _formError, _auditError;
    private bool _loading = true, _acting, _replace, _reviewed, _refreshQueued, _refreshPending;
    private int _version;
    private DateTimeOffset _captured;
    protected override void OnInitialized() { Updates.Changed += Changed; _ = PollAsync(); }
    protected override async Task OnParametersSetAsync()
    {
        var parametersVersion = ++_version;
        var name = SchedulerName ?? Context.SchedulerName;
        try
        {
            if (string.IsNullOrEmpty(name)) name = (await Queries.GetSchedulersAsync(_lifetime.Token)).FirstOrDefault()?.Name ?? "";
            if (parametersVersion != _version || _lifetime.IsCancellationRequested) return;
            if (name != _scheduler) { _details = null; _editor = null; _message = null; _calendars = []; _audit = []; }
            _scheduler = name;
            if (name.Length == 0) { _loading = false; return; }
            Context.SchedulerName = name;
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _error = ex.Message; _loading = false; }
    }
    private async Task LoadAsync()
    {
        if (_scheduler.Length == 0) return;
        var version = ++_version;
        var scheduler = _scheduler;
        try
        {
            var details = await Queries.GetSchedulerDetailsAsync(scheduler, _lifetime.Token);
            var calendars = await Queries.GetCalendarsAsync(scheduler, _lifetime.Token);
            IReadOnlyList<DashboardAuditEntry> audit = [];
            string? auditError = null;
            if (details.Capabilities.CanAdminister)
            {
                try { audit = await Queries.GetAuditAsync(scheduler, _lifetime.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { auditError = ex.Message; }
            }
            if (version != _version || _lifetime.IsCancellationRequested) return;
            _details = details; _calendars = calendars; _audit = audit; _auditError = auditError; _error = null; _captured = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _version) _error = ex.Message; }
        finally { if (version == _version) _loading = false; }
    }
    private void NewCalendar()
    {
        _editor = new CalendarDefinition(); _dates = ""; _replace = false; _reviewed = false; _affected = []; _formError = null;
    }
    private void EditCalendar(CalendarDetails calendar)
    {
        var value = calendar.Definition;
        _editor = new CalendarDefinition { Name = value.Name, Kind = value.Kind, Description = value.Description, TimeZoneId = value.TimeZoneId, ExcludedDates = [.. value.ExcludedDates], ExcludedDays = [.. value.ExcludedDays], Revision = value.Revision };
        _dates = string.Join("\n", value.ExcludedDates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        _replace = true; _reviewed = false; _affected = calendar.AffectedTriggers; _formError = null;
    }
    private void SetDay(DayOfWeek day, ChangeEventArgs args)
    {
        if (_editor is null) return;
        if (args.Value is true) { if (!_editor.ExcludedDays.Contains(day)) _editor.ExcludedDays.Add(day); }
        else _editor.ExcludedDays.Remove(day);
        _reviewed = false;
    }
    private void InvalidateReview() { _reviewed = false; _formError = null; }
    private bool PrepareCalendar()
    {
        if (_editor is null) return false;
        _formError = null;
        if (string.IsNullOrWhiteSpace(_editor.Name) || _editor.Name.Length > 200) { _formError = "Enter a calendar name of 1–200 characters."; return false; }
        if (_editor.Kind == "Holiday")
        {
            var dates = new List<DateOnly>();
            foreach (var line in _dates.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!DateOnly.TryParseExact(line, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { _formError = $"Invalid date '{line}'. Use one YYYY-MM-DD date per line."; return false; }
                dates.Add(date);
            }
            if (dates.Count > 3660) { _formError = "Use at most 3,660 excluded dates."; return false; }
            _editor.ExcludedDates = dates.Distinct().Order().ToList(); _editor.ExcludedDays = [];
        }
        else { _editor.ExcludedDates = []; if (_editor.ExcludedDays.Count == 7) { _formError = "A weekly calendar must leave at least one day included."; return false; } }
        return true;
    }
    private async Task ReviewAsync()
    {
        if (!PrepareCalendar() || _acting) return;
        var scheduler = _scheduler;
        var editor = _editor;
        _acting = true;
        try
        {
            var calendars = await Queries.GetCalendarsAsync(scheduler, _lifetime.Token);
            if (scheduler != _scheduler || !ReferenceEquals(editor, _editor)) return;
            var existing = calendars.FirstOrDefault(c => c.Definition.Name == editor!.Name);
            if (_replace && existing?.Definition.Revision != editor!.Revision) { _formError = "This calendar changed since you opened it. Cancel and reopen it before editing."; return; }
            if (!_replace && existing is not null) { _formError = "That calendar name already exists. Edit the existing calendar instead."; return; }
            var prospective = await Queries.PreviewCalendarAsync(scheduler, editor!, _lifetime.Token);
            if (scheduler != _scheduler || !ReferenceEquals(editor, _editor)) return;
            _affected = existing?.AffectedTriggers ?? []; _prospective = prospective; _reviewed = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (scheduler == _scheduler) _formError = ex.Message; }
        finally { _acting = false; }
    }
    private async Task SaveAsync()
    {
        if (!_reviewed || _editor is null || _acting || !PrepareCalendar()) return;
        var scheduler = _scheduler;
        var editor = _editor;
        var replace = _replace;
        if (!await JS.InvokeAsync<bool>("confirm", $"{(replace ? "Replace" : "Create")} calendar '{editor.Name}'? Quartz will recalculate next fire times for affected triggers.")) return;
        _acting = true;
        try
        {
            await Commands.SaveCalendarAsync(scheduler, editor, replace, _lifetime.Token);
            if (scheduler != _scheduler || !ReferenceEquals(editor, _editor)) return;
            _editor = null; _message = $"Calendar '{editor.Name}' saved. Affected triggers have been recalculated."; await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (scheduler == _scheduler) _formError = ex.Message; }
        finally { _acting = false; }
    }
    private async Task DeleteAsync(CalendarDetails calendar)
    {
        if (_acting || _details?.Capabilities.CanAdminister != true) return;
        var scheduler = _scheduler;
        if (!await JS.InvokeAsync<bool>("confirm", $"Delete calendar '{calendar.Definition.Name}'? This cannot be undone. Quartz rejects deletion while triggers still reference it.")) return;
        _acting = true;
        try
        {
            var deleted = await Commands.DeleteCalendarAsync(scheduler, calendar.Definition.Name, _lifetime.Token);
            if (scheduler != _scheduler) return;
            _message = deleted ? $"Calendar '{calendar.Definition.Name}' deleted." : "Calendar was not found; it may already have been deleted.";
            if (_editor?.Name == calendar.Definition.Name) _editor = null;
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (scheduler == _scheduler) _error = ex.Message; }
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
                    await Task.Delay(300, _lifetime.Token);
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
    private string TriggerUrl(TriggerSummary trigger) => Context.Url("trigger", _scheduler, new Dictionary<string, string?> { ["group"] = trigger.Group, ["name"] = trigger.Name });
    private static string Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Not reported";
    public void Dispose() { ++_version; Updates.Changed -= Changed; _lifetime.Cancel(); _lifetime.Dispose(); }
}
