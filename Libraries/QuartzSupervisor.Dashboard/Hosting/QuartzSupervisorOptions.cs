using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard;

/// <summary>Host-owned dashboard configuration. Authentication is required unless explicitly disabled.</summary>
public sealed class QuartzSupervisorOptions
{
    public string Path { get; set; } = "/quartz-supervisor";
    public string Title { get; set; } = "Quartz Supervisor";
    public bool RequireAuthentication { get; set; } = true;
    public bool ReadOnly { get; set; }
    public string? ViewPolicy { get; set; }
    public string? OperatePolicy { get; set; }
    public string? AdministerPolicy { get; set; }
    public SchedulerDashboardOptions Management { get; internal set; } = new();

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        Path = Path.TrimEnd('/');
        if (Path.Length == 0 || Path[0] != '/' || Path.Contains("//", StringComparison.Ordinal)
            || Path.Split('/').Skip(1).Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            throw new ArgumentException("Dashboard Path must be a non-root absolute path with letter, digit, hyphen or underscore segments.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
    }
}
