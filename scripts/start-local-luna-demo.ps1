[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 8282,

    [ValidateSet('low', 'medium')]
    [string]$ReasoningEffort = 'medium',

    [string]$Database = 'gae_luna_demo'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$environmentPath = Join-Path $repoRoot '.env'
$executablePath = Join-Path $repoRoot 'src/GAE.Dashboard.Api/bin/Release/net10.0/GAE.Dashboard.Api.exe'

if (-not (Test-Path -LiteralPath $environmentPath)) {
    throw "Local environment file was not found at '$environmentPath'."
}
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Build the Release dashboard before starting the Luna demo. Missing '$executablePath'."
}

Get-Content -LiteralPath $environmentPath | ForEach-Object {
    if ($_ -match '^([^#=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable(
            $matches[1].Trim(),
            $matches[2].Trim().Trim('"'),
            'Process')
    }
}

if ([string]::IsNullOrWhiteSpace($env:POSTGRES_HOST_PORT) -or [string]::IsNullOrWhiteSpace($env:GAE_DB_PASSWORD)) {
    throw 'POSTGRES_HOST_PORT and GAE_DB_PASSWORD must be configured in .env.'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__GameDatabase = "Host=127.0.0.1;Port=$env:POSTGRES_HOST_PORT;Database=$Database;Username=gae_app;Password=$env:GAE_DB_PASSWORD"
$env:LmStudio__Provider = 'CodexCli'
$env:LmStudio__Model = 'gpt-5.6-luna'
$env:LmStudio__CodexExecutable = 'codex'
$env:LmStudio__CodexReasoningEffort = $ReasoningEffort
$env:LmStudio__CodexTimeoutSeconds = '120'
$env:LmStudio__RetryCount = '0'

Set-Location -LiteralPath $repoRoot
& $executablePath --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
