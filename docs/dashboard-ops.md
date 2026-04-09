# Dashboard Operations

## Local access

- Browser URL: `http://localhost:8181` by default. `reset-docker-stack.ps1` automatically falls forward to another free port and prints the actual URL when `8181` is already in use.
- User account: `user`
- User password: `GAE-User-Local!123`
- Admin account: `admin`
- Admin password: `GAE-Admin-Local!123`

Override any of those through Docker environment variables or configuration keys:

- `GAE_DASHBOARD_USER_USERNAME`
- `GAE_DASHBOARD_USER_PASSWORD`
- `GAE_DASHBOARD_ADMIN_USERNAME`
- `GAE_DASHBOARD_ADMIN_PASSWORD`
- `GAE_HOST_PORT`
- `WIKIJS_HOST_PORT`

## Manual browser workflow

1. Sign in as `user` to play through the standard command flow.
2. Sign in as `admin` to open the admin console, seed demo personas, run workflows, and use the mutation studio.
3. Use explicit player ids when creating characters if you want repeatable manual or automated scenarios.

## PowerShell examples

```powershell
$base = $env:GAE_BASE_URL
if ([string]::IsNullOrWhiteSpace($base)) {
  $base = 'http://localhost:8181'
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

Invoke-RestMethod "$base/api/dashboard/auth/login" `
  -Method Post `
  -WebSession $session `
  -ContentType 'application/json' `
  -Body (@{
    username = 'admin'
    password = 'GAE-Admin-Local!123'
  } | ConvertTo-Json)

Invoke-RestMethod "$base/api/dashboard/admin/summary" -WebSession $session

Invoke-RestMethod "$base/api/dashboard/admin/mutations/teleport" `
  -Method Post `
  -WebSession $session `
  -ContentType 'application/json' `
  -Body (@{
    playerId = 'demo-user'
    roomId = 'qa-lab'
    roomName = 'QA Lab'
    roomDescription = 'Manual test fixture room.'
    connectFromCurrentRoom = $true
    entryDirection = 'north'
  } | ConvertTo-Json)

Invoke-RestMethod "$base/api/dashboard/action" `
  -Method Post `
  -WebSession $session `
  -ContentType 'application/json' `
  -Body (@{
    playerId = 'demo-user'
    command = 'look'
  } | ConvertTo-Json)
```

## Local ops scripts

- Reset the Docker stack without wiping data:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

- Reset the Docker stack on explicit host ports:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1 -GaePort 8181 -WikiPort 3001
```

- Force a clean rebuild of the `gae` image before restart:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1 -NoCache
```

- Seed the built-in demo data through the admin API:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\seed-data.ps1
```

- Re-seed the built-in demo data and replace any existing demo players:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\seed-data.ps1 -ReplaceExisting
```

- Wipe all persistent app data, including Docker volumes and the local `.\data` directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\wipe-data.ps1
```

- Wipe all persistent app data and immediately restart the stack:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\wipe-data.ps1 -Restart
```

## Browser test commands

- Full E2E: `npm run test:e2e`
- Headed E2E: `npm run test:e2e:headed`
- Visual-only regression: `npm run test:e2e:visual`
- Visual-only regression (safe mode): `npm run test:e2e:visual:safe`
- Refresh visual baselines: `npm run test:e2e:update-snapshots`
- Refresh visual baselines (safe mode): `npm run test:e2e:update-snapshots:safe`
- Docker-backed E2E: `npm run test:e2e:docker`

## Admin mutation routes

- `POST /api/dashboard/admin/mutations/resources`
- `POST /api/dashboard/admin/mutations/teleport`
- `POST /api/dashboard/admin/mutations/grant-item`
- `POST /api/dashboard/admin/mutations/status`
- `POST /api/dashboard/admin/mutations/room-fixture`