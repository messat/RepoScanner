# Build and deployment pipeline for the scanner and the dashboard (design)

- **Status:** Approved by Muhammad, 2026-10-19. Replaces the parked draft of the same date, which planned one pipeline per repository
- **Date:** 2026-10-19
- **Work items:** scanner Issue 12 (#72, task #73) and dashboard Issue 7 (#99, tasks #97 and #98)
- **Branches:** `feature/72-build-and-deployment-pipeline` in RepoScanner for this document, the pipeline, its scripts and ADR-030; `feature/99-build-and-deployment-pipeline` in Dashboard for the DASH-ADR-001 amendment and its README

On 2026-10-19 Steven Wilson, Muhammad's tutor, asked for three things, which this design follows:

1. One pipeline that deploys the scanner first and the dashboard second.
2. The migrations applied by that pipeline, so the `RepoScanner` database built by hand during set-up could be dropped and built again from the migrations alone.
3. The database users and their roles applied by hand, not by the pipeline.

The enqueuer already has its own pipeline, built first (Enqueuer Issue 6, `docs/design/2026-10-19-scheduled-job-and-deployment.md` in the Enqueuer repository), and keeps it. Decisions marked **(Steven Wilson)** are the tutor's, those marked **(Muhammad)** are Muhammad's, made while the dev environment was set up between 2026-10-16 and 2026-10-19; every other decision is the designer's call and is open to change in review.

## Purpose

The scanner and the dashboard have only ever run on the developer's machine (ADR-029). This work deploys both to the dev environment in Azure and keeps them deployed: one pipeline, in RepoScanner, builds and tests both apps whenever a change is merged into `develop` in either repository, then deploys the scanner and, after it, the dashboard, so nothing that changes from one release to the next is done by hand.

What happens when a change is merged into RepoScanner's `develop`:

1. RepoScanner's pipeline starts. It checks out RepoScanner and the Dashboard repository's `develop`.
2. **Build:** both apps are restored, built and tested on a Microsoft-hosted Linux agent, each failing the run if its line coverage drops below 80%, the same gate as `dotnet test` locally. The scanner's job also writes the migration script; the dashboard's publishes the app and zips it. If either fails, the run stops before anything reaches Azure.
3. **Image:** the scanner's Dockerfile is built, tagged `reposcanner:<build ID>` and pushed to `enqreposcannerdev`.
4. **Migrate:** the migration script is applied to Azure SQL as `id-pipeline`. If it fails, the run stops and nothing is deployed.
5. **Deploy the scanner:** the pipeline looks for `job-scanner-nightly-dev` and `job-scanner-ondemand-dev`. Each one missing is created with every setting; each one there is pointed at the new image and has every setting put back as the script says.
6. **Deploy the dashboard:** the pipeline checks Easy Auth is on, puts the web app's settings back as the script says, deploys the zip and starts the app if it is stopped.
7. The run's summary shows both test runs, their coverage and two deployments to `reposcanner-dev`.

A merge into the Dashboard repository's `develop` starts the same pipeline and runs all of it, scanner included, using RepoScanner's `develop`. A pull request into `develop` or `main` in either repository runs the Build stage only.

Rolling back is running the deploy stages of an earlier run again: each run deploys its own image tag and zip. The schema is not rolled back, which expand/contract allows for (ADR-006).

## The dev environment

Created by hand from PowerShell with the Azure CLI, and checked in the Azure portal, between 2026-10-16 and 2026-10-19 **(Muhammad)**. The pipeline never creates these; it deploys into them.

| What | Name | Notes |
|---|---|---|
| Subscription | Pay-As-You-Go, Blacklight tenant | Approved by Steven Wilson |
| Resource group | `RepoAnalysis-dev`, UK West | UK West for the reason in ADR-015 |
| Container registry | `enqreposcannerdev`, Basic | Admin user off. Holds the scanner's and the enqueuer's images |
| Storage | `strreposcannerdev`, with `scan-nightly`, `scan-ondemand`, `scan-nightly-poison`, `scan-ondemand-poison` | Account keys still on, to be turned off once every app is proven on its identity |
| Key Vault | `kv-reposcanner-dev`, RBAC | One secret, `azure-devops-pat`: Code (Read) on BlacklightInternalsMo **(Muhammad)** |
| Azure SQL | server `sql-reposcanner-dev`, database `RepoScanner`, Basic, 2 GB, local backups | Entra sign-in only, Muhammad as the server's only admin. "Allow Azure services" firewall rule. The database was dropped and created again empty on 2026-10-19, and its three users added by hand (decisions 3 and 4) |
| Log Analytics | `log-reposcanner-dev` | |
| Container Apps environment | `code-reposcanner-dev` | Runs `job-enqueuer-dev`. The scanner's two jobs are created by this pipeline. Its logs go to Azure Monitor, and the diagnostic setting `send-to-log-reposcanner-dev` sends console and system logs to `log-reposcanner-dev`, so each line lands in `ContainerAppConsoleLogs`, the table the README's searches and the alerts read (Issue 10 design, decision 13). Checked on 2026-10-19 |
| App Service | plan `dashboard-plan-dev` (Linux B1), web app `app-reposcanner-dashboard-dev` | .NET 10, `id-dashboard` attached, three app settings. Stopped, and Easy Auth not yet on (decision 16) |
| Runtime identities | `id-scanner`, `id-dashboard` | Azure roles as ADR-014, assigned by hand on 2026-10-17 |
| Pipeline identity | `id-pipeline` | Decision 19 |
| Service connection | `sc-reposcanner-dev` in Lumina | Workload Identity Federation as `id-pipeline`, so Azure DevOps holds no secret (ADR-007). Already used by the enqueuer's pipeline |
| Deployment environment | `reposcanner-dev` in Lumina | Already lists the enqueuer's deployments |

### Database users

Added by hand on 2026-10-19 by Muhammad, signed in as the server's Entra admin, in the new empty database **(Steven Wilson)**. Each user is created by its identity's object ID (`WITH OBJECT_ID`), because a spike-era `id-scanner` shares the name in Entra ID. The script goes into the README, so rebuilding the database is copy and paste.

| User | Object ID | Roles and permissions | Why |
|---|---|---|---|
| `id-scanner` | `fce27e24-9ffa-40e2-a01e-06415f765586` | db_datareader, db_datawriter | ADR-008: reads and writes data, never the schema |
| `id-dashboard` | `8c4068b3-51a9-4c54-b95f-356b9ef4a894` | db_datareader, db_datawriter | The same. It writes for triage |
| `id-pipeline` | `f7ab4ce3-509a-471d-8233-79987139e26e` | db_ddladmin, db_datareader, db_datawriter, REFERENCES on `dbo` | Creates and changes tables, writes `__EFMigrationsHistory`; REFERENCES lets it create foreign keys |

The old database's permissions, read on 2026-10-19 before it was dropped, were CONNECT for all three users and REFERENCES on `dbo` for `id-pipeline`, as above. Its role memberships were not read before the drop; the check in Testing reads the new database's after the first run.

## Success criteria

From the two Issues and Steven Wilson's three points:

1. **(Steven Wilson)** One run deploys the scanner and then the dashboard.
2. **(Steven Wilson)** The first run on `develop` builds every table in the empty `RepoScanner` database from the migrations alone.
3. **(Steven Wilson)** The pipeline never creates a database user or grants a role: the users and roles after a run are those in the README.
4. **#72** The Migrate stage runs before both deploy stages, and a failed migration stops the deployment.
5. **#72** Azure SQL schema changes are made only by the pipeline identity.
6. **#72** `dotnet test` runs on every pull request into `develop` and `main`, and the build fails when line coverage on Core or Scanner drops below 80%.
7. **#72** Deliberately breaking a rule fails CI.
8. **#99** One pipeline run builds, tests and deploys the dashboard with no step done by hand.
9. **#99** Running the pipeline twice with no change leaves App Service and the role assignments as they were.
10. **#99** The deployed app answers its health page after each deployment, and a failed check fails the pipeline. **Not met by this work:** the health page does not exist yet (decision 17).
11. **#99** The dashboard's part of the pipeline never changes the schema.
12. **#99** WebSockets, Always On and ARR affinity are on, and a user not signed in to Entra cannot see any page.
13. **#99** `id-dashboard` holds no Key Vault role and can send only to `scan-ondemand`.
14. **Both** No password, connection secret or PAT is stored in Azure DevOps.

## Decisions

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Where it deploys | One dev environment, `RepoAnalysis-dev` **(Muhammad)**. `main` deploys nothing until there is a production environment | Proves the deployment end to end at the cost of one environment, about £20 a month |
| 2 | Who creates the shared resources | Done by hand, once, with the Azure CLI **(Muhammad)**, and recorded in ADR-030 and the README. The pipeline deploys into them and never creates them | What changes from one release to the next (schema, image, jobs, the dashboard's code and settings) is changed only by the pipeline; what is created once is not |
| 3 | The database | Dropped and created again empty by hand on 2026-10-19, Basic with local backups as before **(Steven Wilson, Muhammad)**. The pipeline's first Migrate builds every table | Proves in Azure what the README already insists on locally: a fresh database can always be built from the migrations alone. The schema applied by hand during set-up is gone, so nothing in Azure was made outside a migration |
| 4 | Database users and roles | Added by hand, once per database, from the script in the README **(Steven Wilson)**. No pipeline creates a user or grants a role | The pipeline signs in as `id-pipeline`, which a new database does not yet have, so only the server's admin can add the first user. For `id-pipeline` to add the other two it would need rights close to db_owner, and the server would need its own identity with permission to read Entra ID, which only a Blacklight tenant administrator can grant; the server has none. ADR-008: the pipeline changes the schema, never who has access |
| 5 | How many pipelines | One, `azure-pipelines.yml` at RepoScanner's root, for the scanner and the dashboard **(Steven Wilson)**. The enqueuer keeps its own. The Dashboard repository has no pipeline file | One run applies the migration, then deploys the scanner, then the dashboard, which is the order decision 23 needs. The dashboard stops at start-up if the newest migration is missing (DASH-ADR-001), so it is deployed after Migrate in the same run |
| 6 | Where the dashboard's code comes from | A repository resource, `dashboard`: `Lumina/Dashboard` at `refs/heads/develop`, with a trigger on `develop` and the same path exclusions as RepoScanner's | A merge into either repository starts the run. When the Dashboard repository starts it, Azure Pipelines uses the YAML and code from RepoScanner's `develop` and the Dashboard commit that started it |
| 7 | Agent | Microsoft-hosted `ubuntu-latest` for every stage | The image is built on Linux, and the tests then run on the same OS as the container. The free tier is one hosted parallel job and 1,800 minutes a month, shared with the enqueuer's pipeline. Neither app's tests have run on Linux yet, so any that assume Windows are fixed when the first run finds them |
| 8 | When it runs | On every merge into `develop` in either repository, except changes only to `docs` or `*.md` files. On every pull request into `develop` or `main` in either repository, through a build validation policy in each repository pointing at this pipeline, the Build stage only | Documentation needs no deployment. The policies are the ones ADR-020 waits for. Azure Repos runs a pull request in a repository resource through that repository's own branch policy, on the pull request's merge commit |
| 9 | Stages | Build (two jobs, Scanner then Dashboard), Image, Migrate, Deploy scanner, Deploy dashboard, each depending on the one before. Every stage after Build runs only for `develop` and never for a pull request | Both apps are built and tested before anything reaches Azure, so a broken dashboard never leaves half a release. The deployments run in Steven Wilson's order, and a pull request's check is one stage |
| 10 | Build | Scanner: `dotnet restore`, `dotnet build --configuration Release`, `dotnet test --configuration Release`, then `dotnet tool restore` and `dotnet ef migrations script --idempotent`, the script published as a pipeline artifact. Dashboard: the same three commands, then `dotnet publish src/Dashboard` zipped and published as an artifact. Both test runs and their coverage are published to the run | The coverage gates are already in each repository's `Directory.Build.props`, so CI runs the same commands as the developer. `ScannerDbContextFactory` needs `Scanner__Database__ConnectionString` to be set, so the step sets it to the Azure SQL string with `Authentication=Active Directory Default`; writing the script never connects. The script and the zip are artifacts, so what was applied or deployed can be read afterwards |
| 11 | Image | `az acr login`, `docker build` with RepoScanner's Dockerfile, `docker push enqreposcannerdev.azurecr.io/reposcanner:$(Build.BuildId)`, in an Azure CLI task signed in through `sc-reposcanner-dev`. No `latest` tag | ADR-005. As in the enqueuer's pipeline, one service connection does everything |
| 12 | Migrate (#73) | The Build stage's script, applied by `id-pipeline` through the service connection. Both deploy stages depend on it succeeding | ADR-006 and ADR-008. The script is idempotent: the first run builds every table in the empty database, and a later run applies only what is new. How it runs is the builder's (see Left to the builder), with no password anywhere |
| 13 | Deploying a scanner job | `deploy/Deploy-ScannerJob.ps1 -Lane nightly`, then `-Lane ondemand`, run by an Azure CLI task, following the enqueuer's `Deploy-EnqueuerJob.ps1`: it checks the image is already in the registry, then looks for the job; only ARM's ResourceNotFound counts as missing; a missing job is created with every setting, and one that exists has its identity, registry and Key Vault secret reference set again and its image, environment variables, resources, timeout, retries and scale settings updated | Muhammad's call that the pipeline creates its jobs if they are missing (2026-10-18). The enqueuer's script has already been proved on `job-enqueuer-dev`, including run 20261019.5's lesson about a Deploy stage asking for an image its run never built. Every setting is in the script, so running it twice changes nothing but the image tag |
| 14 | Job settings | As in Job settings below | ADR-001, ADR-011, ADR-012, ADR-013, ADR-014 |
| 15 | Deploying the dashboard | `deploy/Deploy-Dashboard.ps1`, run by an Azure CLI task: it checks Easy Auth is on and stops if not, puts back every setting in Dashboard settings below, deploys the zip with `az webapp deploy --type zip`, and starts the app if it is stopped | The same way as the jobs: Azure CLI in PowerShell, every setting in git, a setting changed in the portal put back by the next run (#97). The parked draft used the Azure Web App task; one way of deploying is easier to read and to run with `-WhatIf`. The app is stopped today, so the run starts it |
| 16 | Easy Auth | Turned on once by hand in the portal before the first run on `develop`: the Microsoft provider with a new app registration, requiring sign-in and signing in anyone in the Blacklight tenant. Then moved off the client secret the portal creates: `id-dashboard` is added to the app registration as a federated identity credential, the app setting `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` is set to `id-dashboard`'s client ID, the provider's client secret setting name is pointed at it, and once sign-in works the secret and its app setting are deleted. The deploy script checks Easy Auth is on, requires sign-in and uses that setting, and fails if not | Turning it on creates an app registration, which is beyond `id-pipeline`'s rights. A client secret would break ADR-007 and stop sign-in when it expired; `id-dashboard` is attached only to this web app, as Microsoft's guidance for this asks. Failing the deployment means the dashboard's code never goes live unprotected. The whole tenant is the designer's call for dev, to revisit before production (#97's open question) |
| 17 | The health page | The post-deploy check is added when the health page is built, after this work **(Muhammad, 2026-10-17)** | Without a page that Easy Auth leaves open, a check sees Easy Auth's redirect even when the app has failed to start. Success criterion 10 is not met until then |
| 18 | Azure role assignments for the runtime identities | Stay as they are, made by hand on 2026-10-17. No pipeline assigns roles | `id-pipeline` cannot grant roles, and should not. #98's roles are in place; success criteria 9 and 13 hold because nothing changes them |
| 19 | The pipeline's identity | `id-pipeline`, a user-assigned managed identity behind `sc-reposcanner-dev`. Azure roles, read on 2026-10-19: Reader and Container Apps Jobs Contributor on `RepoAnalysis-dev`, AcrPush on the registry, Managed Identity Operator on `id-scanner`, Website Contributor on the web app. Database rights as in Database users. Nothing new is needed | Each role covers one thing the pipeline does, and none is a privileged administrator role. It is not the SQL server's admin. ADR-008: only this identity can change the schema |
| 20 | Names and settings | In the pipeline's `variables:` block and the two scripts, in git. No variable groups and no secret variables | Every change is reviewed in a pull request, and nothing secret is needed: the PAT stays in Key Vault, and every sign-in is an identity |
| 21 | Deployment history | Both deploy stages are deployment jobs to the `reposcanner-dev` environment, with no approval check | The environment lists what was deployed when, and by which run, for all three apps. An approval can be added before a production environment exists |
| 22 | Alerts (#72) | Two Log Analytics alert rules, using the README's two searches: a poisoned message and a rejected PAT. Checked every 15 minutes, emailing Muhammad through one action group. Created by `deploy/Set-AlertRules.ps1` from the pipeline once its first deployment works, after Monitoring Contributor is added to `id-pipeline` by hand | Agreed 2026-10-02 that the pipeline creates them. The rejected PAT alert covers the enqueuer too, since both log the same first words |
| 23 | Order when the schema or the message format changes | Within one run: Migrate, then the scanner, then the dashboard. Across repositories: the RepoScanner change is merged first, then the Dashboard's update to its copy of the model; the enqueuer's change to the message format after the scanner's | Expand/contract keeps the older apps working in between (ADR-006). Between the two merges the dashboard stops at start-up, saying its copy needs updating (DASH-ADR-001) |
| 24 | `scripts/test-container.ps1` | Not run in the pipeline | It needs Azurite and SQL Server on the machine. The first deployment and the first scan in Azure check the image instead |
| 25 | Minutes | Estimated: 22 per merge (Build 10, Image 4, Migrate 2, each deploy 3), 10 per pull request. NuGet packages cached between runs if it saves time | About 1,080 minutes a month for 30 merges and 30 pull requests, with the enqueuer's at about 120, within the free 1,800. Measured on the first runs and recorded in the README |
| 26 | Fixes found earlier | The scanner's `ServiceUri` fix (#72's note), the dashboard's triage actor and the health page come after this work **(Muhammad)** | The scanner works with a correct URL; the bug only shows with a wrong one. Until the triage fix, the triage buttons are not used in Azure, because they would save App Service's process account as the person |
| 27 | New ADRs | ADR-030 replaces ADR-029 (below). A dated amendment to DASH-ADR-001, whose "Its pipeline comes later" says it will get one | ADR README rules: a decision is superseded or amended, not edited |
| 28 | Where this document lives | RepoScanner's `docs/design/`. The Dashboard README links to it | One pipeline, one design. RepoScanner's ADRs already describe the whole system |

## Job settings

Both jobs run in `code-reposcanner-dev` as `id-scanner` (ADR-014), pull their image from `enqreposcannerdev` as `id-scanner`, and read the PAT as the Key Vault reference `azure-devops-pat`, which names the secret without a version so a renewed PAT is picked up without a deployment.

| Setting | `job-scanner-nightly-dev` | `job-scanner-ondemand-dev` |
|---|---|---|
| Name | The environment in the name, as `job-enqueuer-dev` **(Muhammad, 2026-10-19)** | the same |
| Image | `enqreposcannerdev.azurecr.io/reposcanner:<build ID>` | the same |
| Trigger | Event: an `azure-queue` scale rule on `scan-nightly` in `strreposcannerdev`, queue length 1, authenticated as `id-scanner` | the same on `scan-ondemand` |
| Executions | min 0, max 1 (ADR-012), polled every 30 seconds, parallelism 1 | the same |
| Replica timeout | 3,600 seconds: the 60-minute limit `Scanner:Execution:MaxRunDurationMinutes` is set under | the same |
| Retries | 0: a failed message is retried by the queue, then poisoned (ADR-013) | the same |
| CPU and memory | 1 vCPU, 2 GiB | the same |

Environment variables for both, `<lane>` being `nightly` or `ondemand`:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `id-scanner`'s client ID, so `DefaultAzureCredential` uses it |
| `Scanner__Queue__Name` | `scan-<lane>` |
| `Scanner__Queue__ServiceUri` | `https://strreposcannerdev.queue.core.windows.net` |
| `Scanner__AzureDevOps__OrganizationUrl` | `https://dev.azure.com/BlacklightInternalsMo` **(Muhammad)** |
| `Scanner__AzureDevOps__Pat` | `secretref:azure-devops-pat` |
| `Scanner__Database__ConnectionString` | `Server=tcp:sql-reposcanner-dev.database.windows.net,1433;Database=RepoScanner;Authentication=Active Directory Managed Identity;User Id=<id-scanner's client ID>;Encrypt=True` |

Every other setting keeps its Production default; `DOTNET_ENVIRONMENT` is not set.

## Dashboard settings

What `deploy/Deploy-Dashboard.ps1` puts back on `app-reposcanner-dashboard-dev` on every run (#97):

| Setting | Value | Why |
|---|---|---|
| WebSockets | On | Blazor Server's connection (ADR-003) |
| Always On | On | So the first page after a quiet spell does not wait for a cold start |
| ARR affinity | On | A circuit stays on one instance |
| HTTPS only, FTPS, minimum TLS | On, disabled, 1.2 | |
| Startup command | `dotnet Dashboard.dll` | |
| State | Running | It is stopped today |
| `AZURE_CLIENT_ID` | `id-dashboard`'s client ID | So `DefaultAzureCredential` uses it |
| `Dashboard__Database__ConnectionString` | `Server=tcp:sql-reposcanner-dev.database.windows.net,1433;Database=RepoScanner;Authentication=Active Directory Managed Identity;User Id=<id-dashboard's client ID>;Encrypt=True` | |
| `Dashboard__Queue__ServiceUri` | `https://strreposcannerdev.queue.core.windows.net` | `Dashboard:Queue:Name` is already `scan-ondemand` in `appsettings.json` |

The identity, `id-dashboard`, was attached by hand and is not changed by the script. The script sets the app settings above and deletes none: Easy Auth's set-up adds its own, such as `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID`, which belong to that by-hand step (decision 16) and are recorded with it in the README; the script checks that one is there rather than setting it. Every other setting keeps its Production default.

## The pipeline, in outline

A sketch for the discussion, not the file:

```yaml
trigger:
  branches: { include: [develop] }
  paths: { exclude: [docs, '**/*.md'] }

resources:
  repositories:
    - repository: dashboard
      type: git
      name: Lumina/Dashboard
      ref: refs/heads/develop
      trigger:
        branches: { include: [develop] }
        paths: { exclude: [docs, '**/*.md'] }

pool: { vmImage: ubuntu-latest }

variables:
  serviceConnection: sc-reposcanner-dev
  registry: enqreposcannerdev
  image: reposcanner

stages:
- stage: Build             # job Scanner: checkout self; restore, build, test with the gate; migrate.sql as an artifact
                           # job Dashboard: checkout dashboard; restore, build, test with the gate; the zip as an artifact
- stage: Image             # develop only, never a pull request: az acr login, docker build, docker push reposcanner:$(Build.BuildId)
- stage: Migrate           # the same condition: apply migrate.sql to RepoScanner as id-pipeline
- stage: DeployScanner     # deployment job to reposcanner-dev: deploy/Deploy-ScannerJob.ps1 -Lane nightly, then -Lane ondemand
- stage: DeployDashboard   # deployment job to reposcanner-dev: deploy/Deploy-Dashboard.ps1 with the zip
```

## Project layout

RepoScanner:

```
azure-pipelines.yml                      the pipeline
deploy/Deploy-ScannerJob.ps1             create or update one scanner job
deploy/Deploy-Dashboard.ps1              check Easy Auth, put the settings back, deploy the zip, start the app
deploy/Set-AlertRules.ps1                the two alert rules and their action group (decision 22)
docs/adr/ADR-030-...md                   new
docs/adr/ADR-029-...md                   status: superseded by ADR-030
docs/adr/README.md                       ADR-030 added, ADR-029 marked superseded
docs/design/2026-10-19-build-and-deployment-pipelines.md   this document
README.md                                Deployment; Who applies migrations; Database users; Alerts
```

Dashboard: the DASH-ADR-001 amendment, `docs/adr/README.md`, and the README's Build and test and a Deployment section pointing here.

No application code changes in either repository.

## ADR-030

The text to add as `docs/adr/ADR-030-deploy-to-a-dev-environment-through-azure-pipelines.md`:

- **Status:** Accepted
- **Date:** the day this document is approved
- **Source:** decided between 2026-10-16 and 2026-10-19 while setting up the dev environment, with Steven Wilson's three points of 2026-10-19; this document

**Context.** ADR-029 kept the first release local, with no pipelines and no Azure resources, until the scanner, the enqueuer and the dashboard ran together locally. They now do. Deploying them is evidence the EPA can use, and Steven Wilson approved a dev environment in the Blacklight Pay-As-You-Go subscription.

**Decision.** The apps are deployed to one dev environment, the resource group `RepoAnalysis-dev` in UK West. Its resources were created once, by hand, with the Azure CLI, and are listed in the pipelines design. RepoScanner's pipeline builds and tests the scanner and the dashboard on every merge into `develop` in either repository, applies the migrations, then deploys the scanner's two jobs and, after them, the dashboard. The enqueuer has its own pipeline in its own repository. Each pipeline creates the jobs it deploys if they are missing and updates them otherwise. Pull requests into `develop` and `main` run the build and tests through a build validation policy. The pipelines sign in as `id-pipeline`, a user-assigned managed identity, through a Workload Identity Federation service connection, with one narrow role per thing they deploy and the only schema rights in the database. The database was built from the migrations alone, by the pipeline, in an empty database; its users and their roles are added by hand from a script in the README, and no pipeline creates a user or grants a role.

**Consequences.** ADR-029 is superseded, so the dated amendments it added to ADR-005, ADR-006, ADR-007, ADR-008 and ADR-020 stop applying: images are tagged with the build ID, migrations are applied by the pipeline, and Workload Identity Federation is in use. The pipeline identity is a managed identity rather than an app registration, which ADR-007 allows. Shared resources are not created by a pipeline: rebuilding the environment means running the README's commands again, then the pipeline. The dashboard's sign-in uses `id-dashboard` as a federated credential, so no client secret exists. A change to the dashboard alone redeploys the scanner too. `main` deploys nothing until a production environment exists.

## Left to the builder

- **How Migrate runs the script.** `sqlcmd` reads the `GO` separators in the idempotent script. Whether `ubuntu-latest`'s `sqlcmd` signs in through the Azure CLI's session, or the step uses a token from `az account get-access-token --resource https://database.windows.net/`, is settled on the first run. It must not use a password or a SQL login.
- **Checkout folders.** A job with one checkout puts that repository at the sources folder's root, and a job with two puts each in a folder named after it. Each Build job checks out one repository; the deploy jobs check out RepoScanner for the scripts.
- **Whether the web app must be started before the zip is deployed,** if a stopped app's deployment endpoint refuses it.
- **Path filters on the Dashboard trigger,** if the repository resource does not support them as the `self` trigger does.
- **The exact task versions,** the `containerapp` extension being added or upgraded by the script, as in the enqueuer's.
- **Caching NuGet packages** between runs, if it saves minutes.
- **Test results and coverage formats,** as long as both apps' results and coverage appear on the run's summary.

## Testing

No application code changes, so `dotnet test` is unchanged in both repositories. The scripts are checked locally with `-WhatIf`; the pipeline is checked by running it:

| Check | How |
|---|---|
| Both apps build and pass on Linux | The first pull request run of this branch. Any test that assumes Windows is fixed in its own commit, in its own repository |
| Coverage gate (criterion 6) | A pull request that drops coverage below 80% fails; shown once and abandoned |
| Breaking a rule fails CI (criterion 7) | A pull request that breaks SEC003, as in Issue 11's mutation check, fails; shown once and abandoned |
| A Dashboard pull request | Runs this pipeline's Build stage on the pull request's merge commit, and nothing deploys |
| The empty database built from the migrations (criterion 2) | After the first run on `develop`, the database has every table and `__EFMigrationsHistory` holds `20260924190222_InitialCreate`. A second run's Migrate changes nothing |
| Migrate first, and stops a failed deployment (criterion 4) | The run's stage graph shows Migrate before both deploy stages, which depend on it succeeding. Not shown with a failing migration, which would mean breaking the only database |
| The users untouched (criterion 3) | After the first run, the users-and-roles query in the README lists the same three users and roles as before it |
| Jobs created, then updated | The first run creates both scanner jobs; a second run with no change leaves `az containerapp job show` the same apart from the image tag |
| A scan in Azure | One message on `scan-ondemand` for `victim-repo`: a `ScanRuns` row Completed, findings saved, and the scanner's lines in Log Analytics under one `CorrelationId` |
| The 16 KB log line (#72's note) | One execution of `job-scanner-ondemand-dev` started by hand with `az containerapp job start`, its command overridden to print one 20 KB JSON line with the image's own shell, so no application code changes. It must be one row in `ContainerAppConsoleLogs` that `parse_json` reads. If it is split, the fix to the scanner's logging is a new Issue, the split is added to Known limitations, and the alerts step goes ahead |
| The nightly lane (#119) | The messages waiting on `scan-nightly` are scanned, and the next 02:00 UTC run fills and empties it; one Log Analytics query returns the enqueuer's line and the scanner's lines for one correlation ID |
| The dashboard (criteria 8, 9, 11, 12) | It starts, logging the scan database at migration `20260924190222_InitialCreate`; signed in through Easy Auth, its pages show the scan from above; a signed-out browser sees only the sign-in page; Scan now puts a message on `scan-ondemand` and the on-demand job scans it; a second run leaves its settings the same |
| A Dashboard merge | A merge into Dashboard's `develop` starts a run, which deploys that commit |
| No secrets (criterion 14) | No secret variable, variable group or secure file exists in Lumina |
| Alerts | A poisoned message sends the email |

## Order of work

One step at a time, stopping after each for review. The board changes need Muhammad's go-ahead.

1. **This document**, reviewed and approved. **Done on 2026-10-19.**
2. **The database:** dropped, created again empty and its three users added by hand. **Done on 2026-10-19 (Muhammad).**
3. **The board:** `out-of-scope` removed from #72 and #99 and their tasks, and their tasks added from this order of work.
4. **ADR-030**, ADR-029 marked superseded, the ADR README.
5. **The pipeline's Build stage:** registered in Lumina as `RepoScanner`, run on this branch's pull request, Linux test failures fixed.
6. **Easy Auth** turned on by hand and moved onto `id-dashboard` with no secret (decision 16), and who may sign in confirmed.
7. **Image, Migrate and both deploy stages,** with the two scripts checked with `-WhatIf`. Merged into `develop`; permission to use `sc-reposcanner-dev`, `reposcanner-dev` and the Dashboard repository given on the first run; the tables built; both jobs created; the dashboard deployed; a scan in Azure; the 16 KB check.
8. **Build validation policies** on `develop` and `main` in RepoScanner and Dashboard, both pointing at this pipeline, and the ADR-020 note. The DASH-ADR-001 amendment on `feature/99-build-and-deployment-pipeline`.
9. **Alerts:** Monitoring Contributor for `id-pipeline` by hand, then the alert rules from the pipeline.
10. **The README sections** in both repositories, with the database users script and the measured minutes.

Then, outside this work: the health page and its post-deploy check, the triage actor, the scanner's `ServiceUri` fix, and turning the storage account's keys off.

## Not in this work

| Item | Where it is |
|---|---|
| The enqueuer's pipeline | Done in Enqueuer Issue 6 (#84) |
| Creating database users or granting roles from a pipeline | Not planned (decision 4) |
| Creating or changing the shared resources from a pipeline | Not planned (decision 2) |
| A production environment, and approvals before it | Not on the board |
| Private networking | Not on the board (ADR-003, ADR-010) |
| Scanning the images for vulnerable packages | Not on the board; noted in Issue 9's design |
| The health page | After this work (decision 17) |
| Keeping the scanner's log lines under 16 KB | A new Issue, only if the 16 KB check finds a split line |
| A self-hosted agent | Not needed while the free minutes last |

## Known limitations

- **One hosted parallel job,** shared with the enqueuer's pipeline: runs and pull request checks wait for each other.
- **1,800 minutes a month:** past that, runs queue until the next month or a parallel job is bought.
- **`ubuntu-latest` moves:** a new image version can change a tool under the pipeline.
- **A change to the dashboard alone redeploys the scanner,** with a new image tag built from the same code and a Migrate that changes nothing: about 12 minutes more than the dashboard needs.
- **A run started by the Dashboard repository uses RepoScanner's `develop` pipeline file,** so a change to the pipeline is merged in RepoScanner first.
- **Between a RepoScanner migration and the Dashboard's update to its copy,** the newly deployed dashboard stops at start-up, saying so (DASH-ADR-001).
- **If the dashboard's deployment fails after the scanner's,** the scanner stays deployed; the failed stage is run again once the cause is fixed.
- **No deployment slots on B1:** each dashboard deployment restarts the app, and open pages show Blazor's reconnecting message (#99).
- **The database, its users and the shared resources are not in the pipeline:** rebuilding means the README's commands, then a run.
- **No post-deploy check on the dashboard** until the health page exists.
- **The PAT** expires in Azure DevOps and is renewed by hand in Key Vault; the jobs pick up the new version within about 30 minutes, without a deployment.
