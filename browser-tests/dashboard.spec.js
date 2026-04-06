const { test, expect } = require('@playwright/test');
const {
  login,
  openAdminConsole,
  openCreateCharacter,
  seedDemoViaApi,
  switchAdminTab,
  cleanupTestPlayersViaApi,
  uniqueId
} = require('./helpers');

test.describe('Grand Adventure Engine dashboard', () => {
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
    await page.waitForFunction(() => {
      const input = document.getElementById('command-input');
      return !!input && !input.disabled && (!window.UI || !window.UI._streamNode);
    }, null, { timeout: 30_000 });
    return result;
  }

  // Clean up test-created players after each test
  test.afterEach(async ({ request }) => {
    try {
      await cleanupTestPlayersViaApi(request, ['pw-']);
    } catch { /* best effort — don't fail the test on cleanup */ }
  });

  test('anonymous visitors are prompted to sign in before using the dashboard', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByRole('heading', { name: 'Grand Adventure Engine' })).toBeVisible();
    await expect(page.locator('#auth-form')).toBeVisible();
    await expect(page.locator('#btn-new-char')).toBeDisabled();
    await expect(page.locator('#existing-players')).toContainText('Sign in to view characters');
  });

  test('user can sign in, create a character, and complete a first gameplay loop', async ({ page }, testInfo) => {
    const playerId = uniqueId('pw-user', testInfo.project.name);

    await login(page, 'user');
    await openCreateCharacter(page);
    await page.locator('#char-name').fill('Playwright Hero');
    await page.locator('#char-player-id').fill(playerId);
    await page.locator('#char-race').selectOption('Human');
    await page.locator('#char-class').selectOption('Warrior');
    await page.locator('#char-backstory').fill('Provisioned by the browser E2E harness.');
    await page.getByRole('button', { name: 'Create Character' }).click();

    await expect(page.locator('#dashboard')).toBeVisible();
    await expect(page.locator('#header-player')).toContainText('Playwright Hero');
    await expect(page.locator('#command-input')).toBeEnabled();
    // Room name is theme-dependent; just verify the player spawned in a real room.
    await expect(page.locator('#room-name')).not.toBeEmpty();

    await page.locator('#command-input').fill('look');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
    await expect(page.locator('#story-log .story-entry')).not.toHaveCount(0, { timeout: 5_000 });
    await expect(page.locator('#story-log')).not.toContainText('Exits:');
    await expect(page.locator('#story-log')).not.toContainText('You see:');
    await expect(page.locator('#story-log')).not.toContainText('Items:');
    await expect(page.locator('#room-desc')).not.toBeEmpty();

    await page.locator('#command-input').fill('stats');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    await expect(page.getByRole('button', { name: 'Open Admin Console' })).toHaveCount(0);
  });

  test('user can accept, review, and abandon a seeded quest through the dashboard', async ({ page }, testInfo) => {
    const playerId = uniqueId('pw-quest', testInfo.project.name);

    await login(page, 'user');
    await openCreateCharacter(page);
    await page.locator('#char-name').fill('Quest Browser');
    await page.locator('#char-player-id').fill(playerId);
    await page.locator('#char-race').selectOption('Human');
    await page.locator('#char-class').selectOption('Warrior');
    await page.locator('#char-backstory').fill('Quest E2E coverage via Playwright.');
    await page.getByRole('button', { name: 'Create Character' }).click();

    await expect(page.locator('#dashboard')).toBeVisible();
    await expect(page.locator('#room-name')).not.toBeEmpty();

    await page.evaluate(() => {
      const maxId = window.setInterval(() => {}, 100000);
      for (let i = 1; i <= maxId; i++) window.clearInterval(i);
    });

    const acceptResult = await sendCommand(page, 'accept quest The Waterway Infestation');
    expect(acceptResult.success).toBeTruthy();
    expect(acceptResult.mechanicalSummary).toContain('The Waterway Infestation');
    await expect(page.locator('#story-log')).toContainText('accept quest The Waterway Infestation');

    const journalResult = await sendCommand(page, 'journal');
    expect(journalResult.success).toBeTruthy();
    expect(journalResult.mechanicalSummary).toContain('Active Quests');
    expect(journalResult.mechanicalSummary).toContain('The Waterway Infestation');
    await expect(page.locator('#story-log')).toContainText('> journal');

    const abandonResult = await sendCommand(page, 'abandon quest The Waterway Infestation');
    expect(abandonResult.success).toBeTruthy();
    expect(abandonResult.mechanicalSummary).toMatch(/abandoned/i);
    await expect(page.locator('#story-log')).toContainText('abandon quest The Waterway Infestation');

    const emptyJournalResult = await sendCommand(page, 'journal');
    expect(emptyJournalResult.success).toBeTruthy();
    expect(emptyJournalResult.mechanicalSummary).toMatch(/quest journal is empty/i);
    await expect(page.locator('#story-log')).toContainText('> journal');
  });

  test('admin console seeds demo actors and runs command/admin workflows', async ({ page }) => {
    await login(page, 'admin');

    await page.getByRole('button', { name: 'Seed Demo User + Admin' }).click();
    await expect(page.locator('#portal-message')).toContainText(/Seeded|already existed/i);

    await openAdminConsole(page);
    await switchAdminTab(page, 'overview');
    await expect(page.locator('#summary-cards .summary-card')).toHaveCount(6);

    await page.locator('#workflow-player-select').selectOption('demo-user');
    await page.getByRole('button', { name: 'Run User Smoke' }).click();
    await expect(page.locator('#workflow-log')).toContainText('demo-user > look');

    await switchAdminTab(page, 'players');
    await expect(page.locator('#admin-players-table')).toContainText('demo-user');
    await expect(page.locator('#admin-players-table')).toContainText('demo-admin');

    await switchAdminTab(page, 'commands');
    await page.locator('#admin-player-select').selectOption('demo-admin');
    await page.locator('#admin-command-input').fill('help');
    await page.getByRole('button', { name: 'Execute' }).click();
    await expect(page.locator('#admin-command-log')).toContainText('demo-admin > help');
    await expect(page.locator('#admin-command-log')).toContainText('Available Commands');
  });

  test('admin mutation studio can stage manual test fixtures', async ({ page }) => {
    await login(page, 'admin');
    await seedDemoViaApi(page, true);
    await openAdminConsole(page);

    await switchAdminTab(page, 'mutations');

    await page.locator('#resource-player-select').selectOption('demo-user');
    await page.locator('#resource-gold-delta').fill('0');
    await page.locator('#resource-set-gold').fill('15');
    await page.locator('#mutation-resource-form').getByRole('button', { name: 'Apply Resource Delta' }).click();
    await expect(page.locator('#mutation-log')).toContainText('Adjusted resources for');

    await page.locator('#item-player-select').selectOption('demo-user');
    await page.locator('#item-name').fill('GM Lantern');
    await page.locator('#item-type').selectOption('Misc');
    await page.locator('#item-effect').fill('Illuminates hidden hooks');
    await page.locator('#mutation-item-form').getByRole('button', { name: 'Grant Item' }).click();
    await expect(page.locator('#mutation-log')).toContainText('Granted GM Lantern');

    await page.locator('#status-player-select').selectOption('demo-user');
    await page.locator('#status-name').fill('Focused');
    await page.locator('#status-type').selectOption('Buff');
    await page.locator('#status-modifiers').fill('wis:2');
    await page.locator('#mutation-status-form').getByRole('button', { name: 'Apply Status' }).click();
    await expect(page.locator('#mutation-log')).toContainText('Applied Focused');

    await page.locator('#room-fixture-form #fixture-room-id').fill('qa-lab');
    await page.locator('#fixture-room-name').fill('QA Lab');
    await page.locator('#fixture-room-description').fill('A repeatable manual test fixture room.');
    await page.locator('#fixture-tags').fill('qa,browser');
    await page.locator('#fixture-item-name').fill('Inspection Token');
    await page.locator('#fixture-npc-name').fill('Sentinel');
    await page.locator('#room-fixture-form').getByRole('button', { name: 'Upsert Room Fixture' }).click();
    await expect(page.locator('#mutation-log')).toContainText('QA Lab');

    await page.locator('#teleport-player-select').selectOption('demo-user');
    await page.locator('#teleport-room-id').fill('qa-lab');
    await page.locator('#teleport-room-name').fill('QA Lab');
    await page.locator('#teleport-room-description').fill('A repeatable manual test fixture room.');
    await page.locator('#mutation-teleport-form').getByRole('button', { name: 'Teleport Player' }).click();
    await expect(page.locator('#mutation-log')).toContainText('Teleported Ari Quickstep to QA Lab');

    await switchAdminTab(page, 'players');
    await page.locator('#admin-players-table [data-player-id="demo-user"][data-admin-action="user"]').click();
    await expect(page.locator('#room-name')).toContainText('QA Lab');
    await expect(page.locator('#inventory-list')).toContainText('GM Lantern');
    await expect(page.locator('#status-effects')).toContainText('Focused');
    await expect(page.locator('#char-gold')).toContainText('15');
  });

  test('fluid character payloads render without assuming fixed schemas', async ({ page }) => {
    await login(page, 'admin');

    await page.evaluate(() => {
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.renderPlayer({
        id: 'shape-tester',
        name: 'Shape Walker',
        race: 'Synthetic',
        class: 'Tester',
        currentRoomId: 'lab',
        level: 2,
        hp: 17,
        maxHp: 20,
        mp: 6,
        maxMp: 8,
        xp: 31,
        gold: 44,
        defense: 13,
        str: 12,
        focus: 99,
        spirit: 12,
        alignmentNote: 'Chaotic useful',
        equipment: {
          focusStone: { name: 'Obsidian Focus', charge: 3 },
          cloak: 'Phase Mantle'
        },
        inventory: [
          { title: 'Prototype Keycard', uses: 4 },
          'Loose Note'
        ],
        statusEffects: [
          { label: 'Inspired', stacks: 2 },
          'Blessed'
        ]
      });
    });

    await expect(page.locator('#stats-grid')).toContainText('Focus');
    await expect(page.locator('#character-details')).toContainText('Alignment Note');
    await expect(page.locator('#equipment-slots')).toContainText('Focus Stone');
    await expect(page.locator('#equipment-slots')).toContainText('Cloak');
    await expect(page.locator('#inventory-list')).toContainText('Prototype Keycard');
    await expect(page.locator('#status-effects')).toContainText(/Inspired|Blessed/);
  });

  test('room summaries collapse duplicate NPCs and items into counted labels', async ({ page }) => {
    await login(page, 'admin');

    await page.evaluate(() => {
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.renderRoom({
        id: 'qa-lab',
        name: 'QA Lab',
        description: 'A repeatable manual test fixture room.',
        exits: { south: 'spawn' },
        npcs: [
          { name: 'Sentinel' },
          { name: 'Sentinel' },
          { name: 'Sentinel' },
          { name: 'Mara the Innkeeper' }
        ],
        items: [
          { name: 'Inspection Token', quantity: 1 },
          { name: 'Inspection Token', quantity: 2 },
          { name: 'Field Note', quantity: 1 }
        ]
      });
    });

    await expect(page.locator('#room-summary')).toContainText('NPCs: Sentinel (x3), Mara the Innkeeper');
    await expect(page.locator('#room-summary')).toContainText('Items: Inspection Token (x3), Field Note');
  });

  test('story parser strips room metadata blocks from mixed responses', async ({ page }) => {
    await login(page, 'admin');

    // Stop the refresh loop by clearing all intervals — prevents renderNoActivePlayer
    // from resetting the story log after we inject test DOM.
    await page.evaluate(() => {
      const maxId = window.setInterval(() => {}, 100000);
      for (let i = 1; i <= maxId; i++) window.clearInterval(i);
    });

    await page.evaluate(() => {
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.renderRoom({
        id: 'qa-lab',
        name: 'QA Lab',
        description: 'A repeatable manual test fixture room.',
        exits: { south: 'spawn' },
        npcs: [{ name: 'Sentinel' }],
        items: [{ name: 'Inspection Token', quantity: 1 }]
      });
      UI.$('story-log').innerHTML = '';
      UI.appendStoryEntry({
        narration: 'The sentinels stir as you step forward.',
        mechanicalSummary: '**QA Lab**\nA repeatable manual test fixture room.\nExits: south\nYou see: Sentinel, Sentinel\nItems: Inspection Token, Inspection Token'
      }, 'success');
    });

    await expect(page.locator('#story-log')).toContainText('The sentinels stir as you step forward.');
    await expect(page.locator('#story-log')).not.toContainText('QA Lab');
    await expect(page.locator('#story-log')).not.toContainText('A repeatable manual test fixture room.');
    await expect(page.locator('#story-log')).not.toContainText('Exits: south');
    await expect(page.locator('#story-log')).not.toContainText('Inspection Token, Inspection Token');
  });
});