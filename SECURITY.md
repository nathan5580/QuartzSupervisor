# Security

Quartz Supervisor exposes privileged scheduler controls. Authentication is required by default. The host owns browser sign-in, authorization policies, and authentication-state revalidation for long-lived circuits. View, Operate, and Administer policies are checked server-side for each query/command using the scheduler name as the resource; administration also requires the lower permissions. ReadOnly blocks all writes on the server.

`RequireAuthentication = false` is an explicit opt-in for a deliberately trusted boundary. Without restrictive policies/read-only mode it permits anonymous administration. Never expose it to an untrusted network. Development-only sample identities are not a production sign-in mechanism.

Remote API calls use host-configured service credentials, not automatically the human operator's identity. Keep secrets in the server's secret provider and use HTTPS. Only approved job types/parameters may be created or overridden. Stored sensitive parameters and arbitrary data maps are not exposed. Execution diagnostics are withheld unless the host provides a safe redactor.

Administrative activity is bounded process memory plus host structured logs, with an optional audit sink. It is not a transactional, complete, or durable compliance record by itself. Native execution-history persistence and production schema migrations remain the scheduler host's responsibility. Revision checks do not provide distributed locking against external Quartz writers.

Report suspected vulnerabilities privately to **hi@n8.lu** with affected version/commit, impact, and reproduction steps. Redact credentials, personal information, and real job/scheduler data. Do not publish exploit details before coordinating disclosure. The project has no stated response-time or coordinated-disclosure guarantee.
