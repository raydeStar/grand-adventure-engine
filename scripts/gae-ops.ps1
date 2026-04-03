Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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