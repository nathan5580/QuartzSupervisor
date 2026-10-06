using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QuartzSupervisor.Integration;

namespace QuartzSupervisor.Dashboard;

public static class QuartzSupervisorDashboardExtensions
{
    public static IServiceCollection AddQuartzSupervisorDashboard(this IServiceCollection services)
    {
        SchedulerDashboardServiceCollectionExtensions.AddQuartzSupervisorDashboard(services);
        services.AddAuthorization();
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }

    public static RazorComponentsEndpointConventionBuilder MapQuartzSupervisorDashboard(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapRazorComponents<DashboardApp>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization();
}
