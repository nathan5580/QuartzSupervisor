# Contributing

## Local setup

Requires the .NET 10 SDK. The solution uses Quartz's in-memory store for tests; no database or credentials are needed.

```sh
dotnet restore QuartzSupervisor.slnx
dotnet build QuartzSupervisor.slnx
dotnet test QuartzSupervisor.slnx
dotnet run --project Samples/Embedded/Embedded.csproj
```

The sample dashboard allows anonymous access only in Development and binds to localhost. Keep production access behind the host's authentication and authorization.

## Changes

Keep changes focused, preserve the host application's Quartz and ASP.NET Core setup, and add tests for observable behavior. Do not read Quartz storage tables directly; use Quartz APIs. Update the README when setup, compatibility, security, or supported dashboard behavior changes.
