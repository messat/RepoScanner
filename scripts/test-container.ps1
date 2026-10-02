<#
.SYNOPSIS
    Builds the scanner image and checks it, its connections and a real scan end to end (Issue 9, #59).

.DESCRIPTION
    Checks, in order, stopping at the first that fails:

        1-3    Docker is running, sqlcmd is installed, and .env is complete
        4-9    the image builds, runs as a non-root user, has git 2.31 or later, no .NET SDK and no
               Development settings, and its Dockerfile tags every base image with something other than latest
        10-11  a container reaches SQL Server on host.docker.internal:1433 and Azurite on host.docker.internal:10001
        12     a scan request for the repository is scanned to a Completed or PartiallyCompleted scan run
        13     a second run, on the now empty queue, exits 0 and saves no scan run

    Needs Docker Desktop, Azurite and SQL Server running, the Azure CLI (for enqueue-local.ps1), sqlcmd, and a
    complete .env (README: Run in a container). The request goes to the queue .env names, and messages already on
    that queue are scanned too. Never prints a value
    from .env. Exits 0 when every check passes, 1 at the first failure.

.PARAMETER RepositoryName
    The repository to scan, in Kudos Wall in BlacklightInternalsMo: victim-repo by default.

.PARAMETER Tag
    The image tag to build and run: v1 by default, giving reposcanner:v1.

.PARAMETER SqlServer
    The SQL Server to read the scan runs from, with Windows authentication: .\SQLEXPRESS by default.

.PARAMETER EnvFile
    The settings file passed to the container: .env at the repository root by default.

.EXAMPLE
    .\scripts\test-container.ps1

.EXAMPLE
    .\scripts\test-container.ps1 -RepositoryName victim-repo -Tag v1
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $RepositoryName = 'victim-repo',

    [ValidateNotNullOrEmpty()]
    [string] $Tag = 'v1',

    [ValidateNotNullOrEmpty()]
    [string] $SqlServer = '.\SQLEXPRESS',

    [string] $EnvFile = ''
)

$ErrorActionPreference = 'Stop'

# Found from the script's own folder, so the script works whichever folder it is started from
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($EnvFile)) {
    $EnvFile = Join-Path $root '.env'
}
$image = "reposcanner:$Tag"
$checkCount = 13
$script:passed = 0

# The settings the container needs (Issue 9 design, .env)
$requiredNames = @(
    'Scanner__Queue__Name',
    'Scanner__Queue__ConnectionString',
    'Scanner__AzureDevOps__OrganizationUrl',
    'Scanner__AzureDevOps__Pat',
    'Scanner__Database__ConnectionString'
)

function Write-Pass {
    param([string] $Name)
    $script:passed++
    Write-Host "PASS $Name" -ForegroundColor Green
}

# Stops the script: a later check would only fail for the same reason, or test something that is not there
function Stop-WithFailure {
    param([string] $Name, [string] $Reason)
    Write-Host "FAIL ${Name}: $Reason" -ForegroundColor Red
    Write-Host "$script:passed of $checkCount checks passed"
    exit 1
}

# Runs a native command and returns its standard output as one string, dropping standard error; the exit code is
# left in $LASTEXITCODE. 'Continue' because Windows PowerShell 5.1 turns redirected standard-error lines into
# terminating errors under 'Stop'.
function Invoke-Quiet {
    param([string] $FilePath, [string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    $output = & $FilePath @Arguments 2>$null
    return (($output | Out-String).Trim())
}

# Runs a native command with everything it prints shown, as for the build and the scanner's own log; the exit code
# is left in $LASTEXITCODE. 'Continue' for the same reason: docker writes its progress to standard error.
function Invoke-Shown {
    param([string] $FilePath, [string[]] $Arguments)
    $ErrorActionPreference = 'Continue'
    & $FilePath @Arguments | Out-Host
}

# One value from the RepoScanner database, read with Windows authentication, as dotnet ef reads it
function Invoke-Sql {
    param([string] $Query)
    return (Invoke-Quiet 'sqlcmd' @('-S', $SqlServer, '-E', '-C', '-d', 'RepoScanner', '-h', '-1', '-W', '-b', '-Q', "SET NOCOUNT ON; $Query"))
}

# 1
$check = 'Docker is running'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-WithFailure $check 'docker was not found. Install Docker Desktop (README: Prerequisites)'
}
Invoke-Quiet 'docker' @('version', '--format', '{{.Server.Version}}') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'Start Docker Desktop and try again'
}
Write-Pass $check

# 2
$check = 'sqlcmd is installed'
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Stop-WithFailure $check 'Install the SQL Server command-line tools, which include sqlcmd'
}
Write-Pass $check

# 3. Problems are named by line number and setting, never by value, since the file holds the PAT and a password.
$check = '.env is complete'
if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    Stop-WithFailure $check "there is no file at $EnvFile. Copy .env.example to .env and fill it in (README: Run in a container)"
}
# A full path, so .NET and docker read the file Test-Path found: Windows PowerShell 5.1 resolves a relative path
# for .NET from the folder the process started in, not the current one
$EnvFile = (Resolve-Path -LiteralPath $EnvFile).ProviderPath
$found = @{}
$queueName = ''
$lineNumber = 0
foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) {
    $lineNumber++
    # Docker skips blank lines and lines starting with #, so they are skipped here too
    if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith('#')) {
        continue
    }
    if ($line -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        Stop-WithFailure $check "line $lineNumber of $EnvFile is not NAME=value"
    }
    $name = $Matches[1]
    $value = $Matches[2]
    if ($value.Length -eq 0) {
        Stop-WithFailure $check "line $lineNumber, $name, is empty"
    }
    # Docker keeps quotes as part of the value, so a quoted queue name or password would not work
    if ($value.StartsWith('"') -or $value.StartsWith("'")) {
        Stop-WithFailure $check "line $lineNumber, $name, is in quotes. Docker keeps them as part of the value: remove them"
    }
    # Docker keeps spaces too, so a PAT pasted with a trailing space would be refused as if it were wrong
    if ($value -ne $value.Trim()) {
        Stop-WithFailure $check "line $lineNumber, $name, starts or ends with a space. Docker keeps it as part of the value: remove it"
    }
    # A line copied from .env.example and not filled in
    if ($value -match '<[^<>]+>') {
        Stop-WithFailure $check "line $lineNumber, $name, still has a placeholder: replace the <...> with the real value"
    }
    # The queue name is not a secret: check 12 sends its message to the queue the container reads
    if ($name -eq 'Scanner__Queue__Name') {
        if ($value -ne 'scan-nightly' -and $value -ne 'scan-ondemand') {
            Stop-WithFailure $check "line $lineNumber, $name, must be scan-nightly or scan-ondemand"
        }
        $queueName = $value
    }
    $found[$name] = $true
}
foreach ($name in $requiredNames) {
    if (-not $found.ContainsKey($name)) {
        Stop-WithFailure $check "$name is missing (see .env.example)"
    }
}
Write-Pass $check

# 4
$check = 'Image builds'
Invoke-Shown 'docker' @('build', '-t', $image, $root)
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check "docker build exited ${LASTEXITCODE}: see its output above"
}
Write-Pass "$check ($image)"

# 5
$check = 'Runs as a non-root user'
$userId = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'id', $image, '-u')
if ($LASTEXITCODE -ne 0 -or $userId -eq '0') {
    Stop-WithFailure $check 'the image runs as root: check USER $APP_UID in the Dockerfile'
}
Write-Pass "$check (user $userId)"

# 6. GIT_CONFIG_COUNT, which gives git the PAT, needs 2.31 (Issue 8 design, decision 1)
$check = 'Git 2.31 or later'
$gitVersion = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'git', $image, '--version')
if ($gitVersion -notmatch '^git version (\d+)\.(\d+)') {
    Stop-WithFailure $check "git did not report a version: '$gitVersion'"
}
if ([version]"$($Matches[1]).$($Matches[2])" -lt [version]'2.31') {
    Stop-WithFailure $check "found $gitVersion"
}
Write-Pass "$check ($gitVersion)"

# 7
$check = 'No .NET SDK in the image'
$sdks = Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'dotnet', $image, '--list-sdks')
if ($LASTEXITCODE -ne 0 -or $sdks.Length -gt 0) {
    Stop-WithFailure $check "found '$sdks': the final stage must be runtime:10.0"
}
Write-Pass $check

# 8
$check = 'No appsettings.Development.json in the image'
Invoke-Quiet 'docker' @('run', '--rm', '--entrypoint', 'sh', $image, '-c', 'test ! -e /app/appsettings.Development.json') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'the file is in /app: check CopyToPublishDirectory="Never" in the scanner''s .csproj'
}
Write-Pass $check

# 9. A FROM naming an earlier stage, such as FROM build, is not an image and needs no tag.
$check = 'Base images are tagged'
$stages = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'Dockerfile')) {
    if ($line -notmatch '^\s*FROM\s+(?:--\S+\s+)*(\S+)(?:\s+AS\s+(\S+))?') {
        continue
    }
    $reference = $Matches[1]
    if ($Matches[2]) {
        $stages[$Matches[2]] = $true
    }
    if ($stages.ContainsKey($reference) -and $reference -ne $Matches[2]) {
        continue
    }
    # The tag is after the last colon, as long as no / follows it (a registry's port is not a tag)
    if ($reference -notmatch ':([^/:]+)$' -or $Matches[1] -eq 'latest') {
        Stop-WithFailure $check "'$($line.Trim())' must name a tag other than latest (ADR-005)"
    }
}
Write-Pass $check

# 10 and 11. busybox is pinned, as every image here is.
$check = 'SQL Server reachable from a container'
Invoke-Quiet 'docker' @('run', '--rm', 'busybox:1.37', 'nc', '-z', '-w', '5', 'host.docker.internal', '1433') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'start the SQL Server (SQLEXPRESS) service, and check TCP/IP is on port 1433 (README: Database and migrations)'
}
Write-Pass "$check (host.docker.internal:1433)"

$check = 'Azurite reachable from a container'
Invoke-Quiet 'docker' @('run', '--rm', 'busybox:1.37', 'nc', '-z', '-w', '5', 'host.docker.internal', '10001') | Out-Null
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check 'start Azurite: azurite --location c:\azurite_data'
}
Write-Pass "$check (host.docker.internal:10001)"

# 12. The correlation ID is set here, so the scan run for this message can be found among any others.
$check = 'A message is scanned'
$correlationId = [guid]::NewGuid()
try {
    & (Join-Path $PSScriptRoot 'enqueue-local.ps1') -RepositoryName $RepositoryName -Queue $queueName -CorrelationId $correlationId | Out-Host
}
catch {
    Stop-WithFailure $check "enqueue-local.ps1 failed: $($_.Exception.Message)"
}
Invoke-Shown 'docker' @('run', '--rm', '--env-file', $EnvFile, $image)
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check "the scanner exited ${LASTEXITCODE}: see its log above (README: Exit codes)"
}
$scanRun = Invoke-Sql "SELECT Status + '|' + ISNULL(CommitSha, '') + '|' + ISNULL(ErrorMessage, '') FROM ScanRuns WHERE CorrelationId = '$correlationId'"
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check "sqlcmd could not read ScanRuns on $SqlServer"
}
if ($scanRun.Length -eq 0) {
    Stop-WithFailure $check "no scan run has correlation ID $correlationId"
}
$status, $commitSha, $errorMessage = $scanRun -split '\|', 3
if (($status -ne 'Completed' -and $status -ne 'PartiallyCompleted') -or $commitSha -notmatch '^[0-9a-f]{40}$') {
    Stop-WithFailure $check "the scan run is $status, commit '$commitSha': $errorMessage"
}
Write-Pass "$check ($RepositoryName $status at $($commitSha.Substring(0, 7)))"

# 13. Check 12 left the queue empty, since the scanner takes every message before it exits.
$check = 'An empty queue exits 0 with no new scan run'
$before = Invoke-Sql 'SELECT COUNT(*) FROM ScanRuns'
# Two failed reads would both be empty and compare equal, so each count must have been read
if ($LASTEXITCODE -ne 0 -or $before -notmatch '^\d+$') {
    Stop-WithFailure $check "sqlcmd could not count ScanRuns on $SqlServer"
}
Invoke-Shown 'docker' @('run', '--rm', '--env-file', $EnvFile, $image)
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure $check "the scanner exited ${LASTEXITCODE}: see its log above"
}
$after = Invoke-Sql 'SELECT COUNT(*) FROM ScanRuns'
if ($LASTEXITCODE -ne 0 -or $after -notmatch '^\d+$') {
    Stop-WithFailure $check "sqlcmd could not count ScanRuns on $SqlServer"
}
if ($before -ne $after) {
    Stop-WithFailure $check "ScanRuns went from $before to $after rows"
}
Write-Pass $check

Write-Host "All $checkCount checks passed: $image scanned $RepositoryName to a $status scan run at $commitSha" -ForegroundColor Green
exit 0
