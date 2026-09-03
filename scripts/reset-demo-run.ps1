[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:8282',
    [ValidateRange(15, 300)]
    [int]$NarrationTimeoutSeconds = 120,
    [switch]$SkipNarratorHealthCheck,
    [switch]$NoClipboard
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'gae-ops.ps1')

# Rejects non-loopback targets because this rehearsal helper deliberately reads local login hints.
function Assert-LocalDemoTarget {
    param(
        [Parameter(Mandatory)]
        [string]$Url
    )

    $uri = [Uri]$Url
    if ($uri.Scheme -notin @('http', 'https')) {
        throw "Demo URL must use HTTP or HTTPS. Received '$Url'."
    }

    if (-not $uri.IsLoopback) {
        throw "This reset command is localhost-only; refusing to modify '$Url'."
    }
}

# Sends JSON to an authenticated dashboard endpoint and returns its decoded response.
function Invoke-DashboardPost {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Body,

        [Parameter(Mandatory)]
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,

        [hashtable]$Headers = @{}
    )

    $request = @{
        Uri         = "$script:DemoBaseUrl$Path"
        Method      = 'Post'
        WebSession  = $Session
        ContentType = 'application/json'
        Body        = ($Body | ConvertTo-Json -Depth 10)
    }
    if ($Headers.Count -gt 0) {
        $request.Headers = $Headers
    }

    return Invoke-RestMethod @request
}

# Reads an authenticated dashboard endpoint and returns its decoded response.
function Invoke-DashboardGet {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session
    )

    return Invoke-RestMethod -Uri "$script:DemoBaseUrl$Path" -WebSession $Session
}

# Finds the local admin credentials without ever printing the password.
function Get-LocalAdminCredential {
    param(
        [Parameter(Mandatory)]
        [object]$LoginOptions
    )

    $account = @($LoginOptions.accounts | Where-Object { $_.role -eq 'admin' }) | Select-Object -First 1
    $usernameProperty = if ($null -ne $account) { $account.PSObject.Properties['username'] } else { $null }
    $passwordProperty = if ($null -ne $account) { $account.PSObject.Properties['password'] } else { $null }
    $username = if ($null -ne $usernameProperty -and -not [string]::IsNullOrWhiteSpace([string]$usernameProperty.Value)) {
        [string]$usernameProperty.Value
    }
    else {
        [Environment]::GetEnvironmentVariable('GAE_DASHBOARD_ADMIN_USERNAME')
    }
    $password = if ($null -ne $passwordProperty -and -not [string]::IsNullOrWhiteSpace([string]$passwordProperty.Value)) {
        [string]$passwordProperty.Value
    }
    else {
        [Environment]::GetEnvironmentVariable('GAE_DASHBOARD_ADMIN_PASSWORD')
    }

    if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrWhiteSpace($password)) {
        throw 'Local admin credentials are unavailable. Enable local login hints or set GAE_DASHBOARD_ADMIN_USERNAME and GAE_DASHBOARD_ADMIN_PASSWORD.'
    }

    return [pscustomobject]@{
        Username = $username
        Password = $password
    }
}

# Waits for the prepared opening beat to appear in Player Flow before declaring the scene ready.
function Wait-ForPreparedNarration {
    param(
        [Parameter(Mandatory)]
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,

        [Parameter(Mandatory)]
        [string]$ExpectedNarration,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $story = @(Invoke-DashboardGet -Path '/api/dashboard/story?playerId=demo-user&limit=10' -Session $Session)
        $opening = $story | Where-Object { $_.narration -eq $ExpectedNarration } | Select-Object -First 1

        if ($null -ne $opening) {
            return $opening
        }

        Start-Sleep -Seconds 2
    }
    while ((Get-Date) -lt $deadline)

    throw "The prepared opening narration did not reach Player Flow within $TimeoutSeconds seconds. The state was reset, but the recording scene is not ready."
}

# Confirms the stable rehearsal contract after the intentionally random opening attack.
function Assert-DemoState {
    param(
        [Parameter(Mandatory)]
        [object]$Ari,

        [Parameter(Mandatory)]
        [object]$Marshal,

        [Parameter(Mandatory)]
        [object]$Room,

        [Parameter(Mandatory)]
        [object]$CoDmAction
    )

    if ($Ari.id -ne 'demo-user' -or $Ari.currentRoomId -ne 'moonfall_beast_pavilion' -or [int]$Ari.hp -ne 12) {
        throw 'Ari did not reach the expected wounded pavilion state.'
    }
    if ([int]$Ari.interaction.mode -ne 2) {
        throw 'Ari is not in combat; the centerpiece appears to have escaped the velvet upholstery.'
    }

    $velvetMaw = @($Room.npcs | Where-Object { $_.id -eq 'velvet_mimic' }) | Select-Object -First 1
    if ($null -eq $velvetMaw -or [int]$velvetMaw.hp -le 0) {
        throw 'The Velvet Maw is missing or defeated, so the hot-open combat is unavailable.'
    }

    $status = @($Marshal.statusEffects | Where-Object { $_.name -eq 'Divinely Singed' }) | Select-Object -First 1
    if ($Marshal.currentRoomId -ne 'spawn' -or $null -eq $status) {
        throw 'Marshal Vale is missing the approved divine singe at the tavern.'
    }
    if ($CoDmAction.status -ne 'approved') {
        throw 'The prepared Co-DM intervention was not approved.'
    }

    return $velvetMaw
}

$script:DemoBaseUrl = $BaseUrl.TrimEnd('/')
Assert-LocalDemoTarget -Url $script:DemoBaseUrl

Write-GaeBanner
Write-Host "Preparing the repeatable recording scene at $script:DemoBaseUrl ..." -ForegroundColor Cyan

$null = Invoke-RestMethod -Uri "$script:DemoBaseUrl/health/live" -TimeoutSec 10
$loginOptions = Invoke-RestMethod -Uri "$script:DemoBaseUrl/api/dashboard/auth/options" -TimeoutSec 10
$credential = Get-LocalAdminCredential -LoginOptions $loginOptions
$session = New-DashboardSession -BaseUrl $script:DemoBaseUrl -Username $credential.Username -Password $credential.Password

$health = Invoke-DashboardGet -Path '/api/dashboard/health' -Session $session
$narratorHealth = $health.'health/narrator'
if (-not $SkipNarratorHealthCheck -and ($null -eq $narratorHealth -or -not [bool]$narratorHealth.ok)) {
    $errorProperty = if ($null -ne $narratorHealth) { $narratorHealth.PSObject.Properties['error'] } else { $null }
    $detail = if ($null -ne $errorProperty) { [string]$errorProperty.Value } else { 'Narrator reported degraded health.' }
    throw "The AI narrator is not healthy at $script:DemoBaseUrl. $detail"
}

Write-Host 'Resetting only demo-user and demo-admin...' -ForegroundColor Green
$null = Invoke-DashboardPost -Path '/api/dashboard/admin/seed-demo' -Body @{ replaceExisting = $true } -Session $session

$null = Invoke-DashboardPost -Path '/api/dashboard/admin/mutations/teleport' -Body @{
    playerId          = 'demo-user'
    roomId            = 'moonfall_beast_pavilion'
    createRoomIfMissing = $false
    connectFromCurrentRoom = $false
} -Session $session

$null = Invoke-DashboardPost -Path '/api/dashboard/admin/mutations/resources' -Body @{
    playerId = 'demo-user'
    setHp     = 12
} -Session $session

Write-Host 'Starting Ari in live combat and placing the prepared opening beat...' -ForegroundColor Green
$null = Invoke-DashboardPost -Path '/api/dashboard/action' -Body @{
    playerId = 'demo-user'
    command  = 'attack the velvet maw'
} -Session $session

$openingNarration = "Ari Quickstep faces The Velvet Maw beneath the pavilion's swaying lanterns. The settee shudders, belches caramel-scented steam, and gathers itself for another deeply upholstered act of violence."
$openingMessageAction = Invoke-DashboardPost -Path '/api/dashboard/admin/co-dm/messages' -Headers @{ 'X-GAE-Request' = 'co-dm' } -Body @{
    requestId = "demo-opening-$([Guid]::NewGuid().ToString('N'))"
    playerId  = 'demo-user'
    message   = $openingNarration
    delivery  = 'player_flow'
} -Session $session

# Combat dice remain genuine, but Ari's opening health is pinned so every recording starts legibly wounded.
$null = Invoke-DashboardPost -Path '/api/dashboard/admin/mutations/resources' -Body @{
    playerId = 'demo-user'
    setHp     = 12
} -Session $session

$tokenBytes = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($tokenBytes)
}
finally {
    $random.Dispose()
}
$approvalToken = [BitConverter]::ToString($tokenBytes).Replace('-', '')
$proposal = Invoke-DashboardPost -Path '/api/dashboard/admin/co-dm/proposals' -Headers @{ 'X-GAE-Request' = 'co-dm' } -Body @{
    requestId         = "demo-reset-$([Guid]::NewGuid().ToString('N'))"
    approvalToken     = $approvalToken
    playerId          = 'demo-admin'
    kind              = 'apply_status'
    title             = 'Divinely singe Marshal Vale'
    rationale         = 'Leave a memorable approved intervention in the multi-player demo feed.'
    evidenceIds       = @('demo-admin', 'spawn')
    statusName        = 'Divinely Singed'
    statusDescription = 'One eyebrow smolders faintly with conspicuously divine disapproval.'
    durationTurns      = 3
    message            = 'Marshal Vale, a needle-thin ray of divine judgment singes one immaculate eyebrow. The heavens decline to explain themselves.'
} -Session $session

$approvedAction = Invoke-DashboardPost -Path "/api/dashboard/admin/co-dm/actions/$($proposal.id)/approve" -Headers @{ 'X-GAE-Request' = 'co-dm' } -Body @{
    approvalToken = $approvalToken
} -Session $session

$null = Wait-ForPreparedNarration -Session $session -ExpectedNarration $openingNarration -TimeoutSeconds $NarrationTimeoutSeconds
$ari = Invoke-DashboardGet -Path '/api/dashboard/players/demo-user' -Session $session
$marshal = Invoke-DashboardGet -Path '/api/dashboard/players/demo-admin' -Session $session
$room = Invoke-DashboardGet -Path '/api/dashboard/rooms/moonfall_beast_pavilion?playerId=demo-user' -Session $session
$velvetMaw = Assert-DemoState -Ari $ari -Marshal $marshal -Room $room -CoDmAction $approvedAction
if ($openingMessageAction.status -ne 'completed') {
    throw 'The prepared Player Flow opening did not complete.'
}

if (-not $NoClipboard) {
    Set-Clipboard -Value $credential.Password
}

Write-Host ''
Write-Host 'DEMO READY' -ForegroundColor Green
Write-Host "  Player:  Ari Quickstep - $($ari.hp)/$($ari.maxHp) HP, in combat with The Velvet Maw ($($velvetMaw.hp)/$($velvetMaw.maxHp) HP)"
Write-Host "  Co-DM:   Marshal Vale - Divinely Singed, approved action $($approvedAction.id)"
Write-Host "  Narrator: $($narratorHealth.service) - $($narratorHealth.status)"
Write-Host "  Open:     $script:DemoBaseUrl"
if (-not $NoClipboard) {
    Write-Host "  Login:    $($credential.Username) (password copied to clipboard)"
}
else {
    Write-Host "  Login:    $($credential.Username)"
}
Write-Host ''
Write-Host "Opening narration: $openingNarration" -ForegroundColor DarkGreen
Write-Host ''
Write-Host 'Sir Thaddeus: The stage is reset; the dice retain just enough liberty to keep the actors nervous.' -ForegroundColor DarkYellow
