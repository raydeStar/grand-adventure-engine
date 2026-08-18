import { spawnSync } from 'node:child_process';

const projectName = process.env.GAE_E2E_PROJECT_NAME || `gae-e2e-${process.pid}`;
if (!/^gae-e2e-[a-z0-9-]+$/.test(projectName)) {
  throw new Error('GAE_E2E_PROJECT_NAME must begin with "gae-e2e-" and contain only lowercase letters, digits, or hyphens.');
}

const hostPort = process.env.GAE_HOST_PORT || '8181';
const baseUrl = process.env.PLAYWRIGHT_BASE_URL
  || process.env.GAE_BASE_URL
  || `http://127.0.0.1:${hostPort}`;
const e2eEnvironment = {
  ...process.env,
  COMPOSE_PROJECT_NAME: projectName,
  GAE_HOST_PORT: hostPort,
  GAE_BASE_URL: baseUrl,
  PLAYWRIGHT_BASE_URL: baseUrl,
  GAE_DASHBOARD_USER_USERNAME: 'user',
  GAE_DASHBOARD_USER_PASSWORD: 'GAE-E2E-User!12345',
  GAE_DASHBOARD_ADMIN_USERNAME: 'admin',
  GAE_DASHBOARD_ADMIN_PASSWORD: 'GAE-E2E-Admin!67890',
  GAE_DB_PASSWORD: 'GAE-E2E-Database!24680',
  GAE_DASHBOARD_SHOW_LOGIN_PASSWORDS: 'false',
  GAE_AUTH_RATE_LIMIT_PER_MINUTE: '1000',
  DISCORD_TOKEN: '',
  DISCORD_CHANNEL_ID: '',
  LM_STUDIO_PROVIDER: 'OpenAICompatible',
  LM_STUDIO_ENDPOINT: 'http://host.docker.internal:9',
  LM_STUDIO_API_KEY: ''
};

// Run a child command synchronously so failures cannot vanish between Docker and Playwright.
function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: false,
    env: process.env,
    ...options
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} exited with code ${result.status}`);
  }
}

// Poll the public health endpoint until the production container is genuinely ready.
async function waitForHealth(url) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const controller = new AbortController();
    let timeoutId;

    try {
      const timeout = new Promise((_, reject) => {
        timeoutId = setTimeout(() => {
          controller.abort();
          reject(new Error('Health probe timed out.'));
        }, 2000);
      });
      const response = await Promise.race([
        fetch(url, { signal: controller.signal }),
        timeout
      ]);
      if (response.ok) {
        return;
      }
    } catch {
      // Retry until healthy.
    } finally {
      clearTimeout(timeoutId);
    }

    await new Promise((resolve) => setTimeout(resolve, 2000));
  }

  throw new Error(`Timed out waiting for ${url}`);
}

// Build, exercise, and remove a deliberately isolated production-like stack.
async function main() {
  try {
    console.info(`[e2e] Raising isolated stack ${projectName}. The user's Discord token is not invited.`);
    run('docker', ['compose', 'up', '--build', '-d'], { env: e2eEnvironment });
    await waitForHealth(`${baseUrl}/health`);
    console.info(`[e2e] Dashboard ready at ${baseUrl}. Releasing the browser familiars.`);
    run(process.execPath, ['node_modules/@playwright/test/cli.js', 'test', 'browser-tests/dashboard.spec.js'], {
      env: e2eEnvironment
    });
  } finally {
    if (process.env.GAE_E2E_KEEP_STACK !== '1') {
      console.info(`[e2e] Lowering isolated stack ${projectName}; no test volume survives the ritual.`);
      run('docker', ['compose', 'down', '--volumes', '--remove-orphans'], { env: e2eEnvironment });
    }
  }
}

await main().catch((error) => {
  console.error(error.message || error);
  process.exit(1);
});
