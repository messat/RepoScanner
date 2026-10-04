<#
.SYNOPSIS
    Puts one scan request on a lane's queue in local Azurite, creating the lane and poison queues first if they
    are missing (Issue 7, #34; Issue 8 defaults, #41).

.DESCRIPTION
    Builds the message in the format of the Issue 7 design (docs/design/2026-09-29-queue-consumer-and-execution-loop.md)
    and sends it as plain text, as the scanner reads it. Run the scanner afterwards to scan it, on this machine or in
    its container (README: Run in a container):

        dotnet run --project src/RepoScanner.Scanner
        docker rm reposcanner-scan
        docker run --name reposcanner-scan --env-file .env reposcanner:v1

    The scanner clones the repository from Scanner:AzureDevOps:OrganizationUrl, BlacklightInternalsMo in Development
    (Issue 8), so it needs the Scanner__AzureDevOps__Pat user environment variable. This script needs neither: it
    only needs the Azure CLI (az) and Azurite running.

.PARAMETER RepositoryName
    The repository to clone, such as victim-repo.

.PARAMETER Queue
    scan-nightly (the default) or scan-ondemand.

.PARAMETER ProjectName
    The Azure DevOps project the repository is in: Kudos Wall by default.

.PARAMETER DefaultBranch
    The branch to clone, without refs/heads/: main by default.

.PARAMETER RepositoryId
    The repository's Azure DevOps ID. By default a GUID made from the project and repository names, so running the
    script again for the same repository lands on the same Repositories row. To use the real one, as the enqueuer
    will in Azure:

        az repos show --org https://dev.azure.com/BlacklightInternalsMo --project "Kudos Wall" --repository victim-repo --query id -o tsv

.PARAMETER CorrelationId
    The message's correlation ID, which its scan run keeps. A new GUID by default; test-container.ps1 sets it to find
    the scan run (Issue 9).

.PARAMETER Malformed
    Sends { "hello": 1 } instead, which the scanner moves straight to the lane's poison queue.

.EXAMPLE
    .\scripts\enqueue-local.ps1 -RepositoryName victim-repo

.EXAMPLE
    .\scripts\enqueue-local.ps1 -RepositoryName victim-repo -Queue scan-ondemand

.EXAMPLE
    .\scripts\enqueue-local.ps1 -RepositoryName victim-repo -RepositoryId (az repos show --org https://dev.azure.com/BlacklightInternalsMo --project "Kudos Wall" --repository victim-repo --query id -o tsv)

.EXAMPLE
    .\scripts\enqueue-local.ps1 -Malformed
#>
[CmdletBinding(DefaultParameterSetName = 'Request')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Request')]
    [ValidateNotNullOrEmpty()]
    [string] $RepositoryName,

    [ValidateSet('scan-nightly', 'scan-ondemand')]
    [string] $Queue = 'scan-nightly',

    [Parameter(ParameterSetName = 'Request')]
    [ValidateNotNullOrEmpty()]
    # Where victim-repo is in BlacklightInternalsMo, the organisation Development clones from
    [string] $ProjectName = 'Kudos Wall',

    [Parameter(ParameterSetName = 'Request')]
    [ValidateNotNullOrEmpty()]
    [string] $DefaultBranch = 'main',

    [Parameter(ParameterSetName = 'Request')]
    [guid] $RepositoryId = [guid]::Empty,

    [Parameter(ParameterSetName = 'Request')]
    [guid] $CorrelationId = [guid]::Empty,

    [Parameter(Mandatory = $true, ParameterSetName = 'Malformed')]
    [switch] $Malformed,

    # Azurite on this machine. Never an Azure storage account: the script is for local testing only.
    [string] $ConnectionString = 'UseDevelopmentStorage=true'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'The Azure CLI (az) is needed: https://learn.microsoft.com/cli/azure/install-azure-cli'
}

# Runs az and stops the script if it fails. --only-show-errors hides az's "preview" notice for storage commands.
function Invoke-Az {
    param([string[]] $Arguments)

    $output = & az @Arguments --connection-string $ConnectionString --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments[0..2] -join ' ') failed. Is Azurite running? (azurite --location c:\azurite_data)"
    }
    return $output
}

# The four queues the scanner uses. Creating one that exists already does nothing. The scanner never creates them
# itself: in Azure it may only use them (Issue 7 design, decision 18).
foreach ($name in 'scan-nightly', 'scan-ondemand', 'scan-nightly-poison', 'scan-ondemand-poison') {
    Invoke-Az @('storage', 'queue', 'create', '--name', $name, '--output', 'none') | Out-Null
}

if ($Malformed) {
    $body = '{ "hello": 1 }'
}
else {
    if ($RepositoryId -eq [guid]::Empty) {
        # The same names always give the same ID: the first 16 bytes of the SHA-256 of "project/repository",
        # lower-cased, as --path makes an ID from a folder path
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(("$ProjectName/$RepositoryName").ToLowerInvariant()))
        }
        finally {
            $sha.Dispose()
        }
        $RepositoryId = [guid]::new([byte[]] $hash[0..15])
    }

    # A new one for each message unless one is given, as the enqueuer makes one per request
    if ($CorrelationId -eq [guid]::Empty) {
        $CorrelationId = [guid]::NewGuid()
    }

    $request = [ordered]@{
        schemaVersion  = 1
        correlationId  = $CorrelationId.ToString()
        repositoryId   = $RepositoryId.ToString()
        projectName    = $ProjectName
        repositoryName = $RepositoryName
        defaultBranch  = $DefaultBranch
    }
    $body = $request | ConvertTo-Json -Compress
}

# Sent from a file, so PowerShell and cmd cannot change the quotes in the JSON on the way to az. az sends the
# message as plain text, not Base64, which is what the scanner reads (checked with Azure CLI 2.87).
$file = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($file.FullName, $body, (New-Object System.Text.UTF8Encoding $false))
    Invoke-Az @('storage', 'message', 'put', '--queue-name', $Queue, '--content', "@$($file.FullName)", '--output', 'none') | Out-Null
}
finally {
    Remove-Item $file.FullName -ErrorAction SilentlyContinue
}

Write-Host "Added to ${Queue}: $body"
# Either way of running the scanner reads the same queue, so both are named (Issue 9)
Write-Host 'Now run the scanner: dotnet run --project src/RepoScanner.Scanner, or in its container: docker rm reposcanner-scan, then docker run --name reposcanner-scan --env-file .env reposcanner:v1 (README: Build and run)'
