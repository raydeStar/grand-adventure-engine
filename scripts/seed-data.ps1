[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:8181',
    [string]$Username,
    [string]$Password,
    [switch]$ReplaceExisting
)

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

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