# Issue 12 and dashboard Issue 7: Build and deployment pipeline, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One pipeline in RepoScanner builds and tests the scanner and the dashboard on every merge into `develop` in either repository, applies the migrations to the empty `RepoScanner` database, then deploys the scanner's two jobs and, after them, the dashboard, with no step done by hand.

**Architecture:** `azure-pipelines.yml` at RepoScanner's root, with the Dashboard repository as a repository resource, and five stages in a line on Microsoft-hosted `ubuntu-latest`: Build (jobs Scanner, then Dashboard), Image, Migrate, DeployScanner, DeployDashboard. Every stage after Build runs only for `develop` and never for a pull request, signed in through `sc-reposcanner-dev` as `id-pipeline`. Two PowerShell scripts, `deploy/Deploy-ScannerJob.ps1` and `deploy/Deploy-Dashboard.ps1`, create or update what they deploy, built the way the enqueuer's live `deploy/Deploy-EnqueuerJob.ps1` is; a third, `deploy/Set-AlertRules.ps1`, comes last. No C# changes.

**Tech Stack:** Azure Pipelines YAML with a repository resource, Azure CLI with the `containerapp` and `scheduled-query` extensions, go-sqlcmd, PowerShell 7 on the agent and Windows PowerShell 5.1 locally, Docker, .NET SDK 10.0.401 from `global.json`, `dotnet-ef` 10.0.12 from `.config/dotnet-tools.json`, coverlet's 80% gate in both repositories.

**Spec:** [docs/design/2026-10-19-build-and-deployment-pipelines.md](../../design/2026-10-19-build-and-deployment-pipelines.md), approved on 2026-10-19 and committed in Task 0. Read its Decisions, Job settings, Dashboard settings, The pipeline in outline and Testing before starting; this plan argues from it. The enqueuer's pipeline and script (Enqueuer repository, `azure-pipelines.yml` and `deploy/Deploy-EnqueuerJob.ps1`) are the pattern to copy.

**Scope:** #73 (Task 3), #97 (Tasks 5 and 7), #98 (checked in Task 6; assigned by hand on 2026-10-17), and four tasks under #72 created in Task 0: #150 (ADR-030), #151 (Build and Image stages), #152 (the scanner's jobs) and #153 (alerts). Tasks 0 to 6 are the first branch and its pull request. Tasks 7 and 8 follow on a second branch of the same name, with Task 7's Dashboard changes on `feature/99-build-and-deployment-pipeline` in the Dashboard repository.

## Global Constraints

- **Branches:** `feature/72-build-and-deployment-pipeline` in RepoScanner, at `develop`'s `ee6b652`; recreated from `develop` for Tasks 7 and 8 once the first pull request is merged. `feature/99-build-and-deployment-pipeline` in Dashboard, from its `develop`, in Task 7.
- **Commits:** one per task (two where a task says so), only after the user has reviewed the diff. The message starts `This commit` and ends with the board task number, such as `#73`. No `Co-Authored-By` line, and no mention of Claude, Opus or any AI tool. Push, open a pull request, register the pipeline, add a policy or change any Azure DevOps setting only when the user says so.
- **Board:** a task goes to Doing when it starts and Done when its checks pass after its commit: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <id> --state <Doing|Done>`. #72 and #99 stay Doing until the last pull request is merged.
- **Azure:** the user runs anything that changes Azure, including the steps the design keeps by hand (Easy Auth, Monitoring Contributor). Claude runs only read-only `az ... show` and `list` and the scripts with `-WhatIf`. The jobs and the web app's settings are never changed by hand outside those steps.
- **Exact values** (design, Job settings and Dashboard settings):
  - Pipeline `RepoScanner` in Lumina; service connection `sc-reposcanner-dev`; environment `reposcanner-dev`; resource group `RepoAnalysis-dev`; Container Apps environment `code-reposcanner-dev`; registry `enqreposcannerdev`; image `reposcanner`, tag `$(Build.BuildId)`, never `latest`. Repository resource alias `dashboard`: `Lumina/Dashboard`, `ref: refs/heads/develop`.
  - Azure SQL: server `sql-reposcanner-dev.database.windows.net`, database `RepoScanner`. The connection string Build gives `ScannerDbContextFactory`, which never connects: `Server=tcp:sql-reposcanner-dev.database.windows.net,1433;Database=RepoScanner;Authentication=Active Directory Default;Encrypt=True`.
  - Jobs `job-scanner-nightly-dev` and `job-scanner-ondemand-dev`, `<lane>` being `nightly` or `ondemand`: trigger `Event`; scale rule named `scan-<lane>`, type `azure-queue`, metadata `accountName=strreposcannerdev`, `queueName=scan-<lane>`, `queueLength=1`, scale rule identity `id-scanner`; min executions `0`; max executions `1`; polling interval `30`; replica timeout `3600`; replica retry limit `0`; parallelism `1`; completion count `1`; CPU `1.0`; memory `2Gi`; identity and registry identity `id-scanner`; secret `azure-devops-pat` as `keyvaultref:https://kv-reposcanner-dev.vault.azure.net/secrets/azure-devops-pat,identityref:<id-scanner's resource ID>`.
  - Job environment variables, in this order: `AZURE_CLIENT_ID=<id-scanner's client ID>`, `Scanner__Queue__Name=scan-<lane>`, `Scanner__Queue__ServiceUri=https://strreposcannerdev.queue.core.windows.net`, `Scanner__AzureDevOps__OrganizationUrl=https://dev.azure.com/BlacklightInternalsMo`, `Scanner__AzureDevOps__Pat=secretref:azure-devops-pat`, `Scanner__Database__ConnectionString=Server=tcp:sql-reposcanner-dev.database.windows.net,1433;Database=RepoScanner;Authentication=Active Directory Managed Identity;User Id=<id-scanner's client ID>;Encrypt=True`. No `DOTNET_ENVIRONMENT`.
  - Web app `app-reposcanner-dashboard-dev`: WebSockets on, Always On on, ARR affinity on, HTTPS only on, FTPS `Disabled`, minimum TLS `1.2`, startup command `dotnet Dashboard.dll`, state `Running`. App settings `AZURE_CLIENT_ID=<id-dashboard's client ID>`, `Dashboard__Database__ConnectionString=` the job's string with `id-dashboard`'s client ID, `Dashboard__Queue__ServiceUri=https://strreposcannerdev.queue.core.windows.net`. Easy Auth's client secret setting name `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID`, an app setting holding `id-dashboard`'s client ID.
  - Client IDs and resource IDs are looked up by identity name at run time (`az identity show`), never written into a script.
- **The deploy condition**, on Image, Migrate, DeployScanner and DeployDashboard and nowhere else: `and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/develop'), ne(variables['Build.Reason'], 'PullRequest'))`. Each stage `dependsOn` the one before it.
- **Secrets:** nothing secret is in the pipeline, the scripts or Azure DevOps. Never read the PAT, from Key Vault or anywhere else, to check anything.
- **Script conventions** (as `Deploy-EnqueuerJob.ps1`): comment-based help with `.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER` and `.EXAMPLE`; `[CmdletBinding(SupportsShouldProcess)]`; its `Invoke-AzCommand` and `Invoke-Az` helpers copied unchanged; `$ErrorActionPreference = 'Stop'`; comments citing the design by decision number; runs under PowerShell 7 and Windows PowerShell 5.1, so no `??`, ternary or `&&`.
- **Workspace** for logs and records, git-ignored: `.superpowers/sdd/2026-10-19-build-and-deployment-pipeline/`, one `taskN.md` per task.
- No em dash (U+2014) in any file. British English.
- `dotnet build` has zero warnings and `dotnet test` passes with the 80% gate, in each repository a task touches, at the end of the task.

## Review Focus

Inputs the spec implies but no happy-path step exercises, most likely to bite first. Each has its step in the task named.

1. **A pull request in either repository, a manual run of another branch, or a documentation-only merge** must never push an image, migrate or deploy. Task 2, Step 7; Task 7, Step 6.
2. **A lookup that fails for a reason other than "missing"** (an expired sign-in, a missing role, a missing resource group) must stop the script before it creates or changes anything. Task 4, Step 5; Task 5, Step 4.
3. **Tests that assume Windows,** in either repository (path separators, line endings, case-insensitive file names, `git` on the path), failing on the Linux agent. Task 2, Step 2.
4. **A second run with no change:** Migrate changes nothing, both jobs take the update path and come out the same, and the web app's settings stay as they were. Task 3, Step 2; Task 6, Step 5.
5. **Easy Auth off, or still signing in with its client secret:** the dashboard's code must not be deployed. Task 5, Step 4.

---

### Task 0: Board, branch and the documents (#72)

- [ ] **Step 1: Show the user the board changes below and wait for the go-ahead**

- [ ] **Step 2: Lift `out-of-scope`**

Set `System.Tags` to `feature; scanner` on #72 and #73, and `dashboard; feature` on #99, #97 and #98. Add a dated note at the top of #72's and #99's descriptions, through `az rest` with a UTF-8 body file without a BOM (as in the Lumina board memory): *Back in scope (2026-10-19): Steven Wilson asked for one pipeline in RepoScanner deploying the scanner first and the dashboard second, migrations applied by it, and database users added by hand. Design: docs/design/2026-10-19-build-and-deployment-pipelines.md.*

- [ ] **Step 3: Create the four tasks under #72**

Each with `az boards work-item create --org https://dev.azure.com/Blacklight-Internal --project Lumina --type Task --iteration "Lumina\Sprint 1 - Scanner" --fields "System.Tags=feature; scanner" --title "<title>"`, then `az boards work-item relation add --org https://dev.azure.com/Blacklight-Internal --id <new id> --relation-type parent --target-id 72`:

| ID | Title |
|---|---|
| #150 | ADR-030: deploy to a dev environment through Azure Pipelines, superseding ADR-029 |
| #151 | Build and Image stages: the scanner and the dashboard built and tested on Linux with the 80% gate, reposcanner:<build ID> pushed, and build validation on develop and main in both repositories (ADR-005, ADR-020) |
| #152 | Scanner jobs: job-scanner-nightly-dev and job-scanner-ondemand-dev created or updated by the pipeline (ADR-011, ADR-012) |
| #153 | Alerts: a poisoned message and a rejected PAT, created by the pipeline and emailed through one action group |

Created on 2026-10-19 as #150 to #153. Expected: `az boards work-item show --id 72 --expand relations` lists #73 and the four new tasks.

- [ ] **Step 4: Set #72 and #99 to Doing**

- [ ] **Step 5: Commit the design and this plan, after the user's review**

```powershell
git add docs/design/2026-10-19-build-and-deployment-pipelines.md docs/superpowers/plans/2026-10-19-build-and-deployment-pipeline.md
git commit -m "This commit adds the design and implementation plan for Issue 12 and dashboard Issue 7: ... #72"
```

---

### Task 1: ADR-030 (#150)

**Files:**
- Create: `docs/adr/ADR-030-deploy-to-a-dev-environment-through-azure-pipelines.md`
- Modify: `docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md` (Status line only), `ADR-005`, `ADR-006`, `ADR-007`, `ADR-008`, `ADR-020` (an amendment each), `docs/adr/README.md`

- [ ] **Step 1: Set #150 to Doing**

- [ ] **Step 2: Write ADR-030**

Title `# ADR-030: Deploy to a dev environment through Azure Pipelines`, in ADR-029's layout: Status `Accepted`, Date `2026-10-19`, Source as the design's ADR-030 section, then `## Context`, `## Decision` and `## Consequences` with the design's text.

- [ ] **Step 3: Mark ADR-029 superseded**

Its Status becomes `Superseded by [ADR-030](ADR-030-deploy-to-a-dev-environment-through-azure-pipelines.md) on 2026-10-19`. Nothing else in it changes.

- [ ] **Step 4: Add a dated amendment to each of the five ADRs ADR-029 amended**

Each starts `**2026-10-19 (Issue 12, ADR-030):** ADR-030 supersedes ADR-029, so the 2026-10-04 amendment above no longer applies.` and goes on:

| ADR | Then |
|---|---|
| ADR-005 | Images are tagged `$(Build.BuildId)` by RepoScanner's pipeline and the enqueuer's. |
| ADR-006 | RepoScanner's pipeline applies the idempotent script to Azure SQL as `id-pipeline` before either app is deployed. |
| ADR-007 | The pipelines sign in through Workload Identity Federation as `id-pipeline`, a user-assigned managed identity, and the dashboard's Easy Auth uses `id-dashboard` as a federated credential, with no client secret. |
| ADR-008 | In Azure, `id-pipeline` holds db_ddladmin; `id-scanner` and `id-dashboard` hold db_datareader and db_datawriter, added by hand (README, Database users in Azure). |
| ADR-020 | The build validation policy comes with the pipeline and gets its own amendment when it is added. |

Each ends `The decision is otherwise unchanged.`

- [ ] **Step 5: Update the ADR index**

Add the ADR-030 row (`Deploy to a dev environment through Azure Pipelines`, `Accepted`); ADR-029's status `Superseded by ADR-030`; ADR-005, 006, 007, 008 and 020 `Accepted, amended 2026-10-04 (ADR-029) and 2026-10-19 (ADR-030)`; "The next new ADR is **ADR-031**".

- [ ] **Step 6: Check, then commit after the user's review**

Expected: every relative link in the changed files names a file that exists, and `Select-String -Pattern ([char]0x2014) docs/adr/*.md` finds nothing. Commit ending `#150`; set #150 to Done.

---

### Task 2: Build and Image stages (#151)

**Files:**
- Create: `azure-pipelines.yml`
- Create: `.superpowers/sdd/2026-10-19-build-and-deployment-pipeline/task2.md`

**Interfaces:**
- Consumes: `RepoScanner.sln`, `global.json`, `.config/dotnet-tools.json`, `ScannerDbContextFactory`'s `Scanner__Database__ConnectionString`, the Dockerfile; Dashboard's `Dashboard.sln`, `src/Dashboard/Dashboard.csproj`, `global.json`. Coverage reports at `tests/<project>/TestResults/coverage/coverage.cobertura.xml` in both repositories.
- Produces, for Tasks 3 to 5: variables `serviceConnection: sc-reposcanner-dev`, `registry: enqreposcannerdev`, `image: reposcanner`; the repository resource `dashboard`; stages `Build` (jobs `Scanner`, `Dashboard`) and `Image`; pipeline artifacts `migrate` holding `migrate.sql` and `dashboard` holding `dashboard.zip`; the image `enqreposcannerdev.azurecr.io/reposcanner:$(Build.BuildId)` on every `develop` run.

- [ ] **Step 1: Set #151 to Doing**

- [ ] **Step 2: Run both repositories' tests on Linux before anything is pushed**

As in the enqueuer's plan: `git archive` RepoScanner's `HEAD`, and `git -C ..\Dashboard archive develop`, each expanded under the workspace, then for each `docker run --rm -v "<folder>:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test <solution> --configuration Release`.
Expected: every test passes in both, each with at least 80% line coverage. A test that fails only on Linux is fixed in its own commit in its own repository (`#151` in RepoScanner, `#97` in Dashboard), after the user's review, before Step 3 (Review Focus 3).

- [ ] **Step 3: Check the migration script locally**

In one PowerShell session, set `$env:Scanner__Database__ConnectionString` to the Build string from Global Constraints (this session only), run `dotnet tool restore`, then `dotnet ef migrations script --idempotent --project src/RepoScanner.Data --startup-project src/RepoScanner.Data --output .superpowers/sdd/2026-10-19-build-and-deployment-pipeline/migrate.sql`.
Expected: the file is written with no sign-in prompt; it begins with `IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL` and names `20260924190222_InitialCreate`.

- [ ] **Step 4: Write `azure-pipelines.yml`: trigger, resource, pool, variables and the Build stage**

- `trigger`: `branches: include: [develop]`, `paths: exclude: [docs, '**/*.md']`. No `pr:` section: pull requests run through build validation policies (Task 7).
- `resources.repositories`: `dashboard`, `type: git`, `name: Lumina/Dashboard`, `ref: refs/heads/develop`, `trigger` with the same branches and paths.
- `pool: vmImage: ubuntu-latest`; the three variables.
- Stage `Build`, job `Scanner`: `checkout: self`; `UseDotNet@2` with `useGlobalJson: true`; `dotnet restore RepoScanner.sln`; `dotnet build RepoScanner.sln --configuration Release --no-restore`; `dotnet test RepoScanner.sln --configuration Release --no-build --logger trx --results-directory $(Agent.TempDirectory)/TestResults`; `dotnet tool restore`; `dotnet ef migrations script --idempotent --project src/RepoScanner.Data --startup-project src/RepoScanner.Data --configuration Release --no-build --output $(Build.ArtifactStagingDirectory)/migrate/migrate.sql` with `env: Scanner__Database__ConnectionString:` the Build string; `PublishTestResults@2` (`VSTest`, the `.trx` files, `testRunTitle: RepoScanner`) and `PublishCodeCoverageResults@2` (`summaryFileLocation: '$(Build.SourcesDirectory)/tests/**/TestResults/coverage/coverage.cobertura.xml'`), both `condition: succeededOrFailed()`; `PublishPipelineArtifact@1` of the `migrate` folder as `migrate`.
- Job `Dashboard`, `dependsOn: Scanner` (design decision 9): `checkout: dashboard`; the same `UseDotNet@2`, restore, build and test for `Dashboard.sln`, `testRunTitle: Dashboard`; `dotnet publish src/Dashboard/Dashboard.csproj --configuration Release --no-build --output $(Build.ArtifactStagingDirectory)/dashboard-app`; `ArchiveFiles@2` of that folder with `includeRootFolder: false` to `$(Build.ArtifactStagingDirectory)/dashboard/dashboard.zip`; coverage from `$(Build.SourcesDirectory)/tests/Dashboard.Tests/TestResults/coverage/coverage.cobertura.xml`; `PublishPipelineArtifact@1` of the `dashboard` folder as `dashboard`.

Comments at the top cite the design (decisions 5 to 10) and say why there is no `pr:` section.

- [ ] **Step 5: Add the Image stage**

Stage `Image`, `dependsOn: Build`, the deploy condition, one job with one `AzureCLI@2` step exactly as the enqueuer's Image stage, with `image: reposcanner` (design decision 11).

- [ ] **Step 6: Show the user the YAML and ask to push and register**

Ask to push the branch and register the pipeline in Lumina as `RepoScanner` from `azure-pipelines.yml` on this branch, without running it. Wait for the answer.

- [ ] **Step 7: Run it by hand on this branch**

The user runs it on `feature/72-build-and-deployment-pipeline` and permits it to use the Dashboard repository.
Expected: both Build jobs succeed; the summary shows the `RepoScanner` and `Dashboard` test runs and coverage of at least 80% for each app; the artifacts list `migrate/migrate.sql` and `dashboard/dashboard.zip`, whose root holds `Dashboard.dll`; Image is **Skipped** (Review Focus 1). If the Code Coverage tab shows only one app, also publish each job's Cobertura files as artifacts `coverage-scanner` and `coverage-dashboard`. Record the run number, each job's minutes and the results in `task2.md`.

- [ ] **Step 8: Commit, after the user's review**

Message ending `#151`, saying what Step 7 showed. #151 stays Doing until Task 7.

---

### Task 3: Migrate (#73)

**Files:**
- Modify: `azure-pipelines.yml` (stage `Migrate`), `README.md` (Who applies migrations; a new Database users in Azure subsection after it)

**Interfaces:**
- Consumes: artifact `migrate`; stage `Image`.
- Produces: stage `Migrate`, which DeployScanner depends on.

- [ ] **Step 1: Set #73 to Doing**

- [ ] **Step 2: Apply the script twice to an empty scratch database**

With go-sqlcmd (`winget install sqlcmd`, with the user's go-ahead, if it is not installed): create `RepoScanner_MigrateCheck` on `.\SQLEXPRESS`, apply Task 2's `migrate.sql` with `sqlcmd -S .\SQLEXPRESS -E -d RepoScanner_MigrateCheck -b -i <file>`, then apply it again.
Expected: both runs exit 0; the database has every table in the Issue 2 design and `__EFMigrationsHistory` holds one row, `20260924190222_InitialCreate` (Review Focus 4). Drop the scratch database afterwards.

- [ ] **Step 3: Add the Migrate stage**

Stage `Migrate`, `dependsOn: Image`, the deploy condition. One job: `download: current` with `artifact: migrate`, then one `AzureCLI@2` step, `pscore`, inline, starting `$ErrorActionPreference = 'Stop'` and `$PSNativeCommandUseErrorActionPreference = $true`. It installs go-sqlcmd with `sudo apt-get install --yes sqlcmd` from Microsoft's Ubuntu package repository if `sqlcmd --version` does not print a `v1.` version (the agent may have only the ODBC one), then runs `sqlcmd -S tcp:sql-reposcanner-dev.database.windows.net,1433 -d RepoScanner --authentication-method ActiveDirectoryDefault -b -i $(Pipeline.Workspace)/migrate/migrate.sql`. Inside the Azure CLI task, `ActiveDirectoryDefault` signs in with the CLI's session as `id-pipeline`: no password and no SQL login (design decision 12). If the first run shows the agent cannot do this, use a token from `az account get-access-token --resource https://database.windows.net/` instead, and record why in `task3.md`.

- [ ] **Step 4: The README**

- Who applies migrations, **Azure:** the pipeline's Migrate stage, as `id-pipeline`, applies the idempotent script before either app is deployed (ADR-006, ADR-030). The ADR-029 link goes.
- New `### Database users in Azure`: the design's Database users table; the script below; the two users-and-roles queries used on 2026-10-19; and "Rebuilding the database": delete it and create it again (Basic, `--backup-storage-redundancy Local`), run the script as the server's Entra admin, then run the pipeline.

```sql
CREATE USER [id-scanner] FROM EXTERNAL PROVIDER WITH OBJECT_ID = 'fce27e24-9ffa-40e2-a01e-06415f765586';
ALTER ROLE db_datareader ADD MEMBER [id-scanner];
ALTER ROLE db_datawriter ADD MEMBER [id-scanner];
CREATE USER [id-dashboard] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '8c4068b3-51a9-4c54-b95f-356b9ef4a894';
ALTER ROLE db_datareader ADD MEMBER [id-dashboard];
ALTER ROLE db_datawriter ADD MEMBER [id-dashboard];
CREATE USER [id-pipeline] FROM EXTERNAL PROVIDER WITH OBJECT_ID = 'f7ab4ce3-509a-471d-8233-79987139e26e';
ALTER ROLE db_ddladmin ADD MEMBER [id-pipeline];
ALTER ROLE db_datareader ADD MEMBER [id-pipeline];
ALTER ROLE db_datawriter ADD MEMBER [id-pipeline];
GRANT REFERENCES ON SCHEMA::dbo TO [id-pipeline];
```

- [ ] **Step 5: Commit, after the user's review**

Message ending `#73`. #73 goes to Done in Task 6, Step 3.

---

### Task 4: The scanner's jobs (#152)

**Files:**
- Create: `deploy/Deploy-ScannerJob.ps1`
- Modify: `azure-pipelines.yml` (stage `DeployScanner`), `README.md` (a new `## Deployment` section after Run in a container)

**Interfaces:**
- Consumes: stage `Migrate`; the image Task 2 pushes.
- Produces: `deploy/Deploy-ScannerJob.ps1`, parameters `-Image` (string, mandatory) and `-Lane` (mandatory, `ValidateSet('nightly', 'ondemand')`), with defaults `-ResourceGroup 'RepoAnalysis-dev'`, `-Environment 'code-reposcanner-dev'`, `-Registry 'enqreposcannerdev'`, `-Identity 'id-scanner'`, `-KeyVault 'kv-reposcanner-dev'`, `-SecretName 'azure-devops-pat'`, `-StorageAccount 'strreposcannerdev'`, `-OrganizationUrl 'https://dev.azure.com/BlacklightInternalsMo'`, `-SqlServer 'sql-reposcanner-dev.database.windows.net'`, `-Database 'RepoScanner'`. The job name is `job-scanner-<Lane>-dev` and the queue `scan-<Lane>`. Exit 0 after a create or an update; a terminating error on any failed `az` call.

- [ ] **Step 1: Set #152 to Doing**

- [ ] **Step 2: Check `id-scanner`'s roles, read-only**

`az role assignment list --assignee fce27e24-9ffa-40e2-a01e-06415f765586 --all --output table`.
Expected: AcrPull on the registry, Key Vault Secrets User, and queue data roles that let it read, receive and delete on `scan-nightly` and `scan-ondemand` and send to both poison queues (ADR-014). Record the list in `task4.md`; anything missing is assigned by the user by hand before Task 6 (design decision 18).

- [ ] **Step 3: Write the script from `Deploy-EnqueuerJob.ps1`**

Keep its order: the extension step, the `id-scanner` lookup, the image check (registry prefix, then `az acr repository show`, with the same message for a missing tag), the secret reference, the `ResourceNotFound`-only exists check, create or update, then a summary. Change only:
- **Create:** `--trigger-type Event` with min executions, max executions, polling interval, the scale rule name, type, metadata and `--scale-rule-identity <id-scanner's resource ID>`, the replica timeout, retry limit, parallelism, completion count, CPU and memory, all from Global Constraints, plus the image, identity, registry, secret and six environment variables.
- **Update:** identity, registry and secret set again, as the enqueuer's, then `az containerapp job update` with the image, timeout, retry limit, min and max executions, polling interval, the same scale rule options, CPU, memory and `--replace-env-vars` with the six variables.
- **Summary:** `<job>: image <image>, queue scan-<lane>, timeout <s> s, retries <n>, max executions <n>, identity id-scanner`.

- [ ] **Step 4: Check the paths locally with `-WhatIf`**

Signed in as the user, with an `enqueuer` tag from `az acr repository show-tags --name enqreposcannerdev --repository enqueuer` as a stand-in image that exists:

```powershell
powershell -ExecutionPolicy Bypass -File deploy/Deploy-ScannerJob.ps1 -Lane nightly -Image enqreposcannerdev.azurecr.io/enqueuer:<tag> -WhatIf
```

Expected: `What if: Performing the operation "Create" on target "job-scanner-nightly-dev".`, exit 0, and `az containerapp job list --resource-group RepoAnalysis-dev --query "[].name"` still only `job-enqueuer-dev`. The same with `-Lane ondemand` names `job-scanner-ondemand-dev`.

- [ ] **Step 5: Check the stops locally**

Expected, each exiting 1 before any Create or Update line (Review Focus 2):
- `-Image enqreposcannerdev.azurecr.io/reposcanner:0 -WhatIf`: the missing-tag message.
- `-Image docker.io/library/reposcanner:1 -WhatIf`: not in `enqreposcannerdev`.
- `-ResourceGroup does-not-exist -WhatIf`: `az identity show` failed.
- `-Lane weekly`: rejected by `ValidateSet`.

- [ ] **Step 6: Add the DeployScanner stage**

Stage `DeployScanner`, `dependsOn: Migrate`, the deploy condition. One `deployment` job, `environment: reposcanner-dev`, `strategy: runOnce: deploy: steps:` `download: none`, `checkout: self`, then two `AzureCLI@2` steps, `pscore`, `scriptPath: deploy/Deploy-ScannerJob.ps1`, with `arguments: -Lane nightly -Image $(registry).azurecr.io/$(image):$(Build.BuildId)` and then `-Lane ondemand`.

- [ ] **Step 7: The README's Deployment section, scanner part**

What a merge into `develop` does (the design's Purpose, seven lines); the jobs' settings table; that the jobs are changed only by the pipeline; how to roll back (re-run an earlier run's deploy stages); how to start one execution by hand (`az containerapp job start`).

- [ ] **Step 8: Commit, after the user's review**

Message ending `#152`. #152 goes to Done in Task 6.

---

### Task 5: Easy Auth and the dashboard's deployment (#97)

**Files:**
- Create: `deploy/Deploy-Dashboard.ps1`
- Modify: `azure-pipelines.yml` (stage `DeployDashboard`), `README.md` (Deployment: the dashboard part and Easy Auth's by-hand steps)

**Interfaces:**
- Consumes: stage `DeployScanner`; artifact `dashboard`.
- Produces: `deploy/Deploy-Dashboard.ps1`, parameters `-Package` (string, mandatory: the zip's path), with defaults `-ResourceGroup 'RepoAnalysis-dev'`, `-WebApp 'app-reposcanner-dashboard-dev'`, `-Identity 'id-dashboard'`, `-SqlServer 'sql-reposcanner-dev.database.windows.net'`, `-Database 'RepoScanner'`, `-StorageAccount 'strreposcannerdev'`. Exit 0 after a deployment; a terminating error on any failed check or `az` call.

- [ ] **Step 1: Set #97 to Doing**

- [ ] **Step 2: Write the script**

With the enqueuer's helpers:
1. Stop if `-Package` is not a file.
2. Look up `id-dashboard`'s client ID.
3. **The Easy Auth check** (design decision 16), from `az webapp auth show` and the app settings' names and the one value it needs: the platform is enabled, sign-in is required, unauthenticated requests are redirected to sign in, the Microsoft provider's client secret setting name is `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID`, and that app setting equals `id-dashboard`'s client ID. Any failure stops the script with: `Easy Auth is not set up on <web app> as the README's Easy Auth steps say, so the dashboard was not deployed: <what was wrong>`. No other app setting's value is read.
4. Inside `ShouldProcess($WebApp, 'Apply settings')`: `az webapp config set` with WebSockets, Always On, FTPS, minimum TLS and the startup command; `az webapp update` with HTTPS only and ARR affinity; `az webapp config appsettings set` with the three app settings. Nothing is deleted.
5. Inside `ShouldProcess($WebApp, 'Start')`, only when its state is not `Running`: `az webapp start`.
6. Inside `ShouldProcess($WebApp, 'Deploy')`: `az webapp deploy --src-path $Package --type zip`.
7. A summary: state, WebSockets, Always On, ARR affinity, HTTPS only, and the deployment's time.

- [ ] **Step 3: Download Task 2's `dashboard.zip` into the workspace**

From the run recorded in `task2.md`.

- [ ] **Step 4: Check the stops locally, before Easy Auth is on**

Expected, each exiting 1 with nothing changed (Review Focus 2 and 5):
- `-Package <ws>/dashboard.zip -WhatIf`: the Easy Auth message, naming the platform as not enabled.
- `-Package <ws>/missing.zip -WhatIf`: the package is not a file.
- `-WebApp does-not-exist -Package <ws>/dashboard.zip -WhatIf`: the `az` error, before any What if line.

- [ ] **Step 5: The user turns Easy Auth on, by hand, in this order**

1. Web app, Authentication, Add identity provider, Microsoft: create a new app registration named `app-reposcanner-dashboard-dev`, current tenant only, require authentication, HTTP 302 redirect for unauthenticated requests.
2. Entra admin centre, that app registration, Certificates and secrets, Federated credentials, Add: the managed identity scenario, `id-dashboard`.
3. Web app, Environment variables: add `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` with `id-dashboard`'s client ID.
4. Web app, Authentication, edit the Microsoft provider: Client secret setting name `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID`.

The client secret the portal made is deleted in Task 6, Step 7, once sign-in has been seen to work. Record the app registration's client ID in `task5.md`.

- [ ] **Step 6: Check the paths locally, after Easy Auth is on**

`-Package <ws>/dashboard.zip -WhatIf`. Expected: exit 0, the What if lines for Apply settings, Start and Deploy, and `az webapp show --query state` still `Stopped`.

- [ ] **Step 7: Add the DeployDashboard stage**

Stage `DeployDashboard`, `dependsOn: DeployScanner`, the deploy condition. One `deployment` job, `environment: reposcanner-dev`, steps `download: current` with `artifact: dashboard`, `checkout: self`, then `AzureCLI@2`, `pscore`, `scriptPath: deploy/Deploy-Dashboard.ps1`, `arguments: -Package $(Pipeline.Workspace)/dashboard/dashboard.zip`.

- [ ] **Step 8: The README's Deployment section, dashboard part**

The Dashboard settings table; Easy Auth's four by-hand steps from Step 5 and the secret's deletion; that the script puts the listed settings back and deletes none.

- [ ] **Step 9: Commit, after the user's review**

Message ending `#97`. #97 goes to Done in Task 6.

---

### Task 6: The pull request and the first run on `develop` (#73, #152, #97, #98)

- [ ] **Step 1: Review, push and merge**

A fresh whole-branch review, its points fixed and committed after the user's review. Then the user pushes, the pull request is opened in PR 2's layout with #72, #73, #97 and the four new tasks linked, and the user merges it.

- [ ] **Step 2: The first run**

The user permits the run to use `sc-reposcanner-dev`, `reposcanner-dev` and the Dashboard repository.
Expected: Build, Image, Migrate, DeployScanner and DeployDashboard succeed in that order; the registry lists `reposcanner:<build ID>` and no `latest`. Record the minutes per stage in `task6.md`.

- [ ] **Step 3: The database (criteria 2, 3 and 5)**

The user runs in the portal's Query editor: the list of tables, `SELECT MigrationId FROM __EFMigrationsHistory`, and the two users-and-roles queries.
Expected: every table in the Issue 2 design; one migration, `20260924190222_InitialCreate`; the three users with exactly the roles and REFERENCES in the README. Set #73 to Done.

- [ ] **Step 4: The jobs**

`az containerapp job show` for both jobs shows every value in Global Constraints. `az containerapp job secret list` shows `azure-devops-pat` with a `keyVaultUrl` and no value, and the environment variable is `secretRef: azure-devops-pat`.

- [ ] **Step 5: A second run with no change (Review Focus 4)**

The user re-runs the run's Migrate, DeployScanner and DeployDashboard stages.
Expected: Migrate succeeds and `__EFMigrationsHistory` still has one row; both scripts print Updated; `az containerapp job show` and `az webapp config show` are identical to Step 4's.

- [ ] **Step 6: The nightly lane (#119)**

Expected: `az containerapp job execution list --name job-scanner-nightly-dev --resource-group RepoAnalysis-dev` shows executions that drained the messages waiting on `scan-nightly`, each `Succeeded`; `ScanRuns` rows `Completed` or `PartiallyCompleted`; one Log Analytics query by `CorrelationId`, as in the README, returns the enqueuer's line and the scanner's lines for one request. Comment on #119 in the Enqueuer epic that its last check passed.

- [ ] **Step 7: The dashboard (criteria 8, 11 and 12)**

Expected:
- Its log stream shows the scan database at migration `20260924190222_InitialCreate`.
- A private browser window sees only the Microsoft sign-in page at the app's address.
- Signed in, Home shows the scans from Step 6.
- Scan now on `victim-repo` puts a message on `scan-ondemand`, `job-scanner-ondemand-dev` runs, and a `ScanRuns` row for it is `Completed` with the on-demand trigger.

Then the user deletes the app setting holding the portal's client secret (`MICROSOFT_PROVIDER_AUTHENTICATION_SECRET`) and the client secret itself in the app registration, and signs in again in a new private window. Expected: sign-in still works. Set #97 to Done.

- [ ] **Step 8: The 16 KB line (#72's note)**

The user starts one execution with its command overridden, so no application code changes, from Azure Cloud Shell's Bash: on Windows `az` is a batch file, which would expand the `%` and mangle the quotes. `%020000d` pads a 0 to 20,000 characters.

```bash
az containerapp job start --name job-scanner-ondemand-dev --resource-group RepoAnalysis-dev --image enqreposcannerdev.azurecr.io/reposcanner:<build ID> --command "/bin/sh" --args "-c" 'printf "{\"@l\":\"Information\",\"@m\":\"16 KB check\",\"Padding\":\"%020000d\"}\n" 0'
```

Then in Log Analytics: `ContainerAppConsoleLogs | where Log has "16 KB check" | extend e = parse_json(Log) | project TimeGenerated, Length = strlen(Log), Message = tostring(e["@m"])`.
Expected: one row, `Length` over 20,000, `Message` `16 KB check`. If not: propose a new scanner Issue to keep each line under 16 KB, add the split to the design's and the README's Known limitations, and go on (design, Testing).

- [ ] **Step 9: `id-dashboard`'s roles (#98, criterion 13)**

`az role assignment list --assignee 8c4068b3-51a9-4c54-b95f-356b9ef4a894 --all --output table`. Expected: Storage Queue Data Message Sender on `scan-ondemand` only, and no Key Vault role. Comment on #98 that the roles were assigned by hand on 2026-10-17 and checked on this date, and set it to Done.

- [ ] **Step 10: No secrets (criterion 14)**

`az pipelines variable-group list --org https://dev.azure.com/Blacklight-Internal --project Lumina` is empty; the `RepoScanner` pipeline's Variables page and Lumina's Secure files have none. The user, who knows the PAT, searches the run's logs and the jobs' Log Analytics lines for it and finds nothing; Claude never sees it. Set #152 to Done.

---

### Task 7: Build validation, the ADR amendments and the READMEs (#151, #97)

On `feature/72-build-and-deployment-pipeline`, created again from `develop`.

**Files:**
- Modify: `docs/adr/ADR-020-branching-strategy-and-branch-policies.md`, `docs/adr/README.md`, `README.md`
- Modify, in Dashboard on `feature/99-build-and-deployment-pipeline`: `docs/adr/DASH-ADR-001-own-repository-and-own-copy-of-the-data-model.md`, `docs/adr/README.md`, `README.md`

- [ ] **Step 1: The build validation policies, with the user's go-ahead**

On `develop` and `main` in RepoScanner and in Dashboard: pipeline `RepoScanner`, required, expiring as soon as the branch is updated, display name `RepoScanner and Dashboard build`. The user adds them, or approves `az repos policy build create --org https://dev.azure.com/Blacklight-Internal --project Lumina --repository-id <id> --branch <develop|main> --build-definition-id <id> --display-name "RepoScanner and Dashboard build" --blocking true --enabled true --manual-queue-only false --queue-on-source-update-only false --valid-duration 0`.

- [ ] **Step 2: The coverage gate fails a pull request (criterion 6)**

A throwaway RepoScanner branch removes one Scanner test file, so line coverage drops below 80%, opened as a pull request. Expected: the policy's run fails in Build's Scanner job. Record it, abandon the pull request and delete the branch.

- [ ] **Step 3: Breaking a rule fails a pull request (criterion 7)**

A throwaway branch breaks SEC003 as in Issue 11's mutation check. Expected: Build fails on SEC003's tests. Record it, abandon and delete.

- [ ] **Step 4: Amend ADR-020**

`**<date> (Issue 12, ADR-030):** the build validation policy is in place on develop and main in RepoScanner and Dashboard: each pull request runs RepoScanner's pipeline's Build stage on its merge commit, with both apps' 80% gates, and cannot be completed until it passes. The decision is otherwise unchanged.` The index row gains the date.

- [ ] **Step 5: The Dashboard repository**

On `feature/99-build-and-deployment-pipeline` from Dashboard's `develop`:
- DASH-ADR-001, under Amendments: `**<date> (Issue 7, #99):** the dashboard is built, tested and deployed by RepoScanner's pipeline, which checks this repository out as a resource: a merge into develop here starts it, and the build validation policy on develop and main runs its Build stage on every pull request. This repository has no pipeline file of its own. Easy Auth is on, signing in anyone in the Blacklight tenant, through id-dashboard as a federated credential with no client secret (#97). The decision is otherwise unchanged.` The index row gains the date.
- README: Build and test says pull requests are built and tested by RepoScanner's pipeline; a new `## Deployment` section points to RepoScanner's README and the design; Known limitations gains the design's dashboard lines (no deployment slots, stopping between a RepoScanner migration and this copy's update, no post-deploy check, the whole tenant may sign in).

Commit ending `#97`, after the user's review; push and pull request when the user says.

- [ ] **Step 6: A Dashboard pull request and a documentation-only merge (Review Focus 1)**

Step 5's Dashboard pull request runs this pipeline on its merge commit. Expected: Build succeeds and Image onwards are **Skipped**. When the user merges it, it changes only `docs` and `*.md` files. Expected: no run starts. Record both. A code merge into Dashboard's `develop` starting a run is checked with the next Dashboard code change, and noted as waiting in `task7.md`.

- [ ] **Step 7: RepoScanner's README**

Build and run says pull requests are built and tested by the pipeline; Deployment gains the measured minutes from `task2.md` and `task6.md`; the Known limitations line about searches being checked only once the job runs in Azure is replaced by Task 6's result; Known limitations gains the design's list. No ADR-029 link is left except in ADR-030's history.

- [ ] **Step 8: Commit, after the user's review**

Message ending `#151`. Set #151 to Done.

---

### Task 8: Alerts (#153)

On the same branch as Task 7.

**Files:**
- Create: `deploy/Set-AlertRules.ps1`
- Modify: `azure-pipelines.yml` (a last step in DeployScanner, and a variable), `README.md` (Alerts)

**Interfaces:**
- Produces: `deploy/Set-AlertRules.ps1`, parameters `-Email` (string, mandatory), with defaults `-ResourceGroup 'RepoAnalysis-dev'`, `-Workspace 'log-reposcanner-dev'`, `-ActionGroup 'ag-reposcanner-dev'`. Rules `alert-poisoned-message` and `alert-rejected-pat`.

- [ ] **Step 1: Set #153 to Doing; the user adds Monitoring Contributor**

The user assigns Monitoring Contributor on `RepoAnalysis-dev` to `id-pipeline` by hand (design decision 22). Read-only check: it appears in `az role assignment list --assignee f7ab4ce3-509a-471d-8233-79987139e26e --all`.

- [ ] **Step 2: Write the script**

With the enqueuer's helpers and the `scheduled-query` extension added as the job scripts add `containerapp`:
- The action group `ag-reposcanner-dev`, short name `reposcanner`, one email receiver, `-Email`; created or updated.
- Each rule looked up with `az monitor scheduled-query show`: only `ResourceNotFound` means create; otherwise update. Both scoped to the workspace, evaluated every 15 minutes over a 15-minute window, firing when the query returns more than 0 rows, acting through the action group. `alert-poisoned-message` uses the README's poisoned-message search, severity 2; `alert-rejected-pat` the README's rejected-PAT search, severity 1. The two queries are copied from the README unchanged, so the tests that pin their lines still guard them.

- [ ] **Step 3: Check it locally with `-WhatIf`**

`-Email <the user's address> -WhatIf`. Expected: What if lines for the action group and both rules, exit 0, and `az monitor scheduled-query list --resource-group RepoAnalysis-dev` still empty. `-ResourceGroup does-not-exist -WhatIf` stops before any What if line.

- [ ] **Step 4: Add it to the pipeline**

A variable `alertEmail` with the address the user gives, and a last `AzureCLI@2` step in DeployScanner running `deploy/Set-AlertRules.ps1 -Email $(alertEmail)`.

- [ ] **Step 5: The README's Alerts**

The two rules, how often they run, that they email through `ag-reposcanner-dev`, and that the pipeline creates them; the ADR-029 sentence goes.

- [ ] **Step 6: Commit, after the user's review; then the pull request and its run**

Message ending `#153`. After a fresh review of the second branch, the user pushes, opens the pull request (its own policy run is Build only), and merges it.
Expected: the run on `develop` creates the action group and both rules, listed by `az monitor scheduled-query list`. Then the user puts a message that is not JSON on `scan-ondemand` (`az storage message put --auth-mode key`). Expected: it is moved to `scan-ondemand-poison`, and the alert email arrives within 30 minutes. Set #153 to Done, then #72 and #99 once their last pull request is merged.
