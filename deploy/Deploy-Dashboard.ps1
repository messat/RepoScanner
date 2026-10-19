<#
.SYNOPSIS
    Deploys the dashboard to its App Service web app and puts its settings back (dashboard Issue 7, #97).

.DESCRIPTION
    Run by the pipeline's DeployDashboard stage, signed in as id-pipeline through sc-reposcanner-dev. Never run it by
    hand without -WhatIf: the web app's code and settings are changed only by the pipeline (design, decision 15).

    Checks Easy Auth first and deploys nothing unless it is on, requires sign-in, sends anyone not signed in to sign
    in, and signs in through id-dashboard as a federated credential rather than a client secret (decision 16), so the
    dashboard's code never goes live unprotected. Then:

        settings   WebSockets, Always On, FTPS, minimum TLS and the startup command; HTTPS only and ARR affinity; and
                   the three app settings, put back on every run. No app setting is deleted: Easy Auth's set-up adds
                   its own
        start      only when the app is not running
        deploy     the zip, which restarts the app

    Any failed check or az command stops the script with what was wrong. Holds no secret: the app reaches the
    database and the queue as id-dashboard. Exits 0 after a deployment.

.PARAMETER Package
    The zip of the published dashboard, with Dashboard.dll at its root. The pipeline passes the Build stage's artifact.

.PARAMETER ResourceGroup
    The dev environment's resource group.

.PARAMETER WebApp
    The dashboard's web app.

.PARAMETER Identity
    The user-assigned identity the app runs as and Easy Auth signs in through (ADR-014).

.PARAMETER SqlServer
    The Azure SQL server holding the scan database.

.PARAMETER Database
    The scan database.

.PARAMETER StorageAccount
    The storage account holding scan-ondemand.

.EXAMPLE
    .\deploy\Deploy-Dashboard.ps1 -Package .\dashboard.zip -WhatIf

.EXAMPLE
    .\deploy\Deploy-Dashboard.ps1 -Package $(Pipeline.Workspace)/dashboard/dashboard.zip
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Package,

    [string] $ResourceGroup = 'RepoAnalysis-dev',
    [string] $WebApp = 'app-reposcanner-dashboard-dev',
    [string] $Identity = 'id-dashboard',
    [string] $SqlServer = 'sql-reposcanner-dev.database.windows.net',
    [string] $Database = 'RepoScanner',
    [string] $StorageAccount = 'strreposcannerdev'
)

$ErrorActionPreference = 'Stop'

# The app setting Easy Auth reads id-dashboard's client ID from, in place of a client secret (decision 16)
$federatedCredentialSetting = 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'

# Runs az and returns its exit code, standard output and standard error separately. 'Continue' because Windows
# PowerShell 5.1 turns standard-error lines into terminating errors under 'Stop'.
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
        # The command's own words, up to its first option, such as "webapp config set"
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

if (-not (Test-Path -Path $Package -PathType Leaf)) {
    throw "$Package is not a file: pass the zip of the published dashboard"
}

$webAppArguments = @('--name', $WebApp, '--resource-group', $ResourceGroup)

# The web app first, so a wrong name, a missing resource group or a missing role stops the script before anything
$site = Invoke-Az (@('webapp', 'show') + $webAppArguments + @('--query', '{id: id, state: state}', '--output', 'json')) | ConvertFrom-Json

# id-dashboard's client ID tells DefaultAzureCredential, the database connection and Easy Auth which identity to use
$clientId = Invoke-Az @('identity', 'show', '--name', $Identity, '--resource-group', $ResourceGroup, '--query', 'clientId', '--output', 'tsv')

# Easy Auth, read from the v2 settings the portal writes. Each part missing is named, so the message says what to fix
$auth = (Invoke-Az @('rest', '--method', 'get', '--url', "https://management.azure.com$($site.id)/config/authsettingsV2?api-version=2023-12-01")) | ConvertFrom-Json
$problems = @()
if ($auth.properties.platform.enabled -ne $true) {
    $problems += 'Easy Auth is off'
}
if ($auth.properties.globalValidation.requireAuthentication -ne $true) {
    $problems += 'sign-in is not required'
}
if ($auth.properties.globalValidation.unauthenticatedClientAction -ne 'RedirectToLoginPage') {
    $problems += 'a request that is not signed in is not sent to sign in'
}
if ($null -eq $auth.properties.identityProviders.azureActiveDirectory) {
    $problems += 'the Microsoft provider is not set up'
}
elseif ($auth.properties.identityProviders.azureActiveDirectory.registration.clientSecretSettingName -ne $federatedCredentialSetting) {
    $problems += "the Microsoft provider's client secret setting name is not $federatedCredentialSetting"
}
# Only this one app setting's value is read: the others may hold what Easy Auth's set-up put there
$federatedClientId = Invoke-Az (@('webapp', 'config', 'appsettings', 'list') + $webAppArguments + @('--query', "[?name=='$federatedCredentialSetting'].value | [0]", '--output', 'tsv'))
if ($federatedClientId -ne $clientId) {
    $problems += "the app setting $federatedCredentialSetting is not $Identity's client ID"
}
if ($problems.Count -gt 0) {
    throw "Easy Auth is not set up on $WebApp as the README's Easy Auth steps say, so the dashboard was not deployed: $($problems -join '; ')"
}

# The settings the pipeline owns (design, Dashboard settings): put back on every run, so a change in the portal does
# not last (#97)
if ($PSCmdlet.ShouldProcess($WebApp, 'Apply settings')) {
    Invoke-Az (@('webapp', 'config', 'set') + $webAppArguments + @(
            '--web-sockets-enabled', 'true',
            '--always-on', 'true',
            '--ftps-state', 'Disabled',
            '--min-tls-version', '1.2',
            '--startup-file', 'dotnet Dashboard.dll')) | Out-Null
    # A patch of just these two properties: az webapp update writes the whole site back, App Service plan included,
    # and id-pipeline's Website Contributor role is on the web app alone (final review). The body goes in a file, as
    # Windows PowerShell 5.1 strips the quotes from JSON passed to a native command.
    $bodyFile = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($bodyFile, '{"properties":{"httpsOnly":true,"clientAffinityEnabled":true}}')
    try {
        Invoke-Az @('rest', '--method', 'patch', '--url', "https://management.azure.com$($site.id)?api-version=2023-12-01", '--body', "@$bodyFile") | Out-Null
    }
    finally {
        Remove-Item -Path $bodyFile -Force
    }
    Invoke-Az (@('webapp', 'config', 'appsettings', 'set') + $webAppArguments + @(
            '--settings',
            "AZURE_CLIENT_ID=$clientId",
            "Dashboard__Database__ConnectionString=Server=tcp:$SqlServer,1433;Database=$Database;Authentication=Active Directory Managed Identity;User Id=$clientId;Encrypt=True",
            "Dashboard__Queue__ServiceUri=https://$StorageAccount.queue.core.windows.net")) | Out-Null
}

# It was stopped until Easy Auth was on; once it runs, starting it again is not needed
if ($site.state -ne 'Running' -and $PSCmdlet.ShouldProcess($WebApp, 'Start')) {
    Invoke-Az (@('webapp', 'start') + $webAppArguments) | Out-Null
}

if ($PSCmdlet.ShouldProcess($WebApp, 'Deploy')) {
    Invoke-Az (@('webapp', 'deploy') + $webAppArguments + @('--src-path', $Package, '--type', 'zip')) | Out-Null
    $summary = Invoke-Az (@('webapp', 'show') + $webAppArguments + @('--query', '{state: state, httpsOnly: httpsOnly, affinity: clientAffinityEnabled}', '--output', 'json')) | ConvertFrom-Json
    $config = Invoke-Az (@('webapp', 'config', 'show') + $webAppArguments + @('--query', '{webSockets: webSocketsEnabled, alwaysOn: alwaysOn}', '--output', 'json')) | ConvertFrom-Json
    Write-Host "${WebApp}: deployed $(Split-Path -Leaf $Package) at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC; state $($summary.state), WebSockets $($config.webSockets), Always On $($config.alwaysOn), ARR affinity $($summary.affinity), HTTPS only $($summary.httpsOnly)"
}
