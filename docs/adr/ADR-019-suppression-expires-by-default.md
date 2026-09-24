# ADR-019: Suppression expires by default

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

A false positive dismissed once should not stay silent forever; a second developer may see something the first did not. Deleting suppressed findings would destroy the audit trail.

## Decision

Suppression is state on the Finding row: Status = Suppressed, SuppressedUntilUtc defaulting to 14 days ahead and configurable in the dashboard, SuppressedBy and a mandatory reason. A null SuppressedUntilUtc on a suppressed row is permanent and still needs the reason. The dashboard sets it; the scanner respects it.

## Consequences

On expiry the finding returns to Open and a SuppressionExpired event is written; the dashboard's open query is "not suppressed, or suppression expired". Nothing is deleted. Who may suppress, and whether permanent suppression needs a second approver, are still open.
