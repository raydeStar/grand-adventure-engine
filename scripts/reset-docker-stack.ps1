[CmdletBinding()]
param(
    [string]$BaseUrl,
    [int]$GaePort,
    [switch]$NoCache,
    [switch]$SkipWait,
    [switch]$ReseedContent,
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

$resolvedGaePort = Resolve-AvailableTcpPort -PreferredPort $requestedGaePort -ServiceName 'GAE dashboard'

$BaseUrl = Resolve-BaseUrl -BaseUrl $BaseUrl -FallbackUrl "http://localhost:$resolvedGaePort"

$env:GAE_HOST_PORT = $resolvedGaePort.ToString()
$env:GAE_BASE_URL = $BaseUrl

Write-Host "Using GAE dashboard at $BaseUrl"

$buildArguments = @('build')
if ($NoCache) {
    $buildArguments += '--no-cache'
}
$buildArguments += 'gae'

Write-Host 'Rebuilding the gae service...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments $buildArguments

# If re-seeding content, start postgres first and clear the content_registry
if ($ReseedContent) {
    Write-Host 'Starting postgres for content re-seed...'
    Invoke-Compose -ProjectRoot $projectRoot -Arguments @('up', '-d', 'postgres')
    Write-Host 'Waiting for postgres to be healthy...'
    Start-Sleep -Seconds 5

    $pgContainer = docker compose -f (Join-Path $projectRoot 'docker-compose.yml') ps -q postgres 2>$null
    if ($pgContainer) {
        Write-Host 'Clearing content_registry to force re-seed on startup...'
        docker exec $pgContainer psql -U gae_app -d gae -c "DELETE FROM content_registry;" 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host 'Content registry cleared. New seed data will load on startup.'
        }
        else {
            Write-Warning 'Could not clear content_registry (table may not exist yet). Content will seed on first run.'
        }
    }
}

Write-Host 'Starting GAE in detached mode...'
Invoke-Compose -ProjectRoot $projectRoot -Arguments @('up', '-d', 'gae')

if (-not $SkipWait) {
    Write-Host "Waiting for $BaseUrl/health/live ..."
    Wait-ForHttpOk -Url "$BaseUrl/health/live" -TimeoutSeconds $WaitSeconds
}

# If re-seeding, also reset rooms after GAE is up
if ($ReseedContent) {
    Write-Host 'Re-seeding rooms via reset-world...'
    try {
        $session = New-DashboardSession -BaseUrl $BaseUrl `
            -Username ($env:GAE_DASHBOARD_ADMIN_USERNAME ?? 'admin') `
            -Password ($env:GAE_DASHBOARD_ADMIN_PASSWORD ?? 'GAE-Admin-Local!123')

        $null = Invoke-RestMethod `
            -Uri "$BaseUrl/api/dashboard/admin/reset-world" `
            -Method Post `
            -WebSession $session `
            -ContentType 'application/json' `
            -Body '{"keepPlayers":true}'

        Write-Host 'Rooms re-seeded successfully.'
    }
    catch {
        Write-Warning "Room re-seed failed: $($_.Exception.Message). You may need to manually reset rooms."
    }
}

Write-Host "Docker stack is up at $BaseUrl"
