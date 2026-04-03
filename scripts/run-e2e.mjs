import { spawn } from 'node:child_process';

const baseUrl = process.env.PLAYWRIGHT_BASE_URL
  || process.env.GAE_BASE_URL
  || `http://127.0.0.1:${process.env.GAE_HOST_PORT || '8181'}`;

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      stdio: 'inherit',
      shell: process.platform === 'win32',
      env: process.env,
      ...options
    });

    child.on('exit', (code) => {
      if (code === 0) {
        resolve();
        return;
      }

      reject(new Error(`${command} ${args.join(' ')} exited with code ${code}`));
    });
  });
}

async function waitForHealth(url) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }
    } catch {
      // Retry until healthy.
    }

    await new Promise((resolve) => setTimeout(resolve, 2000));
  }

  throw new Error(`Timed out waiting for ${url}`);
}

async function main() {
  await run('docker', ['compose', 'up', '--build', '-d']);
  await waitForHealth(`${baseUrl}/health`);
  await run(process.platform === 'win32' ? 'npx.cmd' : 'npx', ['playwright', 'test'], {
    env: {
      ...process.env,
      PLAYWRIGHT_BASE_URL: baseUrl
    }
  });
}

main().catch((error) => {
  console.error(error.message || error);
  process.exit(1);
});