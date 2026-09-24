# ADR-003: App Service Linux for the Blazor dashboard

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Blazor Server holds a persistent SignalR circuit per user. Scaling to zero, or scaling out without sticky sessions, drops circuits mid-interaction.

## Decision

App Service Linux B1. Container Apps with one minimum replica remains viable and was costed.

## Consequences

Costs are comparable, so this was decided on operational simplicity rather than price: no Dockerfile and no registry step for the dashboard, zip deploy, one-toggle WebSockets and Easy Auth for Entra. Three App Service settings are mandatory for the SignalR circuit to survive: WebSockets on (otherwise SignalR falls back to long polling), Always On (otherwise the app unloads after 20 idle minutes and every circuit dies) and ARR affinity on (sticky sessions if the plan is ever scaled out). Accepts two deployment mechanisms instead of one, and the honest counter-argument that everything else in the system is a container. Would revisit if the private-networking work makes a single Container Apps environment materially simpler.
