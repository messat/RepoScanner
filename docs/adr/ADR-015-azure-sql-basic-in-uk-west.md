# ADR-015: Azure SQL Basic in UK West

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

Basic is about £4 a month flat, 5 DTU, 2 GB. Serverless bills per vCore-second and auto-pauses after an hour idle, but a dashboard that is always on never lets it pause, and when it does resume takes 30 to 60 seconds with the first connection often failing, which lands on the 02:00 scanner. The subscription refused UK South at provisioning.

## Decision

Basic, UK West. Move to S0 (about £12) if 5 DTU is exhausted. Serverless rejected for resume latency.

## Consequences

The findings database holds paths, masked previews and hashes derived from client code and must stay in UK regions under Blacklight's client agreements; UK West satisfies that. The region is an environment constraint and is recorded as such, not as a design preference. A retry policy on the scanner's SQL connection is still worth having.
