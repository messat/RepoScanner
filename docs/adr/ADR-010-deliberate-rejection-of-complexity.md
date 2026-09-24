# ADR-010: Deliberate rejection of complexity

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

The signed-off proposal specified a monolithic architecture; a microservices design with API Management was considered during analysis.

## Decision

Stay close to the proposal. No API Management, no Service Bus, no per-scanner services.

## Consequences

API Management earns its place with multiple external consumers, rate limiting or a public gateway - none apply to one internal caller. In a nine-week project, two of which are reserved for the report, infrastructure built speculatively would come at the cost of working features. Each rejected component has a named condition that would justify revisiting it.
