using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using QuartzSupervisor.Dashboard.Hosting;
using QuartzSupervisor.Dashboard.Services;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard;

public static class QuartzSupervisorDashboardExtensions
{
    public static IServiceCollection AddQuartzSupervisorDashboard(this IServiceCollection services, Action<QuartzSupervisorOptions>? configure = null)
    {
        var options = new QuartzSupervisorOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddQuartzSupervisorIntegration(management =>
        {
            management.PageSize = options.Management.PageSize;
            management.MaxTimelineItems = options.Management.MaxTimelineItems;
            management.JobTypes.AddRange(options.Management.JobTypes);
            foreach (var client in options.Management.RemoteClients) management.RemoteClients[client.Key] = client.Value;
            management.AuditSink = options.Management.AuditSink;
            management.RedactDiagnostic = options.Management.RedactDiagnostic;
            options.Management = management;
        });
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.TryAddSingleton<DashboardMapping>();
        services.AddScoped<DashboardCircuitIdentity>();
        services.AddScoped<CircuitHandler>(provider => provider.GetRequiredService<DashboardCircuitIdentity>());
        services.Replace(ServiceDescriptor.Scoped<ISchedulerDashboardAccess, DashboardAccess>());
        services.AddScoped<DashboardContext>();
        return services;
    }

    /// <summary>Reuses a host-configured named HttpClient for the native Quartz API and server-side event stream.</summary>
    public static IServiceCollection AddQuartzSupervisorRemote(this IServiceCollection services, string schedulerName, string httpClientName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schedulerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        services.AddQuartzHttpClient(schedulerName, httpClientName);
        services.Configure<SchedulerDashboardOptions>(options => options.RemoteClients[schedulerName] = httpClientName);
        return services;
    }

    /// <summary>Maps an isolated component app and circuit hub under the configured dashboard prefix.</summary>
    public static RazorComponentsEndpointConventionBuilder MapQuartzSupervisorDashboard(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<QuartzSupervisorOptions>();
        var mapping = endpoints.ServiceProvider.GetRequiredService<DashboardMapping>();
        if (mapping.Endpoints is not null)
            throw new InvalidOperationException("MapQuartzSupervisorDashboard must be called only once per host.");
        mapping.Endpoints = () => endpoints.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints;
        var dashboard = endpoints.MapGroup(options.Path).MapRazorComponents<DashboardApp>()
            .AddInteractiveServerRenderMode()
            .WithMetadata(new DashboardEndpointMetadata());
        if (options.RequireAuthentication) dashboard.RequireAuthorization();
        return dashboard;
    }
}
