# Local Agent Rules

- Use npm run test:e2e:update-snapshots:safe for visual baseline refreshes.
- Use npm run test:e2e:visual:safe for visual regression checks when chat stability matters.
- Avoid running raw playwright update-snapshot commands unless deeper debugging artifacts are explicitly needed.
- Safe mode keeps traces, videos, and failure screenshots off and limits failures to one to reduce oversized chat payloads.
