# ADR-002: Console app, not Web API or Worker Service

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

A job's container must exit for the execution to be marked Succeeded.

## Decision

Console app using Generic Host, so dependency injection, configuration and logging are available without Kestrel.

## Consequences

A Web API would start Kestrel, never exit, be killed at the timeout and marked Failed on every run. A Worker Service has the same trap unless StopApplication() is called explicitly. All SOLID and DI evidence is preserved; host.Run() is simply never called.
