# WebMCP Co-DM Deployment

This challenge did not replace the existing Dockerfile, Compose topology, authentication, PostgreSQL persistence, or narrator configuration. It added no auth bypass and did not deploy or overwrite any public environment.

## Local Docker startup

```powershell
Copy-Item .env.example .env
notepad .env
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

The reset script validates configuration before stopping the existing project, preserves volumes, builds the dashboard image, starts PostgreSQL, waits for health, and prints the dashboard URL. The default is `http://127.0.0.1:8181`.

If port 5432 is already occupied, set an unused `POSTGRES_HOST_PORT` in `.env`; the application still reaches PostgreSQL over the Compose network at `postgres:5432`.

## Required Compose environment

```env
GAE_DASHBOARD_USER_USERNAME=user
GAE_DASHBOARD_USER_PASSWORD=replace-with-a-unique-player-secret
GAE_DASHBOARD_ADMIN_USERNAME=admin
GAE_DASHBOARD_ADMIN_PASSWORD=replace-with-a-different-admin-secret
GAE_DASHBOARD_SHOW_LOGIN_PASSWORDS=false
GAE_AUTH_RATE_LIMIT_PER_MINUTE=10
GAE_DB_PASSWORD=replace-with-a-third-unique-secret
GAE_HOST_PORT=8181
POSTGRES_HOST_PORT=5432
```

Production startup rejects blank, short, shared, or published demo passwords. Use three different secrets of at least 12 characters and keep `.env` out of version control.

## Narrator configuration and fallback

For a local LM Studio narrator behind Docker:

```env
LM_STUDIO_PROVIDER=OpenAICompatible
LM_STUDIO_ENDPOINT=http://host.docker.internal:1234
LM_STUDIO_MODEL=default
LM_STUDIO_API_KEY=
LM_STUDIO_CONTEXT_LENGTH=
LM_STUDIO_THINK=
```

For Ollama, set `LM_STUDIO_PROVIDER=Ollama` and use the Ollama endpoint. When the configured narrator is unavailable, the existing engine continues with its grounded local fallback and reports degraded narrator health; Co-DM context does not pretend the narrator is available.

Discord is optional:

```env
DISCORD_TOKEN=
DISCORD_CHANNEL_ID=
```

The no-Discord challenge path is deliberate. A DM message is persisted to the Player Flow first. When a player has a Discord thread and the notifier is configured, the same message is mirrored on a best-effort basis.

## Health checks

Use the public liveness endpoint for infrastructure:

```powershell
Invoke-RestMethod http://127.0.0.1:8181/health/live
```

Use the signed-in dashboard health endpoint for core API, PostgreSQL, and narrator details:

```text
/api/dashboard/health
```

The Dockerfile exposes HTTP on container port `8080` and defines its health check against `/health/live`.

## Quickest durable public-host route

The smallest durable route is one Docker web service plus one managed PostgreSQL database in the same region. Render supports building a web service directly from a repository Dockerfile and attaching environment variables and a health-check path: [Render Web Services](https://render.com/docs/web-services) and [Docker on Render](https://render.com/docs/docker).

1. Create a managed Render Postgres database.
2. Create a Render Web Service from this repository and select the existing `Dockerfile`.
3. Keep the service on one instance; the current release boundary does not provide a SignalR backplane or multi-instance coordination.
4. Set health check path `/health/live` and expose the Docker service's port `8080`.
5. Convert the managed database credentials into an Npgsql connection string and set it as `ConnectionStrings__GameDatabase`:

   ```text
   Host=<internal-host>;Port=5432;Database=<database>;Username=<user>;Password=<password>
   ```

6. Set these runtime variables in the service, using secret values where appropriate:

   ```text
   ASPNETCORE_ENVIRONMENT=Production
   ASPNETCORE_URLS=http://+:8080
   DashboardAuth__User__Username=user
   DashboardAuth__User__Password=<unique-player-secret>
   DashboardAuth__Admin__Username=admin
   DashboardAuth__Admin__Password=<different-admin-secret>
   DashboardAuth__ShowLoginPasswords=false
   DashboardAuth__LoginRateLimitPerMinute=10
   ConnectionStrings__GameDatabase=<Npgsql connection string above>
   LmStudio__Provider=OpenAICompatible
   LmStudio__Endpoint=<reachable OpenAI-compatible endpoint or an intentionally unavailable URL for fallback mode>
   LmStudio__Model=<model id or default>
   LmStudio__ApiKey=<secret when required>
   Discord__Token=
   Discord__ChannelId=
   ```

7. Deploy, verify `/health/live`, sign in as administrator, click **Seed Demo**, and run the scenario before sharing judge credentials.

No Render Blueprint was added because it was not deployed and tested during the challenge timebox. The manual mapping above is explicit; an unverified infrastructure file would be theatre in a YAML waistcoat.

## Fastest temporary verification route

With the local stack healthy and `cloudflared` installed:

```powershell
cloudflared tunnel --url http://127.0.0.1:8181
```

The command prints a temporary `trycloudflare.com` URL. Cloudflare documents Quick Tunnels as testing-only, not production: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/).

Keep the dashboard authenticated, use unique temporary credentials, share only through a trusted channel, stop `cloudflared` immediately after judging, and do not expose PostgreSQL. If SignalR transport negotiation cannot use WebSockets through a temporary route, the dashboard already falls back to polling; verify the Player Flow message before recording.

## Judge login

1. Give the judge the HTTPS dashboard URL, admin username, and a unique temporary admin password through a private channel.
2. Do not set `GAE_DASHBOARD_SHOW_LOGIN_PASSWORDS=true` on a public URL.
3. The judge signs in as admin, opens **Admin Console → DM Console**, and selects `demo-user`.
4. The judging browser must expose the imperative `document.modelContext.registerTool` API. The WebMCP Status card should show five tools.
5. Rotate the temporary dashboard password after judging.

## Seed and reset

Safe deterministic demo reset:

```powershell
$baseUrl = 'https://your-dashboard-host'
$adminPassword = Read-Host 'Temporary admin password'
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginBody = @{ username = 'admin'; password = $adminPassword; rememberMe = $false } | ConvertTo-Json
Invoke-RestMethod -WebSession $session -Method Post -Uri "$baseUrl/api/dashboard/auth/login" -ContentType 'application/json' -Body $loginBody
Invoke-RestMethod -WebSession $session -Method Post -Uri "$baseUrl/api/dashboard/admin/seed-demo" -ContentType 'application/json' -Body '{"replaceExisting":true}'
```

This resets only the deterministic demo characters through the existing admin endpoint. Do not delete Docker volumes unless a complete destructive wipe is expressly intended.

## WebMCP verification

The most reliable local verification uses the repository's isolated E2E runner. It supplies matching throwaway credentials, raises the login limit for the test campaign, and removes its containers and volumes afterward:

```powershell
$env:GAE_E2E_PROJECT_NAME='gae-e2e-webmcp'
$env:GAE_HOST_PORT='8183'
$env:POSTGRES_HOST_PORT='55434'
npm run test:e2e:docker
```

Choose unused host ports when those examples are occupied. A direct Playwright run against an already-running stack must also export the exact user and admin credentials configured for that stack; setting only `PLAYWRIGHT_BASE_URL` can produce misleading authentication failures.

Backend message verification:

```powershell
dotnet test tests/GAE.Integration.Tests/GAE.Integration.Tests.csproj --filter "FullyQualifiedName~AdminConsoleTests.AdminSendMessage_PersistsToPlayerStory_WhenDiscordIsUnavailable"
```

The browser test injects a stub `document.modelContext.registerTool` before page load. It verifies five registrations, closed top-level schemas, shared-service context, real world search, visible proposal creation, non-mutating rejection, approved mutation, single-player message persistence, and graceful behavior without WebMCP.
