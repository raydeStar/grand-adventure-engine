[CmdletBinding()]
param(
    [string]$BaseUrl,
    [int]$GaePort,
    [int]$WikiPort,
    [switch]$NoCache,
    [switch]$SkipWait,
    [int]$WaitSeconds = 120
)

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

Assert-Tool -Name 'docker'
$projectRoot = Get-ProjectRoot -ScriptRoot $PSScriptRoot

# Tear down first — this frees our own ports before we probe availability.
Write-Host 'Stopping docker compose stack while preserving data volumes...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments @('down', '--remove-orphans')

$requestedGaePort = if ($PSBoundParameters.ContainsKey('GaePort')) {
    $GaePort
}
else {
    Get-ConfiguredPort -EnvironmentVariableName 'GAE_HOST_PORT' -Fallback 8181
}

$requestedWikiPort = if ($PSBoundParameters.ContainsKey('WikiPort')) {
    $WikiPort
}
else {
    Get-ConfiguredPort -EnvironmentVariableName 'WIKIJS_HOST_PORT' -Fallback 3000
}

$resolvedGaePort = Resolve-AvailableTcpPort -PreferredPort $requestedGaePort -ServiceName 'GAE dashboard'
$resolvedWikiPort = Resolve-AvailableTcpPort -PreferredPort $requestedWikiPort -ServiceName 'Wiki.js'

$BaseUrl = Resolve-BaseUrl -BaseUrl $BaseUrl -FallbackUrl "http://localhost:$resolvedGaePort"
$wikiBaseUrl = "http://localhost:$resolvedWikiPort"

$env:GAE_HOST_PORT = $resolvedGaePort.ToString()
$env:WIKIJS_HOST_PORT = $resolvedWikiPort.ToString()
$env:GAE_BASE_URL = $BaseUrl
$env:WIKIJS_BASE_URL = $wikiBaseUrl

Write-Host "Using GAE dashboard at $BaseUrl"
Write-Host "Using Wiki.js at $wikiBaseUrl"

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
Write-Host "Wiki.js is available at $wikiBaseUrl"