<#
.SYNOPSIS
    Creates one of the scanner's two Container Apps jobs if it is missing, or updates it to a new image and its
    settings (Issue 12, #152).

.DESCRIPTION
    Run by the pipeline's DeployScanner stage, once for each lane, signed in as id-pipeline through
    sc-reposcanner-dev. Never run it by hand without -WhatIf: the jobs are changed only by the pipeline (design,
    decision 2). Built the way the enqueuer's deploy/Deploy-EnqueuerJob.ps1 is.

    Looks up id-scanner and checks the image is already in the registry, stopping before anything is changed if a run
    asks for an image it never built. Then looks for the lane's job, job-scanner-<lane>-dev:

        missing   creates it with every setting in the design's Job settings, started by an azure-queue scale rule
                  on scan-<lane> that reads the queue as id-scanner
        exists    assigns its identity, registry and Key Vault secret reference again, then updates its image,
                  timeout, retries, executions, polling interval, scale rule, CPU, memory and environment
                  variables, so running it twice changes nothing but the image (decision 13)

    Any other failure to look the job up stops the script rather than trying to create a job that may exist. Holds no
    secret: the PAT reaches the job as a Key Vault reference read by id-scanner, and the database is reached as
    id-scanner. Exits 0 after a create or an update; any failed az command stops it with that command's error.

.PARAMETER Image
    The image to deploy, such as enqreposcannerdev.azurecr.io/reposcanner:42. The pipeline passes the build's own tag.

.PARAMETER Lane
    nightly or ondemand: the job is job-scanner-<lane>-dev and its queue scan-<lane> (ADR-011).

.PARAMETER ResourceGroup
    The dev environment's resource group.

.PARAMETER Environment
    The Container Apps environment the job runs in.

.PARAMETER Registry
    The container registry the image is pulled from, as id-scanner.

.PARAMETER Identity
    The user-assigned identity the job runs as, pulls its image as, reads its queue as and reads the PAT as (ADR-014).

.PARAMETER KeyVault
    The Key Vault holding the PAT.

.PARAMETER SecretName
    The PAT's secret name, in Key Vault and in the job.

.PARAMETER StorageAccount
    The storage account holding the queues.

.PARAMETER OrganizationUrl
    The Azure DevOps organisation whose repositories are scanned.

.PARAMETER SqlServer
    The Azure SQL server holding the findings database.

.PARAMETER Database
    The findings database.

.EXAMPLE
    .\deploy\Deploy-ScannerJob.ps1 -Lane nightly -Image enqreposcannerdev.azurecr.io/reposcanner:0 -WhatIf

.EXAMPLE
    .\deploy\Deploy-ScannerJob.ps1 -Lane ondemand -Image enqreposcannerdev.azurecr.io/reposcanner:$(Build.BuildId)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Image,

    [Parameter(Mandatory)]
    [ValidateSet('nightly', 'ondemand')]
    [string] $Lane,

    [string] $ResourceGroup = 'RepoAnalysis-dev',
    [string] $Environment = 'code-reposcanner-dev',
    [string] $Registry = 'enqreposcannerdev',
    [string] $Identity = 'id-scanner',
    [string] $KeyVault = 'kv-reposcanner-dev',
    [string] $SecretName = 'azure-devops-pat',
    [string] $StorageAccount = 'strreposcannerdev',
    [string] $OrganizationUrl = 'https://dev.azure.com/BlacklightInternalsMo',
    [string] $SqlServer = 'sql-reposcanner-dev.database.windows.net',
    [string] $Database = 'RepoScanner'
)

$ErrorActionPreference = 'Stop'

# One job and one queue per lane, the environment in the job's name as in job-enqueuer-dev (design, Job settings)
$jobName = "job-scanner-$Lane-dev"
$queueName = "scan-$Lane"

# The job's settings (design, Job settings). The 3,600-second timeout is the 60-minute limit
# Scanner:Execution:MaxRunDurationMinutes is set under; no retries, since a failed message is retried by the queue and
# then poisoned (ADR-013); at most one execution at a time (ADR-012), polled every 30 seconds.
$replicaTimeoutSeconds = '3600'
$replicaRetryLimit = '0'
$minExecutions = '0'
$maxExecutions = '1'
$pollingIntervalSeconds = '30'
$cpu = '1.0'
$memory = '2Gi'

# Runs az and returns its exit code, standard output and standard error separately, so a caller can tell a missing
# job from any other failure. 'Continue' because Windows PowerShell 5.1 turns standard-error lines into terminating
# errors under 'Stop'.
function Invoke-AzCommand {
    param([string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    $lines = @(& az @Arguments --only-show-errors 2>&1)
    $exitCode = $LASTEXITCODE
    $errorLines = @($lines | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $outputLines = @($lines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = ($outputLines -join "`n").Trim()
        Error    = ($errorLines -join "`n").Trim()
    }
}

# Runs az and returns its standard output, or stops the script with the command's name and az's error
function Invoke-Az {
    param([string[]] $Arguments)
    $result = Invoke-AzCommand $Arguments
    if ($result.ExitCode -ne 0) {
        # The command's own words, up to its first option, such as "containerapp job create"
        $words = @()
        foreach ($argument in $Arguments) {
            if ($argument.StartsWith('-')) {
                break
            }
            $words += $argument
        }
        throw "az $($words -join ' ') failed: $($result.Error)"
    }
    return $result.Output
}

# The containerapp commands, and the scale rule's identity option, come from an Azure CLI extension; adding it here
# means the agent's own copy, or the lack of one, does not matter (design, Left to the builder)
if ($PSCmdlet.ShouldProcess('the Azure CLI', 'Add or upgrade the containerapp extension')) {
    Invoke-Az @('extension', 'add', '--name', 'containerapp', '--upgrade', '--yes') | Out-Null
}

# id-scanner runs the job, pulls its image, reads its queue and the PAT (ADR-014); its client ID tells
# DefaultAzureCredential, and the database connection, which identity to use
$identityJson = Invoke-Az @('identity', 'show', '--name', $Identity, '--resource-group', $ResourceGroup, '--query', '{id: id, clientId: clientId}', '--output', 'json')
$identityInfo = $identityJson | ConvertFrom-Json
$identityId = $identityInfo.id

$registryServer = "$Registry.azurecr.io"

# The image must already be in the registry. A new run with only a deploy stage ticked asks for an image its own
# build ID never built, and the job update would then fail on the missing manifest after the identity, registry and
# secret had been set again (the enqueuer's run 20261019.5). Stopping here changes nothing and says how to redeploy.
$registryPrefix = "$registryServer/"
if (-not $Image.StartsWith($registryPrefix)) {
    throw "$Image is not in ${Registry}: the job pulls its image only from $registryServer"
}
$repositoryImage = $Image.Substring($registryPrefix.Length)
$imageLookup = Invoke-AzCommand @('acr', 'repository', 'show', '--name', $Registry, '--image', $repositoryImage, '--query', 'digest', '--output', 'tsv')
if ($imageLookup.ExitCode -ne 0) {
    if ($imageLookup.Error -match 'the specified tag does not exist') {
        throw "$repositoryImage is not in $Registry, so this run did not build it. To redeploy an image, re-run the deploy stage of the run that built it."
    }
    throw "az acr repository show failed: $($imageLookup.Error)"
}

# The PAT as a Key Vault reference naming the secret without a version, so a renewed PAT is picked up without a
# deployment. The job never holds its value (design, Job settings).
$secret = "$SecretName=keyvaultref:https://$KeyVault.vault.azure.net/secrets/$SecretName,identityref:$identityId"

$environmentVariables = @(
    "AZURE_CLIENT_ID=$($identityInfo.clientId)",
    "Scanner__Queue__Name=$queueName",
    "Scanner__Queue__ServiceUri=https://$StorageAccount.queue.core.windows.net",
    "Scanner__AzureDevOps__OrganizationUrl=$OrganizationUrl",
    "Scanner__AzureDevOps__Pat=secretref:$SecretName",
    "Scanner__Database__ConnectionString=Server=tcp:$SqlServer,1433;Database=$Database;Authentication=Active Directory Managed Identity;User Id=$($identityInfo.clientId);Encrypt=True"
)

# An execution starts when the lane's queue holds a message, read as id-scanner, so the scale rule holds no
# connection string (design, Job settings)
$scaleRuleArguments = @(
    '--min-executions', $minExecutions,
    '--max-executions', $maxExecutions,
    '--polling-interval', $pollingIntervalSeconds,
    '--scale-rule-name', $queueName,
    '--scale-rule-type', 'azure-queue',
    '--scale-rule-metadata', "accountName=$StorageAccount", "queueName=$queueName", 'queueLength=1',
    '--scale-rule-identity', $identityId
)

# Only ARM's ResourceNotFound means the job is missing. A missing resource group (ResourceGroupNotFound), an expired
# sign-in or a missing role stops the script instead of trying to create a job that may exist (plan, Review Focus 2).
$lookup = Invoke-AzCommand @('containerapp', 'job', 'show', '--name', $jobName, '--resource-group', $ResourceGroup, '--query', 'id', '--output', 'tsv')
if ($lookup.ExitCode -eq 0) {
    $exists = $true
}
elseif ($lookup.Error -match '\(ResourceNotFound\)') {
    $exists = $false
}
else {
    throw "az containerapp job show failed: $($lookup.Error)"
}

$jobArguments = @('--name', $jobName, '--resource-group', $ResourceGroup)
$deployed = $false

if (-not $exists) {
    if ($PSCmdlet.ShouldProcess($jobName, 'Create')) {
        Invoke-Az (@('containerapp', 'job', 'create') + $jobArguments + @(
                '--environment', $Environment,
                '--trigger-type', 'Event',
                '--replica-timeout', $replicaTimeoutSeconds,
                '--replica-retry-limit', $replicaRetryLimit,
                '--parallelism', '1',
                '--replica-completion-count', '1') + $scaleRuleArguments + @(
                '--image', $Image,
                '--cpu', $cpu,
                '--memory', $memory,
                '--mi-user-assigned', $identityId,
                '--registry-server', $registryServer,
                '--registry-identity', $identityId,
                '--secrets', $secret,
                '--env-vars') + $environmentVariables) | Out-Null
        Write-Host "Created $jobName"
        $deployed = $true
    }
}
else {
    if ($PSCmdlet.ShouldProcess($jobName, 'Update')) {
        # Identity, registry and secret first, so the new image is pulled and started with them in place
        Invoke-Az (@('containerapp', 'job', 'identity', 'assign') + $jobArguments + @('--user-assigned', $identityId)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'registry', 'set') + $jobArguments + @('--server', $registryServer, '--identity', $identityId)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'secret', 'set') + $jobArguments + @('--secrets', $secret)) | Out-Null
        Invoke-Az (@('containerapp', 'job', 'update') + $jobArguments + @(
                '--image', $Image,
                '--replica-timeout', $replicaTimeoutSeconds,
                '--replica-retry-limit', $replicaRetryLimit) + $scaleRuleArguments + @(
                '--cpu', $cpu,
                '--memory', $memory,
                '--replace-env-vars') + $environmentVariables) | Out-Null
        Write-Host "Updated $jobName"
        $deployed = $true
    }
}

if ($deployed) {
    $summaryQuery = '{image: properties.template.containers[0].image, timeout: properties.configuration.replicaTimeout, retries: properties.configuration.replicaRetryLimit, maxExecutions: properties.configuration.eventTriggerConfig.scale.maxExecutions}'
    $summary = Invoke-Az (@('containerapp', 'job', 'show') + $jobArguments + @('--query', $summaryQuery, '--output', 'json')) | ConvertFrom-Json
    Write-Host "${jobName}: image $($summary.image), queue $queueName, timeout $($summary.timeout) s, retries $($summary.retries), max executions $($summary.maxExecutions), identity $Identity"
}
