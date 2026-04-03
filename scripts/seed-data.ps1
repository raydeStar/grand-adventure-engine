[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Username,
    [string]$Password,
    [switch]$ReplaceExisting
)

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

$BaseUrl = Resolve-BaseUrl -BaseUrl $BaseUrl -FallbackUrl (Get-ConfiguredBaseUrl -EnvironmentVariableName 'GAE_BASE_URL' -FallbackUrl "http://localhost:$(Get-ConfiguredPort -EnvironmentVariableName 'GAE_HOST_PORT' -Fallback 8181)")
$Username = Get-DefaultValue -Value $Username -Fallback (Get-DefaultValue -Value $env:GAE_DASHBOARD_ADMIN_USERNAME -Fallback 'admin')
$Password = Get-DefaultValue -Value $Password -Fallback (Get-DefaultValue -Value $env:GAE_DASHBOARD_ADMIN_PASSWORD -Fallback 'GAE-Admin-Local!123')

Write-Host "Logging into $BaseUrl as $Username ..."
$session = New-DashboardSession -BaseUrl $BaseUrl -Username $Username -Password $Password

$body = @{ replaceExisting = [bool]$ReplaceExisting } | ConvertTo-Json
$result = Invoke-RestMethod `
    -Uri "$BaseUrl/api/dashboard/admin/seed-demo" `
    -Method Post `
    -WebSession $session `
    -ContentType 'application/json' `
    -Body $body

Write-Host "Seed request complete. Created $($result.createdCount) demo characters."
$result.players |
    Select-Object id, name, race, class, level, currentRoomId |
    Format-Table -AutoSize