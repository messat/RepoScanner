# Architecture Decision Records

Each file records one decision: the context that forced it, what was decided, and what follows from it.

ADR-001 to ADR-019 come from *Code Repo Analysis Dashboard, Spike Findings & ADRs v2*, which is canonical for those numbers. ADR-020 onwards were decided while building this repository. The next new ADR is **ADR-026**.

| ADR | Decision | Status |
|---|---|---|
| [ADR-001](ADR-001-container-apps-job-for-the-scanner.md) | Container Apps Job for the scanner | Accepted |
| [ADR-002](ADR-002-console-app-not-web-api-or-worker-service.md) | Console app, not Web API or Worker Service | Accepted |
| [ADR-003](ADR-003-app-service-linux-for-the-blazor-dashboard.md) | App Service Linux for the Blazor dashboard | Accepted |
| [ADR-004](ADR-004-storage-queue-deferred-then-adopted.md) | Storage queue, deferred then adopted | Accepted |
| [ADR-005](ADR-005-immutable-image-tags.md) | Immutable image tags | Accepted |
| [ADR-006](ADR-006-migrations-applied-by-the-pipeline.md) | Migrations applied by the pipeline | Accepted |
| [ADR-007](ADR-007-managed-identity-throughout.md) | Managed identity throughout | Accepted |
| [ADR-008](ADR-008-two-privilege-levels.md) | Two privilege levels | Accepted |
| [ADR-009](ADR-009-one-scanner-image.md) | One scanner image | Accepted |
| [ADR-010](ADR-010-deliberate-rejection-of-complexity.md) | Deliberate rejection of complexity | Accepted |
| [ADR-011](ADR-011-two-job-definitions-two-queues.md) | Two job definitions, two queues | Accepted, amended 2026-09-24 |
| [ADR-012](ADR-012-maxexecutions-1-raised-on-evidence.md) | maxExecutions 1, raised on evidence | Accepted |
| [ADR-013](ADR-013-delete-after-upsert-poison-queue-after-three-failures.md) | Delete after upsert, poison queue after three failures | Accepted |
| [ADR-014](ADR-014-two-runtime-identities.md) | Two runtime identities | Accepted |
| [ADR-015](ADR-015-azure-sql-basic-in-uk-west.md) | Azure SQL Basic in UK West | Accepted |
| [ADR-016](ADR-016-shallow-single-branch-clone-with-the-credential-in-a-header.md) | Shallow, single-branch clone with the credential in a header | Accepted |
| [ADR-017](ADR-017-everything-runs-locally-before-azure.md) | Everything runs locally before Azure | Accepted, amended 2026-09-24 |
| [ADR-018](ADR-018-code-pattern-rules-from-a-roslyn-syntax-walker-justified-by-analyser-ids.md) | Code-pattern rules from a Roslyn syntax walker, justified by analyser IDs | Accepted |
| [ADR-019](ADR-019-suppression-expires-by-default.md) | Suppression expires by default | Accepted |
| [ADR-020](ADR-020-branching-strategy-and-branch-policies.md) | Branching strategy and branch policies | Accepted |
| [ADR-021](ADR-021-explicit-csharp-style-and-build-conventions.md) | Explicit C# style and build conventions, enforced by the build | Accepted |
| [ADR-022](ADR-022-one-options-class-per-configuration-group.md) | One options class per configuration group, validated at start-up | Accepted |
| [ADR-023](ADR-023-secret-hash-is-an-unsalted-sha-256.md) | The secret hash is an unsalted SHA-256 | Accepted |
| [ADR-024](ADR-024-fingerprint-per-scanner-without-line-numbers.md) | A fingerprint per scanner type, without line numbers | Accepted |
| [ADR-025](ADR-025-auto-resolve-only-for-scanners-that-completed.md) | Only a scanner that completed can resolve its findings | Accepted |

## Writing a new ADR

Copy the layout of an existing file: a title `# ADR-NNN: <decision>`, the status, date and source, then `## Context`, `## Decision` and `## Consequences`. A decision is never edited to mean something else: it is amended with a dated note, or superseded by a new ADR that says which one it replaces. An ADR is added in the same pull request as the change it explains.

## Numbering note: the implementation brief

The implementation brief of 23 September numbers its 14 "do not reopen" decisions independently. This table maps them to the numbers used here.

| Brief | Decision | This repository |
|---|---|---|
| ADR-001 | Single console app, three scanner classes, Generic Host | ADR-002, ADR-009 |
| ADR-002 | Clone once per execution per repository | ADR-009; spike document, Architecture, scan scope |
| ADR-003 | Queue-triggered Container Apps Job, two lanes | ADR-004, ADR-011 |
| ADR-004 | maxExecutions 1 initially | ADR-012 |
| ADR-005 | replicaTimeout ceiling with an in-app MaxRunDuration budget | ADR-001; spike document, Spike 1 |
| ADR-006 | *(not present in the brief)* | |
| ADR-007 | Key Vault holds only the PAT; managed identity for everything else | ADR-007 |
| ADR-008 | Pipeline identity owns DDL; scanner is db_datareader and db_datawriter | ADR-006, ADR-008 |
| ADR-009 | Suppression is a nullable SuppressedUntil, 14-day default | ADR-019 |
| ADR-010 | CSharpSyntaxWalker rules with CA IDs as justification | ADR-018 |
| ADR-011 | Never store raw secret values | Spike document, Data model and Security design |
| ADR-012 | PAT is read-only scoped | Spike document, Security design, repository access |
| ADR-013 | Immutable image tags, never :latest | ADR-005 |
| ADR-014 | Multi-stage Dockerfile, dotnet/runtime final image | Spike document, Spike 1 method; no separate ADR |
