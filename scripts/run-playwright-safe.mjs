import { spawn } from 'node:child_process';

const playwrightArgs = process.argv.slice(2);

if (!playwrightArgs.some((arg) => arg.startsWith('--max-failures'))) {
  playwrightArgs.push('--max-failures=1');
}

if (!playwrightArgs.some((arg) => arg.startsWith('--reporter'))) {
  playwrightArgs.push('--reporter=line');
}

const command = process.platform === 'win32' ? 'npx.cmd' : 'npx';
const child = spawn(command, ['playwright', 'test', ...playwrightArgs], {
  stdio: 'inherit',
  shell: process.platform === 'win32',
  env: {
    ...process.env,
    GAE_PLAYWRIGHT_SAFE_MODE: '1'
  }
});

child.on('exit', (code) => {
  process.exit(code ?? 1);
});

child.on('error', (error) => {
  console.error(error.message || error);
  process.exit(1);
});
