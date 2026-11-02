using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard.Services;

/// <summary>Per-circuit scheduler selection and host/path-base aware dashboard links.</summary>
public sealed class DashboardContext
{
    private readonly NavigationManager _navigation;
    private readonly QuartzSupervisorOptions _options;
    private readonly ISchedulerDashboardAccess _access;
    private readonly string _applicationPath;
    private string schedulerName = "";

    public DashboardContext(NavigationManager navigation, IHttpContextAccessor http, QuartzSupervisorOptions options,
        ISchedulerDashboardAccess access)
    {
        _navigation = navigation;
        _options = options;
        _access = access;
        var basePath = new Uri(navigation.BaseUri).AbsolutePath.TrimEnd('/');
        _applicationPath = http.HttpContext?.Request.PathBase.Value
            ?? (basePath.EndsWith(options.Path, StringComparison.OrdinalIgnoreCase) ? basePath[..^options.Path.Length] : basePath);
        SynchronizeSelection();
    }

    public string SchedulerName
    {
        get => schedulerName;
        set
        {
            if (schedulerName == value) return;
            schedulerName = value;
            Changed?.Invoke();
        }
    }

    public string RootPath => _applicationPath + _options.Path;
    public string Title => _options.Title;
    public bool ReadOnly => _options.ReadOnly;
    public event Action? Changed;

    public string Url(string page = "", string? schedulerName = null, IReadOnlyDictionary<string, string?>? query = null)
    {
        page = page.Trim('/');
        if (page.Split('/').Any(segment => segment is "." or "..") || page.Contains('?') || page.Contains('#') || page.Contains('\\'))
            throw new ArgumentException("Page must be a dashboard-relative route.", nameof(page));
        var values = query is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(query);
        var selected = schedulerName ?? SchedulerName;
        if (!string.IsNullOrWhiteSpace(selected)) values["scheduler"] = selected;
        return QueryHelpers.AddQueryString(RootPath + (page.Length == 0 ? "/" : "/" + page), values);
    }

    public string Asset(string relative) => _applicationPath + "/_content/QuartzSupervisor/" + relative.TrimStart('/');
    internal string FrameworkScript => _applicationPath + "/_framework/blazor.web.js";

    public async Task<bool> CanAsync(DashboardPermission permission, string? schedulerName = null, CancellationToken ct = default)
    {
        try
        {
            await _access.AuthorizeAsync(schedulerName ?? SchedulerName, permission, ct);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void SelectScheduler(string name)
    {
        SchedulerName = name;
        var current = new Uri(_navigation.Uri);
        var page = current.AbsolutePath.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase)
            ? current.AbsolutePath[(RootPath.Length + 1)..] : "";
        var query = QueryHelpers.ParseQuery(current.Query).ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        foreach (var key in new[] { "group", "name", "id", "skip" }) query.Remove(key);
        if (page is "job" or "trigger" or "execution") page = page == "job" ? "jobs" : page == "trigger" ? "triggers" : "timeline";
        _navigation.NavigateTo(Url(page, name, query));
    }

    internal void SynchronizeSelection()
    {
        var query = QueryHelpers.ParseQuery(new Uri(_navigation.Uri).Query);
        if (query.TryGetValue("scheduler", out var selected)) SchedulerName = selected.ToString();
    }
}
