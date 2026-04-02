# Local Agent Rules

- Use npm run test:e2e:update-snapshots:safe for visual baseline refreshes.
- Use npm run test:e2e:visual:safe for visual regression checks when chat stability matters.
- Avoid running raw playwright update-snapshot commands unless deeper debugging artifacts are explicitly needed.
- Safe mode keeps traces, videos, and failure screenshots off and limits failures to one to reduce oversized chat payloads.
- When taking screenshots with Playwright or Chrome MCP tools, ALWAYS downscale to a small resolution (e.g. 800×600 or smaller, or use scale/quality options) before returning them to chat. Full-resolution screenshots exceed the chat context window limit and irreversibly break the conversation. Use clip regions, viewport resizing, or deviceScaleFactor:1 to keep image payloads under 200KB.
