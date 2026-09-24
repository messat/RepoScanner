# ADR-004: Storage queue, deferred then adopted

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Initially rejected as premature for a single nightly loop over repositories.

## Decision

Adopted once "Scan now" required targeting one repository on demand.

## Consequences

The repository identifier travels in the message, so no per-scan reconfiguration is needed. Scheduled and manual scans share one code path. A plain Storage queue rather than Service Bus: topics and strict ordering are not required. Accepts at-least-once delivery, handled by the unique fingerprint index.
