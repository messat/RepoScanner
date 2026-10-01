<#
.SYNOPSIS
    Puts one scan request on a lane's queue in local Azurite, creating the lane and poison queues first if they
    are missing (Issue 7, #34).

.DESCRIPTION
    Builds the message in the format of the Issue 7 design (docs/design/2026-09-29-queue-consumer-and-execution-loop.md)
    and sends it as plain text, as the scanner reads it. Run the scanner afterwards to scan it:

        dotnet run --project src/RepoScanner.Scanner

    In Issue 7 the scanner finds the repository's code in the folder named RepositoryName inside
    Scanner:Source:LocalRoot. Needs the Azure CLI (az) and Azurite running.

.PARAMETER RepositoryName
    The folder to scan inside LocalRoot, such as victim-repo.

.PARAMETER Queue
    scan-nightly (the default) or scan-ondemand.

.PARAMETER RepositoryId
    The repository's Azure DevOps ID. By default a GUID made from the project and repository names, so running the
    script again for the same repository lands on the same Repositories row.

.PARAMETER Malformed
    Sends { "hello": 1 } instead, which the scanner moves straight to the lane's poison queue.

.EXAMPLE
    .\scripts\enqueue-local.ps1 -RepositoryName victim-repo

.EXAMPLE
    .\scripts\enqueue-local.ps1 -RepositoryName victim-repo -Queue scan-ondemand

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
    [string] $ProjectName = 'Lumina',

    [Parameter(ParameterSetName = 'Request')]
    [ValidateNotNullOrEmpty()]
    [string] $DefaultBranch = 'develop',

    [Parameter(ParameterSetName = 'Request')]
    [guid] $RepositoryId = [guid]::Empty,

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

    $request = [ordered]@{
        schemaVersion  = 1
        correlationId  = [guid]::NewGuid().ToString()
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
Write-Host 'Now run: dotnet run --project src/RepoScanner.Scanner'
