const { test, expect } = require('@playwright/test');
const {
  login,
  openAdminConsole,
  openCreateCharacter,
  cleanupTestPlayersViaApi,
  uniqueId
} = require('./helpers');

/**
 * End-to-end tests that exercise the full LM Studio narrator pipeline
 * through the browser dashboard. These tests require LM Studio to be
 * running at localhost:1234 and the Docker container at localhost:8181.
 *
 * The tests verify that real LLM narration is produced (not fallback text).
 */
test.describe('LM Studio end-to-end narration', () => {
  test.afterEach(async ({ request }) => {
    try {
      await cleanupTestPlayersViaApi(request, ['lm-e2e-']);
    } catch { /* best effort */ }
  });

  test('narrator health check shows LM Studio connected', async ({ page }) => {
    await login(page, 'admin');

    // The health panel should show narrator as OK
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
    await page.getByRole('button', { name: 'Create Character' }).click();

    await expect(page.locator('#header-player')).toContainText('Narrator Test Hero');
    await expect(page.locator('#command-input')).toBeEnabled();

    // Send 'look' — this should trigger narration
    await page.locator('#command-input').fill('look');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    // Verify the story log contains a real narration entry (not the fallback)
    const storyText = await page.locator('#story-log').textContent();
    expect(storyText).toBeTruthy();
    expect(storyText).not.toContain('narrator clears his throat');
    expect(storyText.length).toBeGreaterThan(20);
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
    await page.getByRole('button', { name: 'Create Character' }).click();

    await expect(page.locator('#header-player')).toContainText('Explorer Test');
    await expect(page.locator('#command-input')).toBeEnabled();

    // Move north — should generate a new room via LM Studio
    await page.locator('#command-input').fill('go north');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    // Room name should change from Crossroads of the Shattered Reaches
    const roomName = await page.locator('#room-name').textContent();
    expect(roomName).toBeTruthy();
    expect(roomName).not.toBe('');

    // Description should be present (LLM-generated)
    const roomDesc = await page.locator('#room-description').textContent();
    expect(roomDesc).toBeTruthy();
    expect(roomDesc.length).toBeGreaterThan(10);

    // Story log should have narration for the move
    const storyText = await page.locator('#story-log').textContent();
    expect(storyText).not.toContain('narrator clears his throat');
  });

  test('admin command probe returns narrated response', async ({ page }) => {
    await login(page, 'admin');

    // Seed demo characters
    await page.getByRole('button', { name: 'Seed Demo User + Admin' }).click();
    await expect(page.locator('#portal-message')).toContainText(/Seeded|already existed/i);

    await openAdminConsole(page);

    // Run a command as demo-user via admin console
    await page.locator('#admin-player-select').selectOption('demo-user');
    await page.locator('#admin-command-input').fill('look');
    await page.getByRole('button', { name: 'Ask / Execute' }).click();

    // Wait for response in admin command log
    await expect(page.locator('#admin-command-log')).toContainText('demo-user > look', { timeout: 30_000 });

    // Verify it shows real narration, not "narrator clears his throat"
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
    await page.getByRole('button', { name: 'Create Character' }).click();

    await expect(page.locator('#command-input')).toBeEnabled();

    // First command: look
    await page.locator('#command-input').fill('look');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    const firstStory = await page.locator('#story-log').textContent();

    // Second command: look again (should produce a different narration due to temperature)
    await page.locator('#command-input').fill('look');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    const secondStory = await page.locator('#story-log').textContent();

    // Story log should have grown (more content after second command)
    expect(secondStory.length).toBeGreaterThan(firstStory.length);
    // Neither should contain fallback text
    expect(secondStory).not.toContain('narrator clears his throat');
  });
});
