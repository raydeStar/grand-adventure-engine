# B06 — Self-Hosting Setup Guide

**Category:** Blocker
**Effort:** Medium
**Touches:** `docs/`, config files, `README.md`

## What

Someone who wants to run their own instance needs to: get a Discord bot token, configure LM Studio, set up the database, and run the Docker stack. The config still has `YOUR_DISCORD_BOT_TOKEN_HERE` as a placeholder. There's no step-by-step guide for any of this.

## Why This Blocks MVP

If you want anyone besides yourself to host this, they need instructions. Even for you — if you set up a fresh machine or hand it to a friend, the setup should be documented.

## Acceptance Criteria

- [ ] `docs/setup-guide.md` exists with step-by-step instructions
- [ ] Covers: Discord bot creation (with link to Discord dev portal), token configuration
- [ ] Covers: LM Studio installation, model selection, endpoint config
- [ ] Covers: Docker stack startup (`reset-docker-stack.ps1`), what it creates, how to verify
- [ ] Covers: Database setup (PostgreSQL, `gae_app` user)
- [ ] Covers: Wiki.js initial setup (if needed for content)
- [ ] Includes a `.env.example` or `appsettings.example.json` with all required config keys and comments
- [ ] Linked from README "Want to Run Your Own?" section
- [ ] Troubleshooting section: common issues (port conflicts, LM Studio not responding, bot not connecting)

## Notes

- The README already links to download pages for prerequisites. This guide goes deeper into configuration.
- Environment variable overrides are documented in `docs/dashboard-ops.md` — reference that, don't duplicate.
