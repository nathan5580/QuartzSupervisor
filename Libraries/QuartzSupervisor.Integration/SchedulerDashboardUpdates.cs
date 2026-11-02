using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace QuartzSupervisor.Integration;

public interface ISchedulerDashboardUpdates
{
    event Action<string>? Changed;
    DashboardFreshness GetFreshness(string schedulerName);
}
internal sealed class SchedulerDashboardEvents(ILogger<SchedulerDashboardEvents> logger) : ISchedulerDashboardUpdates, IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DashboardFreshness> _freshness = new(StringComparer.Ordinal);
    private int started;
    private int draining;
    public event Action<string>? Changed;
    public DashboardFreshness GetFreshness(string schedulerName) => _freshness.GetValueOrDefault(schedulerName) ?? new("snapshot", null, "Waiting for scheduler observation.");
    internal void State(string name, string mode, string? message = null)
    {
        _freshness.AddOrUpdate(name, new DashboardFreshness(mode, DateTimeOffset.UtcNow, message), (_, old) => old with { Mode = mode, Message = message, LastUpdatedUtc = DateTimeOffset.UtcNow });
        Notify(name);
    }
    internal void Notify(string name)
    {
        _pending[name] = 0;
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            // A single periodic drain bounds fanout regardless of job throughput.
            drain = new Timer(_ => Drain(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }
    }
    private Timer? drain;
    private void Drain()
    {
        if (Interlocked.Exchange(ref draining, 1) != 0) return;
        try
        {
            foreach (var name in _pending.Keys)
            {
                if (!_pending.TryRemove(name, out _)) continue;
                _freshness.AddOrUpdate(name, new DashboardFreshness("local", DateTimeOffset.UtcNow, null), (_, old) => old with { LastUpdatedUtc = DateTimeOffset.UtcNow });
                if (Changed is not { } handlers) continue;
                foreach (Action<string> handler in handlers.GetInvocationList())
                    try { handler(name); }
                    catch (Exception ex) { logger.LogWarning("Dashboard subscriber failed ({ErrorType})", ex.GetType().Name); }
            }
        }
        finally { Volatile.Write(ref draining, 0); }
    }
    public void Dispose() => drain?.Dispose();
}
internal sealed class SchedulerDashboardListenerRegistration(SchedulerDashboardEvents events)
{
    private readonly HashSet<IScheduler> _attached = [];
    public void EnsureAttached(IScheduler scheduler)
    {
        lock (_attached)
        {
            if (!_attached.Add(scheduler)) return;
            IListenerManager manager;
            try { manager = scheduler.ListenerManager; }
            catch (NotSupportedException) { return; }
            var listener = new SchedulerDashboardChangeListener(events);
            try
            {
                manager.AddSchedulerListener(listener);
                manager.AddJobListener(listener, [Matchers.AllJobs()]);
                manager.AddTriggerListener(listener, [Matchers.AllTriggers()]);
                events.State(scheduler.SchedulerName, "local");
            }
            catch { _attached.Remove(scheduler); throw; }
        }
    }
}
internal sealed class SchedulerDashboardObserver(ISchedulerFactory factory, SchedulerDashboardListenerRegistration listeners,
    SchedulerDashboardEvents events, IHttpClientFactory clients, IOptions<SchedulerDashboardOptions> options, ILogger<SchedulerDashboardObserver> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var remotes = options.Value.RemoteClients.Select(pair => ObserveRemote(pair.Key, pair.Value, stoppingToken)).ToArray();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var scheduler in await factory.GetAllSchedulers(stoppingToken).ConfigureAwait(false)) listeners.EnsureAttached(scheduler);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("Dashboard scheduler observation failed ({ErrorType}); retrying", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(remotes).ConfigureAwait(false); }
    }
    private async Task ObserveRemote(string name, string clientName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = clients.CreateClient(clientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"schedulers/{Uri.EscapeDataString(name)}/events");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new NotSupportedException("Target does not serve SSE.");
                events.State(name, "remote"); // Also invalidates after reconnect: native history reconciles events missed in the gap.
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct).ConfigureAwait(false))
                    events.Notify(name);
                events.State(name, "snapshot", "Remote event stream disconnected; periodic snapshot refresh is active.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or IOException or OperationCanceledException)
            {
                events.State(name, "snapshot", "Remote event stream unavailable; periodic snapshot refresh is active.");
                logger.LogWarning("Dashboard remote event stream for {Scheduler} unavailable ({ErrorType})", name, ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            events.Notify(name);
        }
    }
}
internal sealed class SchedulerDashboardChangeListener(SchedulerDashboardEvents events) : ISchedulerListener, IJobListener, ITriggerListener
{
    public string Name => nameof(SchedulerDashboardChangeListener);
    public ValueTask JobScheduled(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobUnscheduled(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerFinalized(IScheduler scheduler, ITrigger trigger, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerError(IScheduler scheduler, SchedulerErrorContext errorContext, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerInError(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggersInError(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobInterrupted(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobInterrupted(IScheduler scheduler, JobKey jobKey, string fireInstanceId, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerPaused(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggersPaused(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerResumed(IScheduler scheduler, TriggerKey triggerKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggersResumed(IScheduler scheduler, string? triggerGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobAdded(IScheduler scheduler, IJobDetail jobDetail, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobDeleted(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobPaused(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobsPaused(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobResumed(IScheduler scheduler, JobKey jobKey, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobsResumed(IScheduler scheduler, string? jobGroup, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerInStandbyMode(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerStarted(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerShuttingDown(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulerShutdown(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask SchedulingDataCleared(IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask TriggerMisfired(ITrigger trigger, IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerSkipped(ITrigger trigger, IScheduler scheduler, CancellationToken ct) => Notify(scheduler);
    public ValueTask TriggerRetriesExhausted(ITrigger trigger, IJobExecutionContext context, JobExecutionException jobException, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken ct) => Notify(context.Scheduler);
    public ValueTask TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction instruction, CancellationToken ct) => Notify(context.Scheduler);
    private ValueTask Notify(IScheduler scheduler) { events.Notify(scheduler.SchedulerName); return ValueTask.CompletedTask; }
}
