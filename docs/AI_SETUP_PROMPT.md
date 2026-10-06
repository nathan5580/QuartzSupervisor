# Guided AI setup prompt

Paste the prompt below into an AI coding agent opened at the root of the ASP.NET Core application that should host Quartz Supervisor. The agent should inspect the host first, present its proposed setup, and only then edit files.

---

You are integrating Quartz Supervisor into this existing application. Make the smallest complete change that fits its current architecture. Do not replace the application, invent a new authentication system, or create a second Quartz configuration convention.

## Work in these stages

1. **Inspect before editing.** Identify the web host and target framework; existing Quartz registration and scheduler names; dashboard/API routes; authentication and authorization; configuration and secret sources; deployment boundaries; and relevant tests. Read the current Quartz Supervisor README and verify whether its NuGet package is published before choosing project references or packages.
2. **Choose the topology.** Determine whether the dashboard belongs beside an in-process scheduler, on a central host connected to one Quartz HTTP API, or on a central host connected to multiple API hosts. One Quartz HTTP API can serve multiple schedulers in its container. Register `AddQuartzHttpClient` once per remote scheduler; schedulers behind the same API URL and using the same credentials may share a named `HttpClient`. Use distinct named clients when URL or auth differs. These registrations are keyed by scheduler name: detect duplicate names across independent API targets, explain the collision, and do not claim it provides failover or clustering. Preserve the exact scheduler identities expected by each API.
3. **Confirm only material unknowns.** Before editing, give the user a short proposed topology and ask one grouped set of questions only for missing decisions: which host owns the dashboard; local versus remote schedulers and their non-secret URLs/names; the dashboard route; the existing human sign-in scheme/policy; and where API credentials should be obtained. Never ask for credential values. If repository configuration answers these questions, proceed without asking.
4. **Implement in the existing host.** Reuse its dependency injection, configuration providers, route conventions, authentication, and deployment model. Keep Quartz storage and job registration in the scheduler-owning API. Keep the dashboard as an operations UI, not a proxy for arbitrary URLs or a second scheduler store.
5. **Verify and report.** Build and run relevant tests. Exercise the dashboard route with and without an authenticated identity, confirm each configured scheduler appears under the expected name, and verify that a failed or unauthorized remote target does not make healthy targets unusable. Report exact changed files, topology, auth boundaries, configuration keys (names only), and commands/results.

## Supported integration contract

- The UI host registers `AddQuartzSupervisorDashboard()` and maps `MapQuartzSupervisorDashboard()`. Dashboard access requires authorization by default. The host owns its authentication scheme and sign-in flow; the package does not add a login page, authentication handler, identity store, or credential vault.
- Configure browser access with the host's existing interactive sign-in mechanism (commonly its cookie/OIDC setup) and the narrowest suitable authorization policy. Preserve `UseAuthentication()` and `UseAuthorization()` ordering. Do not make a production dashboard anonymous.
- A scheduler-owning API registers Quartz and `AddQuartzHttpApi()`. One API path serves its registered schedulers. Protect the route with the API host's authorization policy; if access must differ per scheduler, use `QuartzHttpApiOptions.SchedulerAuthorizationPolicy` with resource-aware authorization for `SchedulerResource`.
- A central host registers `AddQuartzHttpClient(schedulerName, namedHttpClient)` once per remote scheduler, after the required Quartz registration described in the current README. Reuse a named client when multiple schedulers share the same API address and credentials; use distinct clients when they differ. Keep each scheduler name and credential source isolated.
- The dashboard authenticates the human operator; remote API requests use the central host's service identity unless explicit token delegation is implemented and verified. Never assume the API receives the human user's claims, and never forward browser cookies as a shortcut.
- For OAuth/client-credentials or rotating tokens, use the application's existing token provider through a `DelegatingHandler`; do not paste long-lived tokens into source or commit them in settings. Load secrets from the configured secret manager/environment. Do not put service credentials in browser JavaScript, URLs, screenshots, logs, or this prompt.
- Preserve the documented limitation: listener-based live execution history is local to the dashboard process; remote scheduler executions are not streamed into the timeline.
- Keep `UseAntiforgery()`, `MapStaticAssets()`, and the host's existing middleware/routes when required by its .NET version and hosting model. Do not copy Development-only anonymous sample settings into production.

## Guardrails

- Check for duplicate remote scheduler names before wiring multiple API targets. Do not silently rename a proxy; if independent schedulers collide, explain that their owning API registrations need distinct names and ask before changing them.
- Do not guess whether an endpoint is public, a scheduler name, a credential mechanism, or a production secret source. Ask when the repository cannot establish it.
- Do not weaken authentication to make a smoke test pass. Use a test identity or test-only auth handler instead.
- Do not run Quartz Supervisor against production schedulers to verify setup. Use a test host or non-production scheduler and avoid destructive job/trigger actions.
- Do not claim success for an unexercised path. If a check cannot run, state the missing prerequisite and the exact command not run.

---

Reference: [Quartz Supervisor setup and security documentation](../README.md).