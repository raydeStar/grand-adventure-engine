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

  async function submitCreateCharacter(page) {
    const responsePromise = page.waitForResponse((response) => {
      if (!response.url().includes('/api/dashboard/characters')) return false;
      return response.request().method() === 'POST';
    });

    await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();
    const createResponse = await responsePromise;
    expect(createResponse.ok()).toBeTruthy();
    return await createResponse.json();
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
    await expect(page.locator('#session-summary')).toContainText('Authentication required.');
    await expect(page.getByRole('button', { name: 'Quick: User' })).toBeVisible();
    await expect(page.locator('#dashboard')).toBeHidden();
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
    await submitCreateCharacter(page);

    await expect(page.locator('#dashboard')).toBeVisible();
    await expect(page.locator('#header-player')).toContainText('Playwright Hero', { timeout: 30_000 });
    await expect(page.locator('#command-input')).toBeEnabled();
    // Room name is theme-dependent; just verify the player spawned in a real room.
    await expect(page.locator('#room-name')).not.toBeEmpty();

    const storyCountBeforeSpark = await page.locator('#story-log .story-entry').count();
    await page.locator('#btn-adventure-spark').click();
    await expect(page.locator('#command-input')).not.toHaveValue('');
    await expect(page.locator('#story-log .story-entry')).toHaveCount(storyCountBeforeSpark);

    await page.locator('#command-input').fill('look');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
    await expect(page.locator('#story-log .story-entry')).not.toHaveCount(0, { timeout: 5_000 });
    await expect(page.locator('#story-log')).not.toContainText('Exits:');
    await expect(page.locator('#story-log')).not.toContainText('You see:');
    await expect(page.locator('#story-log')).not.toContainText('Items:');
    await expect(page.locator('#story-log')).not.toContainText('<think');
    await expect(page.locator('#room-desc')).not.toBeEmpty();

    // Guard the gameplay surface itself: readable on desktop and genuinely
    // usable on a phone, where the header/prompt previously squeezed controls.
    const gameplayLayout = await page.evaluate(() => {
      const rect = (selector) => document.querySelector(selector)?.getBoundingClientRect();
      const header = document.querySelector('.app-header');
      const storyEntry = rect('#story-log .story-entry');
      const roomCopy = rect('.room-panel-copy');
      const commandPrompt = rect('#command-prompt');
      const commandInput = rect('#command-input');
      const portalButton = rect('#btn-open-portal');

      return {
        viewportWidth: window.innerWidth,
        headerClientWidth: header?.clientWidth || 0,
        headerScrollWidth: header?.scrollWidth || 0,
        storyWidth: storyEntry?.width || 0,
        roomCopyWidth: roomCopy?.width || 0,
        roomCopyHeight: roomCopy?.height || 0,
        promptBottom: commandPrompt?.bottom || 0,
        inputTop: commandInput?.top || 0,
        inputWidth: commandInput?.width || 0,
        portalLeft: portalButton?.left || 0,
        portalRight: portalButton?.right || 0
      };
    });

    expect(gameplayLayout.storyWidth).toBeLessThanOrEqual(1041);
    if (testInfo.project.name === 'mobile-chromium') {
      expect(gameplayLayout.headerScrollWidth).toBeLessThanOrEqual(gameplayLayout.headerClientWidth + 1);
      expect(gameplayLayout.portalLeft).toBeGreaterThanOrEqual(0);
      expect(gameplayLayout.portalRight).toBeLessThanOrEqual(gameplayLayout.viewportWidth + 1);
      expect(gameplayLayout.inputWidth).toBeGreaterThan(220);
      expect(gameplayLayout.promptBottom).toBeLessThanOrEqual(gameplayLayout.inputTop + 1);
      expect(gameplayLayout.roomCopyWidth).toBeGreaterThan(100);
      expect(gameplayLayout.roomCopyHeight).toBeGreaterThan(20);
    }

    await page.locator('#command-input').fill('stats');
    await page.getByRole('button', { name: 'Send' }).click();
    await expect(page.locator('#command-input')).toBeDisabled({ timeout: 3_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });

    await expect(page.getByRole('button', { name: 'Open Admin Console' })).toHaveCount(0);
  });

  test('story log replaces same-length batches when the active story changes', async ({ page }, testInfo) => {
    const firstName = `First Switch ${testInfo.project.name}`;
    const secondName = `Second Switch ${testInfo.project.name}`;

    await login(page, 'admin');
    await page.evaluate((name) => {
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.$('story-log').innerHTML = '';
      UI._lastStoryCount = 0;
      UI._lastStorySignature = '';
      UI._renderedActionIds.clear();
      UI.renderStoryLog([{
        id: 'story-first',
        rawInput: 'character-creation',
        narration: `${name} the Human Warrior enters the world.`
      }]);
    }, firstName);

    await expect(page.locator('#story-log .story-entry')).toHaveCount(1);
    await expect(page.locator('#story-log')).toContainText(firstName);
    await expect(page.locator('#story-log')).not.toContainText('<think');

    await page.evaluate((name) => {
      UI.renderStoryLog([{
        id: 'story-second',
        rawInput: 'character-creation',
        narration: `${name} the Human Warrior enters the world.`
      }]);
    }, secondName);

    await expect(page.locator('#story-log .story-entry')).toHaveCount(1);
    await expect(page.locator('#story-log')).toContainText(secondName);
    await expect(page.locator('#story-log')).not.toContainText(firstName);
    await expect(page.locator('#story-log')).not.toContainText('<think');
  });

  test('story polling preserves locally appended entries and an active pending indicator', async ({ page }) => {
    await login(page, 'admin');

    const result = await page.evaluate(() => {
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.$('story-log').innerHTML = '';
      UI._lastStoryCount = 0;
      UI._lastStorySignature = '';
      UI._renderedActionIds.clear();

      const first = { id: 'story-first', rawInput: 'look', narration: 'The room waits.' };
      const second = { id: 'story-second', rawInput: 'listen', narration: 'A floorboard creaks.' };
      UI.renderStoryLog([first]);
      const firstNode = UI.$('story-log').querySelector('[data-action-id="story-first"]');
      UI.appendStoryEntry(second, 'success');
      UI._cancelStreaming();
      const secondNode = UI.$('story-log').querySelector('[data-action-id="story-second"]');

      UI.renderStoryLog([second, first]);
      const entriesPreserved = firstNode === UI.$('story-log').querySelector('[data-action-id="story-first"]')
        && secondNode === UI.$('story-log').querySelector('[data-action-id="story-second"]');

      UI.beginPendingAction('search the rafters');
      const pendingNode = UI._pendingNode;
      UI.renderStoryLog([second, first]);
      const pendingPreserved = pendingNode === UI._pendingNode && UI.$('story-log').contains(pendingNode);
      UI.endPendingAction();

      return { entriesPreserved, pendingPreserved };
    });

    expect(result.entriesPreserved).toBeTruthy();
    expect(result.pendingPreserved).toBeTruthy();
  });

  test('realtime feed join failures fall back to polling', async ({ page }) => {
    await login(page, 'admin');

    const result = await page.evaluate(async () => {
      window.signalR = window.signalR || { HubConnectionState: { Connected: 'Connected' } };
      GameHub.connection = {
        state: window.signalR?.HubConnectionState?.Connected ?? 'Connected',
        invoke: async () => { throw new Error('join denied'); }
      };
      GameHub.realtimeEnabled = true;

      const statusEvents = [];
      GameHub.on('status', status => statusEvents.push(status));
      const joined = await GameHub.joinPlayerFeed('missing-player');

      return {
        joined,
        realtimeEnabled: GameHub.realtimeEnabled,
        statusEvents
      };
    });

    expect(result.joined).toBeFalsy();
    expect(result.realtimeEnabled).toBeFalsy();
    expect(result.statusEvents).toContain('polling');
  });

  test('admin event log treats player IDs and summaries as text, never markup', async ({ page }) => {
    await login(page, 'admin');

    await page.evaluate(() => {
      UI.renderEventLog([{
        typeName: 'SystemMessage',
        timestamp: new Date().toISOString(),
        playerId: '\"><img src=x onerror="window.__eventLogXss=true">',
        summary: '<svg onload="window.__eventLogXss=true">untrusted summary</svg>'
      }], '');
      UI.renderRegistryList('items', [{
        id: '\"><img src=x onerror="window.__registryXss=true">',
        name: 'Untrusted Trinket',
        tags: []
      }]);
    });

    await expect(page.locator('#event-log-list img, #event-log-list svg')).toHaveCount(0);
    await expect(page.locator('#event-log-list')).toContainText('<img src=x');
    await expect(page.locator('#event-log-list')).toContainText('<svg onload=');
    await expect(page.locator('#registry-list img, #registry-list svg')).toHaveCount(0);
    await expect(page.locator('#registry-list .registry-entry')).toHaveAttribute(
      'data-registry-id',
      '\"><img src=x onerror="window.__registryXss=true">'
    );
    expect(await page.evaluate(() => window.__eventLogXss === true)).toBeFalsy();
    expect(await page.evaluate(() => window.__registryXss === true)).toBeFalsy();
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
    await submitCreateCharacter(page);

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

    await page.locator('#btn-seed-demo-admin').click();
    await expect(page.locator('#portal-message')).toContainText(/Seeded|already existed/i);

    await openAdminConsole(page);
    await switchAdminTab(page, 'overview');
    await expect(page.locator('#summary-cards .summary-card')).toHaveCount(6);
    await page.getByRole('button', { name: 'All Players' }).click();
    await expect(page.locator('#overview-results [data-ov-id="demo-user"]')).toBeVisible();
    await page.locator('#overview-results [data-ov-id="demo-user"]').click();
    await expect(page.locator('#overview-detail-panel')).toContainText('Equipment');
    await expect(page.locator('#overview-detail-panel')).toContainText('Inventory Items');
    await expect(page.locator('#overview-detail-panel .dm-inline-action')).not.toHaveCount(0);

    await switchAdminTab(page, 'tools');
    await expect(page.locator('#admin-player-select')).toContainText('demo-user');
    await expect(page.locator('#admin-player-select')).toContainText('demo-admin');
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

    // ── Room fixture (tools tab) — the only surviving standalone mutation form ──
    await switchAdminTab(page, 'tools');
    await page.locator('details:has(#room-fixture-form) > summary').click();
    await page.locator('#room-fixture-form #fixture-room-id').fill('qa-lab');
    await page.locator('#fixture-room-name').fill('QA Lab');
    await page.locator('#fixture-room-description').fill('A repeatable manual test fixture room.');
    await page.locator('#fixture-tags').fill('qa,browser');
    await page.locator('#fixture-item-name').fill('Inspection Token');
    await page.locator('#fixture-npc-name').fill('Sentinel');
    await page.locator('#room-fixture-form').getByRole('button', { name: 'Upsert Room Fixture' }).click();
    await expect(page.locator('#mutation-log')).toContainText('QA Lab');

    // ── Player mutations via API (standalone mutation forms were removed; ──
    // ── inline overview actions are the new UI but live in a detail panel) ──
    await page.evaluate(async () => {
      await API.adjustResources({ playerId: 'demo-user', setGold: 15 });
      await API.grantItem({ playerId: 'demo-user', name: 'GM Lantern', type: 'Misc', quantity: 1, effect: 'Illuminates hidden hooks' });
      await API.applyStatus({ playerId: 'demo-user', name: 'Focused', type: 'Buff', remainingTurns: 3 });
      await API.teleportPlayer({ playerId: 'demo-user', roomId: 'qa-lab', roomName: 'QA Lab', roomDescription: 'A repeatable manual test fixture room.' });
    });

    // ── Verify state via overview detail panel ──
    await switchAdminTab(page, 'overview');
    await page.getByRole('button', { name: 'All Players' }).click();
    await expect(page.locator('#overview-results [data-ov-id="demo-user"]')).toBeVisible();
    await page.locator('#overview-results [data-ov-id="demo-user"]').click();
    await expect(page.locator('#overview-detail-panel')).toContainText('GM Lantern');

    // ── Switch to play mode and verify rendered state ──
    // Use dispatchEvent since the Play button is obscured on narrow viewports
    await page.evaluate(() => {
      document.dispatchEvent(new CustomEvent('overview-play-player', { detail: { playerId: 'demo-user' } }));
    });
    await expect(page.locator('#room-name')).toContainText('QA Lab');
    await expect(page.locator('#stat-bar .hud-gold')).toContainText('15');

    const playerSnapshot = await page.evaluate(async () => {
      const player = await API.getPlayer('demo-user');
      return {
        roomId: player.currentRoomId,
        gold: player.gold,
        inventoryText: JSON.stringify(player.inventory || []),
        statusText: JSON.stringify(player.statusEffects || [])
      };
    });

    expect(playerSnapshot.roomId).toBe('qa-lab');
    expect(playerSnapshot.gold).toBe(15);
    expect(playerSnapshot.inventoryText).toContain('GM Lantern');
    expect(playerSnapshot.statusText).toContain('Focused');
  });

  test('fluid character payloads render without assuming fixed schemas', async ({ page }) => {
    await login(page, 'admin');

    const renderSnapshot = await page.evaluate(() => {
      const player = {
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
      };

      UI.showDashboard(true);
      UI.showPortal(false);
      UI.renderPlayer(player);

      return {
        header: document.getElementById('header-player')?.textContent || '',
        statBar: document.getElementById('stat-bar')?.textContent || '',
        stats: UI.getStatEntries(player).map((entry) => entry.label),
        details: UI.getDetailEntries(player).map((entry) => `${entry.label}:${entry.value}`),
        equipment: Object.entries(player.equipment || {}).map(([key, value]) => `${UI.humanizeKey(key)}:${UI.summarizeEntity(value)}`),
        inventory: (player.inventory || []).map((item) => UI.summarizeEntity(item)),
        statusEffects: (player.statusEffects || []).map((effect) => UI.summarizeEntity(effect))
      };
    });

    expect(renderSnapshot.header).toContain('Shape Walker');
    expect(renderSnapshot.statBar).toContain('◈ 44');
    expect(renderSnapshot.stats).toContain('Focus');
    expect(renderSnapshot.details).toContain('Alignment Note:Chaotic useful');
    expect(renderSnapshot.equipment).toContain('Focus Stone:Obsidian Focus');
    expect(renderSnapshot.equipment).toContain('Cloak:Phase Mantle');
    expect(renderSnapshot.inventory).toContain('Prototype Keycard');
    expect(renderSnapshot.statusEffects).toContain('Inspired');
    expect(renderSnapshot.statusEffects).toContain('Blessed');
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
        mechanicalSummary: '**QA Lab**\n\nA repeatable manual test fixture room.\n\n  - Sentinel\n  - Sentinel\n  * Inspection Token\n  * Inspection Token\n\n**Exits:** south'
      }, 'success');
    });

    await expect(page.locator('#story-log')).toContainText('The sentinels stir as you step forward.');
    await expect(page.locator('#story-log')).not.toContainText('QA Lab');
    await expect(page.locator('#story-log')).not.toContainText('A repeatable manual test fixture room.');
    await expect(page.locator('#story-log')).not.toContainText('Sentinel');
    await expect(page.locator('#story-log')).not.toContainText('Inspection Token');
    await expect(page.locator('#story-log')).not.toContainText('Exits: south');
  });

  test('WebMCP Co-DM registers bounded tools and keeps human approval authoritative', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.removeItem('gae.coDm.selectedPlayer');
      localStorage.removeItem('gae.coDm.activity');
      localStorage.removeItem('gae.coDm.proposals');
      window.__gaeRegisteredTools = [];
      Object.defineProperty(document, 'modelContext', {
        configurable: true,
        value: {
          registerTool: async (tool) => {
            window.__gaeRegisteredTools.push(tool);
          }
        }
      });
    });

    await login(page, 'admin');
    await seedDemoViaApi(page, true);
    await openAdminConsole(page);
    await switchAdminTab(page, 'overview');

    await page.waitForFunction(() => window.__gaeRegisteredTools?.length === 5);
    const registration = await page.evaluate(() => window.__gaeRegisteredTools.map((tool) => ({
      name: tool.name,
      additionalProperties: tool.inputSchema.additionalProperties,
      readOnlyHint: tool.annotations.readOnlyHint
    })));
    expect(registration.map((tool) => tool.name)).toEqual([
      'get_dm_context',
      'search_world',
      'inspect_entity',
      'send_dm_message',
      'propose_dm_intervention'
    ]);
    expect(registration.every((tool) => tool.additionalProperties === false)).toBeTruthy();
    expect(registration.find((tool) => tool.name === 'get_dm_context').readOnlyHint).toBeTruthy();
    expect(registration.find((tool) => tool.name === 'propose_dm_intervention').readOnlyHint).toBeFalsy();

    const contextResult = await page.evaluate(async () => {
      const original = window.GaeCoDm.getContext.bind(window.GaeCoDm);
      window.__sharedContextCalls = 0;
      window.GaeCoDm.getContext = async (input) => {
        window.__sharedContextCalls += 1;
        return await original(input);
      };
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'get_dm_context');
      return await tool.execute({ playerId: 'demo-user', storyLimit: 6 });
    });
    expect(contextResult.ok).toBeTruthy();
    expect(contextResult.data.mode).toBe('co-dm');
    expect(contextResult.data.player.id).toBe('demo-user');
    expect(await page.evaluate(() => window.__sharedContextCalls)).toBe(1);
    await expect(page.locator('#co-dm-player-select')).toHaveValue('demo-user');
    await expect(page.locator('#co-dm-scene')).toContainText('demo-user');

    const searchResult = await page.evaluate(async () => {
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'search_world');
      return await tool.execute({ query: 'Mara', type: 'npc', limit: 5 });
    });
    expect(searchResult.ok).toBeTruthy();
    expect(searchResult.data.results.some((entry) => entry.type === 'npc')).toBeTruthy();
    await expect(page.locator('#overview-results .dm-result-card')).not.toHaveCount(0);

    await page.evaluate(async () => {
      window.__mutationCalls = 0;
      for (const method of ['grantItem', 'applyStatus', 'adjustResources', 'teleportPlayer']) {
        const original = API[method].bind(API);
        API[method] = async (...args) => {
          window.__mutationCalls += 1;
          return await original(...args);
        };
      }
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'propose_dm_intervention');
      await tool.execute({
        playerId: 'demo-user',
        kind: 'apply_status',
        title: 'Mark the claim as suspicious',
        rationale: 'Mara and the room evidence do not support the player claim.',
        evidenceIds: ['spawn', 'mara'],
        statusName: 'Under Suspicion',
        statusDescription: 'The innkeeper is watching closely.',
        durationTurns: 3
      });
    });
    await expect(page.locator('#co-dm-proposals .co-dm-proposal').first()).toContainText('pending');
    await page.locator('#co-dm-proposals [data-co-dm-reject]').first().click();
    await expect(page.locator('#co-dm-proposals .co-dm-proposal').first()).toContainText('rejected');
    expect(await page.evaluate(() => window.__mutationCalls)).toBe(0);

    const originalPlayer = await page.evaluate(() => API.getPlayer('demo-user'));
    await page.evaluate(async () => {
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'propose_dm_intervention');
      await tool.execute({
        playerId: 'demo-user',
        kind: 'adjust_resources',
        title: 'Award one gold',
        rationale: 'Small deterministic approval-path check.',
        evidenceIds: ['demo-user'],
        goldDelta: 1
      });
    });
    await page.locator('#co-dm-proposals [data-co-dm-approve]:not([disabled])').first().click();
    await expect(page.locator('#co-dm-proposals .co-dm-proposal').first()).toContainText('approved');
    const updatedPlayer = await page.evaluate(() => API.getPlayer('demo-user'));
    expect(updatedPlayer.gold).toBe(originalPlayer.gold + 1);
    expect(await page.evaluate(() => window.__mutationCalls)).toBe(1);

    await page.evaluate(async () => {
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'propose_dm_intervention');
      await tool.execute({
        playerId: 'demo-user',
        kind: 'apply_status',
        title: 'Expose approved status evidence',
        rationale: 'The Co-DM must verify its approved consequence in refreshed context.',
        evidenceIds: ['demo-user'],
        statusName: 'QA Watch',
        statusDescription: 'Visible proof that approved status effects return to the shared context.',
        durationTurns: 2
      });
    });
    await page.locator('#co-dm-proposals [data-co-dm-approve]:not([disabled])').first().click();
    await expect(page.locator('#co-dm-proposals .co-dm-proposal').first()).toContainText('approved');
    const refreshedContext = await page.evaluate(async () => {
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'get_dm_context');
      return await tool.execute({ playerId: 'demo-user', storyLimit: 6 });
    });
    const qaStatus = refreshedContext.data.statusEffects.find((effect) => effect.name === 'QA Watch');
    expect(qaStatus).toMatchObject({ type: 'debuff', remainingTurns: 2 });
    const statusDetails = page.locator('#co-dm-scene details').filter({ hasText: 'Status effects' });
    await expect(statusDetails).toHaveAttribute('open', '');
    await expect(statusDetails).toContainText('QA Watch');
    expect(await page.evaluate(() => window.__mutationCalls)).toBe(2);

    const dmMessage = `The ledger remembers, even when adventurers do not. ${Date.now()}`;
    const messageResult = await page.evaluate(async (message) => {
      const tool = window.__gaeRegisteredTools.find((entry) => entry.name === 'send_dm_message');
      return await tool.execute({ playerId: 'demo-user', message });
    }, dmMessage);
    expect(messageResult.ok).toBeTruthy();
    expect(messageResult.data.player.id).toBe('demo-user');
    expect(messageResult.data.server.sent).toBe(1);
    expect(messageResult.data.storyReceipt.narration).toBe(dmMessage);

    await page.evaluate(() => {
      document.dispatchEvent(new CustomEvent('overview-play-player', { detail: { playerId: 'demo-user' } }));
    });
    await expect(page.locator('#story-log')).toContainText(dmMessage, { timeout: 15_000 });
  });

  test('dashboard remains usable when WebMCP is unavailable', async ({ page }) => {
    await login(page, 'admin');
    await openAdminConsole(page);
    await switchAdminTab(page, 'overview');
    await expect(page.locator('#co-dm-status')).toContainText('WebMCP supported');
    await expect(page.locator('#co-dm-status')).toContainText('no');
    await expect(page.locator('#overview-search-input')).toBeEnabled();
  });
});
