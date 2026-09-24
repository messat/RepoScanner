# ADR-009: One scanner image

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Three scanners (secrets, packages, code patterns) and two trigger modes.

## Decision

One image containing three scanner classes behind a common interface, deployed to two job resources (ADR-011).

## Consequences

Identical behaviour is guaranteed across scheduled and on-demand scans, one pipeline, one clone per repository. Separate images per scanner were rejected: the logic would diverge and each would clone the same repository independently. The split into two job resources happened for isolation (ADR-011), not for volume, and cost nothing in code because the image was already designed to be deployed more than once.
