# ADR-030: Deploy to a dev environment through Azure Pipelines

- **Status:** Accepted
- **Date:** 2026-10-19
- **Source:** Decided between 2026-10-16 and 2026-10-19 while setting up the dev environment, with Steven Wilson's three points of 2026-10-19; the design is `docs/design/2026-10-19-build-and-deployment-pipelines.md` (Issue 12, #72, task #150)

## Context

[ADR-029](ADR-029-this-release-runs-locally-without-pipelines.md) kept the first release local, with no pipelines and no Azure resources, until the scanner, the enqueuer and the dashboard ran together locally. They now do. Deploying them is evidence the EPA can use, and Steven Wilson approved a dev environment in the Blacklight Pay-As-You-Go subscription.

## Decision

The apps are deployed to one dev environment, the resource group `RepoAnalysis-dev` in UK West. Its resources were created once, by hand, with the Azure CLI, and are listed in the pipelines design.

RepoScanner's pipeline builds and tests the scanner and the dashboard on every merge into `develop` in either repository, applies the migrations, then deploys the scanner's two jobs and, after them, the dashboard. The enqueuer has its own pipeline in its own repository. Each pipeline creates the jobs it deploys if they are missing and updates them otherwise. Pull requests into `develop` and `main` run the build and tests through a build validation policy.

The pipelines sign in as `id-pipeline`, a user-assigned managed identity, through a Workload Identity Federation service connection, with one narrow role per thing they deploy and the only schema rights in the database.

The database was built from the migrations alone, by the pipeline, in an empty database. Its users and their roles are added by hand from a script in the README, and no pipeline creates a user or grants a role.

## Consequences

ADR-029 is superseded, so the dated amendments it added to ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020 stop applying: images are tagged with the build ID, migrations are applied by the pipeline, and Workload Identity Federation is in use. The pipeline identity is a managed identity rather than an app registration, which ADR-007 allows. The dashboard's sign-in uses `id-dashboard` as a federated credential, so no client secret exists.

Shared resources are not created by a pipeline: rebuilding the environment means running the README's commands again, then the pipeline. A change to the dashboard alone redeploys the scanner too. `main` deploys nothing until a production environment exists.
