# Security

Quartz Supervisor exposes scheduler state and administrative controls. Treat its dashboard as a privileged surface: keep the default authenticated endpoint requirement and configure authentication and authorization in the host application. Do not allow anonymous access on an untrusted network. The sample's anonymous Development mode is local-only; it is not a production security configuration.

To report a suspected vulnerability, email **hi@n8.lu** privately with a description, affected version or commit, impact, and reproduction steps when available. Do not open a public issue or publish exploit details before coordinating disclosure. Avoid sending credentials, secrets, or real scheduler/job data; redact sensitive material. The project has no stated response-time or coordinated-disclosure guarantee.
