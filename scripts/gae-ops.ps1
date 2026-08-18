Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-GaeBanner {
    Write-Host '  ____                     _      _       _                 ' -ForegroundColor Green
    Write-Host ' / ___|_ __ __ _ _ __   __| |    / \   __| |_   _____ _ __ ' -ForegroundColor Green
    Write-Host '| |  _| ''__/ _` | ''_ \ / _` |   / _ \ / _` \ \ / / _ \ ''__|' -ForegroundColor Green
    Write-Host '| |_| | | | (_| | | | | (_| |  / ___ \ (_| |\ V /  __/ |   ' -ForegroundColor Green
    Write-Host ' \____|_|  \__,_|_| |_|\__,_| /_/   \_\__,_| \_/ \___|_|   ' -ForegroundColor Green
    Write-Host 'Sir Thaddeus: Checking the wards before anyone touches the dramatic lever.' -ForegroundColor DarkYellow
}

function Get-ProjectRoot {
    param(
        [Parameter(Mandatory)]
        [string]$ScriptRoot
    )

    return (Resolve-Path (Join-Path $ScriptRoot '..')).Path
}

function Assert-Tool {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' was not found in PATH."
    }
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Push-Location $ProjectRoot
    try {
        & docker compose @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-ConfiguredPort {
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentVariableName,

        [Parameter(Mandatory)]
        [int]$Fallback
    )

    $rawValue = [Environment]::GetEnvironmentVariable($EnvironmentVariableName)
    if ([string]::IsNullOrWhiteSpace($rawValue)) {
        return $Fallback
    }

    $parsedValue = 0
    $parsedSuccessfully = [int]::TryParse($rawValue.Trim(), [ref]$parsedValue)
    if (-not $parsedSuccessfully -or $parsedValue -lt 1 -or $parsedValue -gt 65535) {
        throw "Environment variable '$EnvironmentVariableName' must be a valid TCP port. Current value: '$rawValue'."
    }

    return $parsedValue
}

function Get-ConfiguredBaseUrl {
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentVariableName,

        [Parameter(Mandatory)]
        [string]$FallbackUrl
    )

    $rawValue = [Environment]::GetEnvironmentVariable($EnvironmentVariableName)
    if ([string]::IsNullOrWhiteSpace($rawValue)) {
        return $FallbackUrl.TrimEnd('/')
    }

    return $rawValue.TrimEnd('/')
}

function Test-TcpPortListening {
    param(
        [Parameter(Mandatory)]
        [int]$Port
    )

    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    return @($listeners | Where-Object { $_.Port -eq $Port }).Count -gt 0
}

function Get-TcpPortOwnerSummary {
    param(
        [Parameter(Mandatory)]
        [int]$Port
    )

    $netTcpCommand = Get-Command 'Get-NetTCPConnection' -ErrorAction SilentlyContinue
    if ($null -eq $netTcpCommand) {
        return 'another process'
    }

    $connections = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Sort-Object OwningProcess -Unique)
    if ($connections.Count -eq 0) {
        return 'another process'
    }

    $owners = foreach ($connection in $connections) {
        $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            "PID $($connection.OwningProcess)"
        }
        else {
            "$($process.ProcessName) (PID $($process.Id))"
        }
    }

    return ($owners -join ', ')
}

function Resolve-AvailableTcpPort {
    param(
        [Parameter(Mandatory)]
        [int]$PreferredPort,

        [Parameter(Mandatory)]
        [string]$ServiceName,

        [int]$MaxOffset = 20
    )

    for ($candidatePort = $PreferredPort; $candidatePort -le ($PreferredPort + $MaxOffset); $candidatePort++) {
        if (-not (Test-TcpPortListening -Port $candidatePort)) {
            if ($candidatePort -ne $PreferredPort) {
                $owners = Get-TcpPortOwnerSummary -Port $PreferredPort
                Write-Warning "$ServiceName host port $PreferredPort is already in use by $owners. Using $candidatePort instead."
            }

            return $candidatePort
        }
    }

    throw "Could not find a free host port for $ServiceName in range $PreferredPort-$($PreferredPort + $MaxOffset)."
}

function Resolve-BaseUrl {
    param(
        [AllowNull()]
        [string]$BaseUrl,

        [Parameter(Mandatory)]
        [string]$FallbackUrl
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        return $FallbackUrl.TrimEnd('/')
    }

    return $BaseUrl.TrimEnd('/')
}

function Wait-ForHttpOk {
    param(
        [Parameter(Mandatory)]
        [string]$Url,

        [int]$TimeoutSeconds = 120,

        [int]$PollIntervalSeconds = 2
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 10
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return
            }
        }
        catch {
            Start-Sleep -Seconds $PollIntervalSeconds
            continue
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }
    while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Url."
}

function Get-DefaultValue {
    param(
        [AllowNull()]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$Fallback
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Fallback
    }

    return $Value.Trim()
}

function New-DashboardSession {
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,

        [Parameter(Mandatory)]
        [string]$Username,

        [Parameter(Mandatory)]
        [string]$Password
    )

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = @{
        username   = $Username
        password   = $Password
        rememberMe = $false
    } | ConvertTo-Json

    $null = Invoke-RestMethod `
        -Uri "$BaseUrl/api/dashboard/auth/login" `
        -Method Post `
        -WebSession $session `
        -ContentType 'application/json' `
        -Body $body

    return $session
}

function Clear-DirectoryContents {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
        return
    }

    Get-ChildItem -LiteralPath $Path -Force | Remove-Item -Recurse -Force
}

function Get-ComposeResolvedConfig {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    Push-Location $ProjectRoot
    try {
        $json = & docker compose config --format json
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose config failed with exit code $LASTEXITCODE. No containers were changed."
        }

        return ($json | ConvertFrom-Json)
    }
    finally {
        Pop-Location
    }
}

function Get-ComposeServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [object]$ComposeConfig,

        [Parameter(Mandatory)]
        [string]$ServiceName,

        [Parameter(Mandatory)]
        [string]$VariableName
    )

    $service = $ComposeConfig.services.PSObject.Properties[$ServiceName].Value
    if ($null -eq $service) {
        throw "Compose service '$ServiceName' was not found."
    }

    $property = $service.environment.PSObject.Properties[$VariableName]
    if ($null -eq $property) {
        return $null
    }

    return [string]$property.Value
}

function Assert-ProductionDashboardSecrets {
    param(
        [Parameter(Mandatory)]
        [object]$ComposeConfig
    )

    $userPassword = Get-ComposeServiceEnvironmentValue -ComposeConfig $ComposeConfig -ServiceName 'gae' -VariableName 'DashboardAuth__User__Password'
    $adminPassword = Get-ComposeServiceEnvironmentValue -ComposeConfig $ComposeConfig -ServiceName 'gae' -VariableName 'DashboardAuth__Admin__Password'
    $knownDefaults = @('GAE-User-Local!123', 'GAE-Admin-Local!123')

    foreach ($credential in @(
        @{ Name = 'GAE_DASHBOARD_USER_PASSWORD'; Value = $userPassword },
        @{ Name = 'GAE_DASHBOARD_ADMIN_PASSWORD'; Value = $adminPassword }
    )) {
        if ([string]::IsNullOrWhiteSpace($credential.Value) -or $credential.Value.Length -lt 12) {
            throw "$($credential.Name) must be a unique password of at least 12 characters. No containers were changed."
        }

        if ($knownDefaults -contains $credential.Value) {
            throw "$($credential.Name) still uses a published demo password. No containers were changed."
        }
    }

    if ($userPassword -ceq $adminPassword) {
        throw 'Dashboard user and admin passwords must differ. No containers were changed.'
    }
}
