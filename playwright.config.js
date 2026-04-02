const { defineConfig, devices } = require('@playwright/test');

const baseURL = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:8181';
const isSnapshotUpdateRun = process.argv.includes('--update-snapshots');
const isVisualTargetRun = process.argv.includes('--grep')
  || process.argv.some((arg) => arg.includes('dashboard.visual.spec.js') || arg.includes('@visual'));
const safeVisualMode = process.env.GAE_PLAYWRIGHT_SAFE_MODE === '1'
  || (isSnapshotUpdateRun && isVisualTargetRun);

module.exports = defineConfig({
  testDir: './browser-tests',
  timeout: 120_000,
  workers: 1,
  fullyParallel: false,
  maxFailures: safeVisualMode ? 1 : 0,
  retries: process.env.CI ? 2 : 0,
  reporter: safeVisualMode
    ? [['line']]
    : [
      ['list'],
      ['html', { outputFolder: 'playwright-report', open: 'never' }]
    ],
  globalSetup: require.resolve('./browser-tests/global-setup.js'),
  use: {
    baseURL,
    trace: safeVisualMode ? 'off' : 'retain-on-failure',
    screenshot: safeVisualMode ? 'off' : 'only-on-failure',
    video: safeVisualMode ? 'off' : 'retain-on-failure'
  },
  projects: [
    {
      name: 'desktop-chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 1000 }
      }
    },
    {
      name: 'mobile-chromium',
      use: {
        ...devices['Pixel 7']
      }
    }
  ]
});