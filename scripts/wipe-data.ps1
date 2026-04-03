[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Force,
    [switch]$Restart,
    [string]$BaseUrl,
    [int]$WaitSeconds = 120
)

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

Assert-Tool -Name 'docker'
$projectRoot = Get-ProjectRoot -ScriptRoot $PSScriptRoot
$localDataPath = Join-Path $projectRoot 'data'

if (-not $Force) {
    $confirmation = Read-Host 'This will delete GAE Docker volumes and local data under .\data. Type WIPE to continue'
    if ($confirmation -ne 'WIPE') {
        Write-Host 'Wipe cancelled.'
        return
    }
}

if ($PSCmdlet.ShouldProcess('GAE persistent data', 'Remove docker compose volumes and local data')) {
    Write-Host 'Stopping stack and removing compose volumes...'
    Invoke-Compose -ProjectRoot $projectRoot -Arguments @('down', '-v', '--remove-orphans')

    Write-Host 'Clearing local data directory...'
    Clear-DirectoryContents -Path $localDataPath

    Write-Host 'Persistent data wiped.'
}

if ($Restart) {
    Write-Host 'Restarting stack on a clean state...'

    $restartArguments = @('-WaitSeconds', $WaitSeconds)
    if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) {
        $restartArguments += @('-BaseUrl', $BaseUrl)
    }

    & (Join-Path $PSScriptRoot 'reset-docker-stack.ps1') @restartArguments
}