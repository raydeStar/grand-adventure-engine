# Self-Hosting Setup Guide

Everything you need to run Grand Adventure Engine on your own machine or server.

---

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | 10.0+ | Builds and runs the game engine |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | 24+ | Runs the app container and PostgreSQL |
| [LM Studio](https://lmstudio.ai/) | Latest | Runs the AI narrator locally (free, private) |
| [Node.js](https://nodejs.org/) | 20+ | Only needed if you want to run browser tests |

Optional:

| Tool | Purpose |
|------|---------|
| [Discord Developer Account](https://discord.com/developers/applications) | Required only if you want the Discord bot |
| [Git](https://git-scm.com/) | Clone the repository |

---

## 1. Clone the Repository

```bash
git clone https://github.com/your-org/GrandAdventureEngine.git
cd GrandAdventureEngine
```

---

## 2. Set Up LM Studio

LM Studio runs the AI that narrates the game. It's free and runs entirely on your machine.

1. **Install LM Studio** from [lmstudio.ai](https://lmstudio.ai/).
2. **Download a model.** Recommended models (in order of preference):
   - `lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF` — good balance of speed and quality
   - Any 7B–13B instruct-tuned model works. Larger models produce better narration but are slower.
3. **Start the local server:**
   - Open LM Studio → go to the **Local Server** tab (left sidebar, server icon).
   - Load your chosen model.
   - Click **Start Server**. The default endpoint is `http://localhost:1234`.
   - Leave it running while the game is active.

> **Tip:** LM Studio uses an OpenAI-compatible API. The engine connects to it via the `LmStudio:Endpoint` config setting. Inside Docker, this is automatically set to `http://host.docker.internal:1234`.

### Ollama / Unsloth Studio option

ProjectBonk can also call Ollama directly, or use Unsloth Studio through its protected OpenAI-compatible API.

1. Install Ollama and pull or create your model.
2. Set the narrator provider to `Ollama`.
3. Use Ollama's default endpoint, `http://localhost:11434` locally or `http://host.docker.internal:11434` from Docker.
4. Set `LmStudio:ContextLength` to `16384` for a 16K context window.
5. Set `LmStudio:Think` to `false` to ask thinking models to answer directly.

Example `.env` values for Docker:

```env
LM_STUDIO_PROVIDER=Ollama
LM_STUDIO_ENDPOINT=http://host.docker.internal:11434
LM_STUDIO_MODEL=diffusiongemma:latest
LM_STUDIO_CONTEXT_LENGTH=16384
LM_STUDIO_THINK=false
```

If you use Ollama's OpenAI-compatible `/v1/chat/completions` endpoint instead of ProjectBonk's native Ollama mode, context size is not a per-request OpenAI field. Create an Ollama `Modelfile` with `PARAMETER num_ctx 16384`, run `ollama create`, then point `LM_STUDIO_MODEL` at that created model.

For Dockerized Unsloth Studio, use `OpenAICompatible`, set `LM_STUDIO_ENDPOINT=http://host.docker.internal:8000`, and set `LM_STUDIO_API_KEY` to a Studio API key. Context length is chosen when the model is loaded in Studio; `LM_STUDIO_THINK=false` sends Studio's `enable_thinking=false` request extension.

---

## 3. Configure Environment Variables

Create a `.env` file in the project root (next to `docker-compose.yml`):

```env
# ── Required ──────────────────────────────────
# Narrator provider: OpenAICompatible for LM Studio, Ollama for Ollama native /api/chat
LM_STUDIO_PROVIDER=OpenAICompatible

# Narrator endpoint. Use http://host.docker.internal:11434 for Ollama in Docker,
# or http://host.docker.internal:8000 for Dockerized Unsloth Studio.
LM_STUDIO_ENDPOINT=http://host.docker.internal:1234

# Narrator model name (use "default" to auto-detect the loaded model)
LM_STUDIO_MODEL=default

# Optional bearer token, required for Unsloth Studio's /v1 API
LM_STUDIO_API_KEY=

# Optional request options
LM_STUDIO_CONTEXT_LENGTH=
LM_STUDIO_THINK=

# ── Discord Bot (optional — skip if dashboard-only) ──
DISCORD_TOKEN=your_discord_bot_token_here
DISCORD_CHANNEL_ID=your_channel_id_here

# ── Dashboard Credentials (optional — defaults shown) ──
GAE_DASHBOARD_USER_USERNAME=user
GAE_DASHBOARD_USER_PASSWORD=GAE-User-Local!123
GAE_DASHBOARD_ADMIN_USERNAME=admin
GAE_DASHBOARD_ADMIN_PASSWORD=GAE-Admin-Local!123

# ── Ports (optional — defaults shown) ──
GAE_HOST_PORT=8181
POSTGRES_HOST_PORT=5432

# ── Database (optional — default works for local dev) ──
GAE_DB_PASSWORD=gae_dev_password
```

> **Security note:** For any internet-facing deployment, change all default passwords and use strong, unique values.

---

## 4. Start the Docker Stack

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

This script:
1. Stops any existing containers (preserves data volumes).
2. Rebuilds the `gae` application image.
3. Starts PostgreSQL and waits for it to be healthy.
4. Starts the game engine container.
5. Waits for the health endpoint to respond.
6. Prints the dashboard URL.

**First run note:** The first build takes a few minutes to download base images and compile. Subsequent runs are much faster thanks to Docker layer caching.

### Verify It's Running

Open your browser to **http://localhost:8181** (or the URL printed by the script).

| Login | Username | Password |
|-------|----------|----------|
| Player | `user` | `GAE-User-Local!123` |
| Admin | `admin` | `GAE-Admin-Local!123` |

The admin view has additional panels for managing players, editing lore, and inspecting game state.

---

## 5. Set Up the Discord Bot (Optional)

Skip this section if you only want the web dashboard.

### Create a Bot Application

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications).
2. Click **New Application** → name it (e.g., "Grand Adventure Engine").
3. Go to **Bot** → click **Reset Token** → copy the token.
4. Under **Privileged Gateway Intents**, enable:
   - **Message Content Intent** (required — the bot reads player commands).
5. Go to **OAuth2** → **URL Generator**:
   - Scopes: `bot`
   - Bot Permissions: `Send Messages`, `Read Message History`, `Embed Links`
   - Copy the generated URL and open it in your browser to invite the bot to your server.

### Configure the Channel

1. In your Discord server, create a channel for the game (e.g., `#adventure`).
2. Right-click the channel → **Copy Channel ID** (requires Developer Mode in Discord settings).
3. Add both values to your `.env` file:

```env
DISCORD_TOKEN=your_bot_token
DISCORD_CHANNEL_ID=your_channel_id
```

4. Restart the stack: `powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1`

The bot will appear online in your server and respond to commands in the configured channel.

---

## 6. Seed Demo Data (Optional)

To populate the game with sample characters and content:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\seed-data.ps1
```

To replace existing demo data:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\seed-data.ps1 -ReplaceExisting
```

---

## Running Without Docker (Development Mode)

If you prefer to run directly on your machine:

```powershell
# Build
dotnet build GrandAdventureEngine.slnx

# Run the dashboard API (includes the game engine)
dotnet run --project src/GAE.Dashboard.Api
```

The app reads config from `config/appsettings.json` and `config/appsettings.Development.json`. Make sure LM Studio is running on `localhost:1234` and PostgreSQL is accessible at the configured connection string.

---

## Configuration Reference

All settings can be overridden via environment variables using the `__` (double underscore) separator. For example, `LmStudio__Endpoint` overrides the `LmStudio.Endpoint` config key.

| Key | Default | Description |
|-----|---------|-------------|
| `LmStudio:Provider` | `OpenAICompatible` | Narrator backend mode: `OpenAICompatible` for LM Studio or `Ollama` for native Ollama `/api/chat` |
| `LmStudio:Endpoint` | `http://localhost:1234` | Narrator API URL. Use `http://host.docker.internal:8000` for Dockerized Unsloth Studio |
| `LmStudio:Model` | `default` | Model name (or `default` for auto-detect) |
| `LmStudio:ApiKey` | *(empty)* | Optional bearer token for protected OpenAI-compatible backends such as Unsloth Studio |
| `LmStudio:ContextLength` | *(empty)* | Optional context length. In native Ollama mode this is sent as `options.num_ctx`; use `16384` for 16K. For OpenAI-compatible Unsloth Studio, set context length when loading the model in Studio |
| `LmStudio:Think` | *(empty)* | Optional thinking control. In native Ollama mode this is sent as `think`; in OpenAI-compatible mode this sends `enable_thinking=false` and `reasoning_effort=none` when set to `false` |
| `LmStudio:RetryCount` | `1` | Retries on transient LM Studio failures |
| `LmStudio:RetryDelayMs` | `2000` | Delay between retries (ms) |
| `Discord:Token` | *(empty)* | Discord bot token |
| `Discord:ChannelId` | *(empty)* | Discord channel for the game |
| `DashboardAuth:User:Username` | `user` | Player dashboard login |
| `DashboardAuth:User:Password` | `GAE-User-Local!123` | Player dashboard password |
| `DashboardAuth:Admin:Username` | `admin` | Admin dashboard login |
| `DashboardAuth:Admin:Password` | `GAE-Admin-Local!123` | Admin dashboard password |
| `DashboardAuth:SessionHours` | `12` | Login session duration |
| `DashboardAuth:ShowLoginPasswords` | `false` | Include passwords in anonymous login hints; only enable for private local demos |
| `ConnectionStrings:GameDatabase` | *(see appsettings.json)* | PostgreSQL connection string |

See [dashboard-ops.md](dashboard-ops.md) for additional admin operations and environment overrides.

---

## Troubleshooting

### LM Studio not responding

**Symptom:** The game loads but narrator responses are empty or error out.

**Fix:**
- Verify LM Studio is running and the server is started (Local Server tab → green status).
- Check the endpoint: inside Docker, the engine uses `http://host.docker.internal:1234`. This requires Docker Desktop's host networking support.
- On Linux without Docker Desktop, you may need `--network=host` or set `LmStudio__Endpoint` to your machine's LAN IP.
- Try `curl http://localhost:1234/v1/models` — you should get a JSON response listing the loaded model.

### Port 8181 already in use

**Symptom:** `reset-docker-stack.ps1` fails or the dashboard isn't reachable.

**Fix:**
- The script automatically falls forward to the next free port and prints the actual URL.
- To force a specific port: `.\scripts\reset-docker-stack.ps1 -GaePort 9090`
- Check what's using the port: `netstat -ano | findstr :8181`

### Discord bot not connecting

**Symptom:** The bot appears offline in your server.

**Fix:**
- Verify `DISCORD_TOKEN` is set correctly in `.env` (no extra spaces or quotes).
- Check that **Message Content Intent** is enabled in the Discord Developer Portal.
- Look at container logs: `docker compose logs gae | Select-String -Pattern "Discord"`
- The bot only activates if the token is non-empty and not the placeholder value.

### PostgreSQL connection errors

**Symptom:** Startup fails with database connection errors.

**Fix:**
- Wait for PostgreSQL to be healthy: `docker compose ps` (should show `healthy`).
- If using custom passwords, ensure `GAE_DB_PASSWORD` matches in both the app and PostgreSQL containers.
- To reset the database: `powershell -ExecutionPolicy Bypass -File .\scripts\wipe-data.ps1 -Restart`

### Game state seems stale or broken

**Fix:**
- Wipe all data and restart fresh:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\scripts\wipe-data.ps1 -Restart
  ```
- Re-seed content (clears the content registry and reloads from YAML):
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1 -ReseedContent
  ```

### Build fails

**Fix:**
- Ensure .NET 10 SDK is installed: `dotnet --version` should show `10.x.x`.
- Clean and rebuild: `dotnet clean; dotnet build GrandAdventureEngine.slnx`
- Force Docker rebuild: `.\scripts\reset-docker-stack.ps1 -NoCache`
