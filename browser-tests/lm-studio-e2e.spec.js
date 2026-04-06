const { test, expect } = require('@playwright/test');
const {
  login,
  openAdminConsole,
  openCreateCharacter,
  cleanupTestPlayersViaApi,
  switchAdminTab,
  uniqueId
} = require('./helpers');

/**
 * End-to-end tests that exercise the full LM Studio narrator pipeline
 * through the browser dashboard. These tests require LM Studio to be
 * running at localhost:1234 and the Docker container at localhost:8181.
 *
 * The tests verify that real LLM narration is produced, not fallback text.
 */
test.describe('LM Studio end-to-end narration', () => {
  async function waitForCommandCompletion(page) {
    await page.waitForFunction(() => {
      const input = document.getElementById('command-input');
      return !!input && !input.disabled && (!window.UI || !window.UI._streamNode);
    }, null, { timeout: 30_000 });
  }

  async function sendCommand(page, command) {
    const responsePromise = page.waitForResponse((response) => {
      if (!response.url().includes('/api/dashboard/action')) return false;
      if (response.request().method() !== 'POST') return false;
      return (response.request().postData() || '').includes(command);
    });

    await page.locator('#command-input').fill(command);
    await page.getByRole('button', { name: 'Send' }).click();

    const actionResponse = await responsePromise;
    const result = await actionResponse.json();
    await waitForCommandCompletion(page);
    return result;
  }

  test.afterEach(async ({ request }) => {
    try {
      await cleanupTestPlayersViaApi(request, ['lm-e2e-']);
    } catch {
      /* best effort */
    }
  });

  test('narrator health check shows LM Studio connected', async ({ page }) => {
    await login(page, 'admin');

    const healthResult = await page.evaluate(async () => {
      const checks = await API.getHealth();
      return checks['health/narrator'];
    });

    expect(healthResult).toBeTruthy();
    expect(healthResult.ok).toBe(true);
    expect(healthResult.service).toBe('lm-studio');
  });

  test('look command returns real LLM narration, not fallback', async ({ page }, testInfo) => {
    const playerId = uniqueId('lm-e2e-look', testInfo.project.name);

    await login(page, 'user');
    await openCreateCharacter(page);
    await page.locator('#char-name').fill('Narrator Test Hero');
    await page.locator('#char-player-id').fill(playerId);
    await page.locator('#char-race').selectOption('Human');
    await page.locator('#char-class').selectOption('Warrior');
    await page.locator('#char-backstory').fill('Born for testing narrator connectivity.');
    await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();

    await expect(page.locator('#header-player')).toContainText('Narrator Test Hero');
    await expect(page.locator('#command-input')).toBeEnabled();

    const result = await sendCommand(page, 'look');
    const narrationText = [result.narration, result.mechanicalSummary].filter(Boolean).join(' ');

    expect(result.success).toBeTruthy();
    expect(narrationText).toBeTruthy();
    expect(narrationText).not.toContain('narrator clears his throat');
    expect(narrationText.length).toBeGreaterThan(20);
  });

  test('movement generates a new room with LLM-produced description', async ({ page }, testInfo) => {
    const playerId = uniqueId('lm-e2e-move', testInfo.project.name);

    await login(page, 'user');
    await openCreateCharacter(page);
    await page.locator('#char-name').fill('Explorer Test');
    await page.locator('#char-player-id').fill(playerId);
    await page.locator('#char-race').selectOption('Elf');
    await page.locator('#char-class').selectOption('Ranger');
    await page.locator('#char-backstory').fill('Wanderer of generated realms.');
    await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();

    await expect(page.locator('#header-player')).toContainText('Explorer Test');
    await expect(page.locator('#command-input')).toBeEnabled();

    const moveResult = await sendCommand(page, 'go north');
    expect(moveResult.success).toBeTruthy();

    const roomName = await page.locator('#room-name').textContent();
    expect(roomName).toBeTruthy();
    expect(roomName).not.toBe('');

    const roomDesc = await page.locator('#room-desc').textContent();
    expect(roomDesc).toBeTruthy();
    expect(roomDesc.length).toBeGreaterThan(10);

    const storyText = await page.locator('#story-log').textContent();
    expect(storyText).not.toContain('narrator clears his throat');
  });

  test('admin command probe returns narrated response', async ({ page }) => {
    await login(page, 'admin');

    await page.locator('#btn-seed-demo-admin').click();
    await expect(page.locator('#portal-message')).toContainText(/Seeded|already existed/i);

    await openAdminConsole(page);
    await switchAdminTab(page, 'commands');

    await page.locator('#admin-player-select').selectOption('demo-user');
    await page.locator('#admin-command-input').fill('look');
    await page.getByRole('button', { name: 'Execute' }).click();

    await expect(page.locator('#admin-command-log')).toContainText('demo-user > look', { timeout: 30_000 });

    const logText = await page.locator('#admin-command-log').textContent();
    expect(logText).not.toContain('narrator clears his throat');
  });

  test('multiple sequential commands produce distinct narrations', async ({ page }, testInfo) => {
    const playerId = uniqueId('lm-e2e-seq', testInfo.project.name);

    await login(page, 'user');
    await openCreateCharacter(page);
    await page.locator('#char-name').fill('Sequence Tester');
    await page.locator('#char-player-id').fill(playerId);
    await page.locator('#char-race').selectOption('Dwarf');
    await page.locator('#char-class').selectOption('Warrior');
    await page.locator('#char-backstory').fill('Tests sequential LLM calls.');
    await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();

    await expect(page.locator('#command-input')).toBeEnabled();

    const firstResult = await sendCommand(page, 'look');
    const firstStory = await page.locator('#story-log').textContent();

    const secondResult = await sendCommand(page, 'look');
    const secondStory = await page.locator('#story-log').textContent();

    expect(secondStory.length).toBeGreaterThan(firstStory.length);
    expect(firstResult.success).toBeTruthy();
    expect(secondResult.success).toBeTruthy();
    expect(secondStory).not.toContain('narrator clears his throat');
  });
});
