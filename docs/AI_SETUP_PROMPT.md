# Guided AI setup prompt

Paste this into a coding agent opened at the root of the application that should host Quartz Supervisor.

---

Integrate Quartz Supervisor with the smallest complete change that fits this host. Use the [current README](../README.md) as the installation/API source of truth.

1. **Inspect.** Identify the .NET web host, existing Quartz registrations/stores, scheduler names, routes/PathBase, browser authentication, authorization policies, deployment boundaries, and secret providers. Check package availability before choosing a local package or project reference. Completion: the proposed integration preserves the existing host and scheduler configuration.
2. **Choose topology.** Use an in-process dashboard beside the scheduler, or a central dashboard with `AddQuartzSupervisorRemote(schedulerName, namedHttpClient)` for each remote target. Reuse named clients only when API URL and credentials match. Independent schedulers need unique names; duplicate names are not failover. Completion: every scheduler maps to an exact API name and authenticated client.
3. **Resolve decisions.** State the mount path, topology, and access model. Ask only for materially missing decisions not available in repository/configuration: dashboard host, non-secret remote addresses/names, trusted access boundary, or existing sign-in/policies. Never request credential values.
4. **Embed.** Add `AddQuartzSupervisorDashboard(options => ...)` and `MapQuartzSupervisorDashboard()` with static assets and antiforgery. Preserve host routes and authentication middleware. Use configured `Path` and the host's `UsePathBase` before routing. Authentication remains required unless anonymous mode is an explicit decision. Configure View/Operate/Administer policies and ReadOnly as needed; resource-aware policies receive the scheduler name string. Keep native history/store/schema ownership in the scheduler host.
5. **Approve management.** Register only approved job implementations and parameter schemas if job creation/overrides are needed. Sensitive stored values stay hidden. Diagnostics remain withheld unless a deliberate host redactor is provided. Native persistent history and a durable administrative audit sink are separate host decisions; do not create a second history database.
6. **Verify.** Build and run the non-production host. Exercise the actual mounted browser page, assets, circuit connection, navigation, and a harmless job run through native completion/history. Check anonymous rejection or explicit opt-in, viewer/operator/admin boundaries, read-only command rejection, and proxy/custom-path behavior. For remote targets, verify the named client's credentials, native history, live stream, and explicit snapshot fallback. Check desktop/mobile, light/dark, visible keyboard focus, and error states. Completion: report only checks actually run and their observed results.

## Security and operational guardrails

- Browser identity and remote service identity are distinct. Use the host's token provider/DelegatingHandler and secret manager. Credentials stay server-side; never forward browser cookies as implicit delegation.
- Long-lived circuits require the host's authentication-state revalidation for logout/role revocation. UI visibility is not authorization.
- A missing API capability must be reported, not replaced with invented history, progress, nodes, or cancellation success.
- Cancellation is cooperative and node-local in the supported API; forecasts are not guaranteed executions. Stale edits are protected within this process, not by a distributed Quartz compare-and-swap.
- Use test schedulers and harmless jobs for verification. Do not run destructive checks against production or publish/deploy without an explicit request.
- Report changed files, topology, permission boundaries, non-secret configuration keys, exact verification commands/results, and any genuinely missing prerequisite.

---
