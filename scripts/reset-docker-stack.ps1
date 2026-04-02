[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:8181',
    [switch]$NoCache,
    [switch]$SkipWait,
    [int]$WaitSeconds = 120
)

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

Assert-Tool -Name 'docker'
$projectRoot = Get-ProjectRoot -ScriptRoot $PSScriptRoot

Write-Host 'Stopping docker compose stack while preserving data volumes...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments @('down', '--remove-orphans')

$buildArguments = @('build')
if ($NoCache) {
    $buildArguments += '--no-cache'
}
$buildArguments += 'gae'

Write-Host 'Rebuilding the gae service...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments $buildArguments

Write-Host 'Starting Wiki.js and GAE in detached mode...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments @('up', '-d', 'wikijs', 'gae')

if (-not $SkipWait) {
    Write-Host "Waiting for $BaseUrl/health/live ..."
    Wait-ForHttpOk -Url "$BaseUrl/health/live" -TimeoutSeconds $WaitSeconds
}

Write-Host "Docker stack is up at $BaseUrl"