# ADR-029: This release runs locally, without CI/CD pipelines

- **Status:** Accepted
- **Date:** 2026-10-04
- **Source:** Decided on 2026-10-04 while planning Issue 11 (#66), task #120

## Context

ADR-017 has everything run locally before anything moves to Azure. The board then planned one CI/CD pipeline per repository: the scanner's (Issue 12, #72), the enqueuer's (enqueuer Issue 6, #84) and the dashboard's (dashboard Issue 3, #99). The enqueuer and the dashboard are not built yet, so a pipeline built now would build and deploy apps that have never run together, and every decision it fixed, such as image tags, job definitions and identities, would be made before the apps it serves exist.

## Decision

This release runs locally only: Azurite for the queues, SQL Server Express (`.\SQLEXPRESS`) for the database, and Docker Desktop for the scanner's container. No CI/CD pipeline is built and no Azure resource is created. The three pipeline Issues stay on the board, tagged `out-of-scope` and kept at the bottom, so they can come back if the EPA needs evidence of an automated build or deployment.

The enqueuer and the dashboard keep their own repositories, as planned. Each app keeps its own code and history. The message format is the one thing the enqueuer shares with the scanner, and the dashboard sends the same message for Scan now: both copy `docs/contracts/scan-request.example.json` for their contract tests (Issue 11, #101; enqueuer #81, dashboard #88). How each repository is set up stays with #102 and #93.

## Consequences

Nothing runs the tests on a pull request: `dotnet test RepoScanner.sln`, with its 80% coverage gate, is run by hand before a pull request is completed. Checks that were planned for a pipeline, such as Issue 11's coverage gate and mutation check, are run locally and recorded in the pull request.

The ADRs that describe Azure (ADR-001, ADR-003, ADR-009, ADR-011, ADR-014, ADR-015 and ADR-023) describe the target and are unchanged. ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020 each have a dated amendment saying what holds locally instead. Bringing the pipeline back means removing the `out-of-scope` tag from #72, #84 and #99 and planning them; those amendments then stop applying.
