# Contributing

## Local workflow

Requires the .NET 10 SDK. Tests use Quartz's in-memory store and ASP.NET Core's test host; no external database or credentials are needed.

```sh
dotnet restore QuartzSupervisor.slnx
dotnet format QuartzSupervisor.slnx --verify-no-changes
dotnet build QuartzSupervisor.slnx
dotnet test QuartzSupervisor.slnx
dotnet run --project Samples/Embedded/Embedded.csproj
```

The sample permits anonymous dashboard access only in Development and binds to localhost there. Production access belongs behind the host application's authentication and authorization; do not copy the sample's Development exception into a public deployment.

## Code conventions

Use the existing two-library boundary: Quartz integration and dashboard UI stay separate, with the embedded host as a sample. Keep file-scoped namespaces, nullable reference types, warnings-as-errors, central package versions, and the existing `Async` suffix on asynchronous APIs. Separate logical stages in multi-step method bodies with blank lines. C# and Razor formatting is checked with `dotnet format QuartzSupervisor.slnx --verify-no-changes`.

This is an embeddable library, not the template’s database-backed API and standalone Web application. Add layers only when a real boundary requires them.

## Issues and pull requests

For bugs, include the .NET and Quartz versions, hosting/authentication setup, concise reproduction steps, expected versus actual behavior, and relevant sanitized logs or exception text. Never attach credentials, tokens, job data, or production scheduler details. For feature requests, describe the operational need and desired observable behavior.

Keep PRs focused, explain user-visible changes and compatibility impact, and include or update tests for behavior changes. Run restore, build, and tests before submitting; mention any command that could not be run. Update README guidance when setup, compatibility, security, or supported behavior changes. Use Quartz APIs rather than reading storage tables, and preserve host-owned authentication and Quartz configuration.

For suspected vulnerabilities, follow [SECURITY.md](SECURITY.md), not a public issue.
