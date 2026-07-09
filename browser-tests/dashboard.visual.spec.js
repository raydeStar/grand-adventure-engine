const { test, expect } = require('@playwright/test');

const visualLoginHints = [
  {
    username: 'user',
    role: 'user',
    displayName: 'User Workspace'
  },
  {
    username: 'admin',
    role: 'admin',
    displayName: 'Admin Console'
  }
];

const userVisualSession = {
  username: 'user',
  role: 'user',
  displayName: 'User Workspace',
  isAdmin: false
};

const adminVisualSession = {
  username: 'admin',
  role: 'admin',
  displayName: 'Admin Console',
  isAdmin: true
};

const adminVisualPlayers = [
  { id: 'demo-user', name: 'Ari Quickstep', level: 2, race: 'Human', class: 'Ranger', currentRoomId: 'qa-lab' },
  { id: 'demo-admin', name: 'Marshal Vale', level: 2, race: 'Elf', class: 'Mage', currentRoomId: 'spawn' }
];

const adminPanelIds = [
  'admin-overview-panel',
  'admin-workflow-panel',
  'admin-command-panel',
  'admin-mutation-panel',
  'admin-registry-panel',
  'admin-room-catalogue-panel',
  'admin-payload-panel',
  'admin-notes-panel'
];

const adminMobilePanelSnapshots = [
  { panelId: 'admin-command-panel', heading: /Command Harness/i, snapshotName: 'admin-command.png' }
];

async function resolveAdminPanelLocator(page, panelId, heading) {
  const panelById = page.locator(`#${panelId}`);
  if (await panelById.count()) {
    return panelById.first();
  }

  const panelByHeading = page
    .locator('.admin-grid .panel')
    .filter({ has: page.getByRole('heading', { name: heading }) });
  return panelByHeading.first();
}

async function openVisualHarness(page) {
  await page.addInitScript(() => {
    sessionStorage.setItem('gae.booted', '1');
  });
  await page.goto('/');
  await page.waitForFunction(() => typeof UI !== 'undefined' && typeof GameHub !== 'undefined');
}

async function renderStableAdminVisualState(page) {
  await page.evaluate(async ({ session, players, panelIds, loginHints }) => {
    await GameHub.disconnect();

    UI.showDashboard(true);
    UI.showPortal(false);
    UI.showCreateForm(false);
    UI.setPortalMessage('');
    UI.setAuthMessage('');
    UI.setSession(session, loginHints);
    UI.setMode('admin', session);
    UI.setConnectionStatus('connected');
    UI.renderNoActivePlayer(true);
    UI.renderSummary({
      playerCount: 4,
      activePlayerCount: 3,
      roomCount: 7,
      discoveredRoomCount: 5,
      storyEntryCount: 28
    }, 'SignalR', { isAdmin: true });
    UI.renderHealth({
      health: { ok: true, status: 'healthy' },
      'health/wiki': { ok: false, status: 'degraded' },
      'health/narrator': { ok: true, status: 'healthy' }
    });
    UI.renderAdminPlayers(players, 'demo-user', { isAdmin: true });
    UI.renderRoomCatalogue([
      { id: 'spawn', name: 'The Crossroads Inn', description: 'Launch room for most runs.', isDiscovered: true, exitCount: 3, npcCount: 2, itemCount: 1 },
      { id: 'qa-lab', name: 'QA Lab', description: 'Fixture room with seeded hooks for manual test passes.', isDiscovered: true, exitCount: 2, npcCount: 1, itemCount: 1 }
    ]);
    UI.renderPayloads(
      {
        id: 'demo-user',
        name: 'Ari Quickstep',
        currentRoomId: 'qa-lab',
        inventory: [{ name: 'GM Lantern' }],
        statusEffects: [{ name: 'Focused' }]
      },
      {
        id: 'qa-lab',
        name: 'QA Lab',
        exits: { south: 'spawn' },
        items: [{ name: 'Inspection Token' }]
      }
    );
    UI.renderSelectOptions(players, 'demo-user');

    const setVal = (id, v) => { const el = document.getElementById(id); if (el) el.value = v; };
    setVal('workflow-player-select', 'demo-user');
    setVal('admin-player-select', 'demo-admin');
    setVal('resource-player-select', 'demo-user');
    setVal('teleport-player-select', 'demo-user');
    setVal('item-player-select', 'demo-user');
    setVal('status-player-select', 'demo-user');
    setVal('msg-player-select', '');
    setVal('transfer-player-select', 'demo-user');

    const setHtml = (id, html) => { const el = document.getElementById(id); if (el) el.innerHTML = html; };
    setHtml('workflow-log', '<div class="activity-item success"><div class="activity-meta">10:42:00 AM</div><div class="activity-text">Smoke workflow completed for demo-user.</div></div>');
    setHtml('admin-command-log', '<div class="activity-item info"><div class="activity-meta">10:43:00 AM</div><div class="activity-text">demo-admin > help</div></div><div class="activity-item success"><div class="activity-meta">10:43:01 AM</div><div class="activity-text">Available Commands rendered cleanly.</div></div>');
    setHtml('mutation-log', '<div class="activity-item success"><div class="activity-meta">10:44:00 AM</div><div class="activity-text">Teleported Ari Quickstep to QA Lab.</div></div>');

    for (const panelId of panelIds) {
      document.getElementById(panelId)?.classList.remove('hidden');
    }

    window.scrollTo(0, 0);
  }, {
    session: adminVisualSession,
    players: adminVisualPlayers,
    panelIds: adminPanelIds,
    loginHints: visualLoginHints
  });
}

test.describe('Grand Adventure Engine visual baselines', () => {
  test('@visual portal layout remains stable', async ({ page }) => {
    await openVisualHarness(page);

    await page.evaluate(({ session, loginHints }) => {
      UI.setSession(session, loginHints);
      UI.showPortal(true);
      UI.showDashboard(false);
      UI.showCreateForm(false);
      UI.setPortalMessage('');
      UI.setAuthMessage('');
      UI.renderPortalPlayers([
        {
          id: 'demo-user',
          name: 'Ari Quickstep',
          level: 2,
          race: 'Human',
          class: 'Ranger',
          currentRoomId: 'qa-lab'
        },
        {
          id: 'demo-admin',
          name: 'Marshal Vale',
          level: 2,
          race: 'Elf',
          class: 'Mage',
          currentRoomId: 'spawn'
        },
        {
          id: 'visual-smoke',
          name: 'Playwright Hero',
          level: 1,
          race: 'Human',
          class: 'Warrior',
          currentRoomId: 'spawn'
        }
      ], 'demo-user', {
        username: session.username,
        role: session.role,
        displayName: session.displayName,
        isAdmin: session.isAdmin
      });
      const overlay = document.getElementById('portal-overlay');
      if (overlay) {
        overlay.scrollTop = 0;
      }
    }, {
      session: adminVisualSession,
      loginHints: visualLoginHints
    });

    await expect(page.locator('#portal-overlay')).toHaveScreenshot('portal-shell.png', {
      animations: 'disabled'
    });
  });

  test('@visual user workspace remains stable', async ({ page }) => {
    await openVisualHarness(page);

    await page.evaluate(({ session, loginHints }) => {
      UI.setSession(session, loginHints);
      UI.showDashboard(true);
      UI.showPortal(false);
      UI.setMode('user', session);
      UI.setConnectionStatus('connected');
      UI.renderPlayer({
        id: 'demo-user',
        name: 'Ari Quickstep',
        race: 'Human',
        class: 'Ranger',
        currentRoomId: 'spawn',
        hp: 18,
        maxHp: 22,
        mp: 7,
        maxMp: 10,
        xp: 42,
        gold: 19,
        level: 2,
        defense: 14,
        str: 11,
        dex: 16,
        con: 13,
        int: 10,
        wis: 14,
        cha: 12,
        luck: 9,
        equipment: {
          weapon: { name: 'Ashwood Bow' },
          armor: { name: 'Trail Leathers' },
          shield: null,
          helmet: null
        },
        inventory: [
          { name: 'Field Journal', quantity: 1 },
          { name: 'Trail Rations', quantity: 2 }
        ],
        statusEffects: [
          { name: 'Focused' }
        ]
      });
      UI.renderRoom({
        id: 'spawn',
        name: 'The Crossroads Inn',
        description: 'A weathered inn at a three-road junction with fresh tracks across the yard.',
        exits: {
          north: 'pine-road',
          east: 'market-lane',
          south: 'river-step'
        },
        npcs: [
          { name: 'Mara', personality: 'Watchful innkeeper' },
          { name: 'Bran', personality: 'Courier with too much news' }
        ],
        items: [
          { name: 'Dropped Map', quantity: 1 }
        ]
      });
      UI.renderStoryLog([
        {
          narration: 'Ari steadies the party at the inn threshold and takes stock of every path out.',
          mechanicalSummary: 'look'
        },
        {
          narration: 'The innkeeper marks a northern route as safe enough for first-step testing.',
          mechanicalSummary: 'talk to mara'
        }
      ]);
      UI.renderPlayersList([
        { id: 'demo-user', name: 'Ari Quickstep', level: 2, race: 'Human', class: 'Ranger', currentRoomId: 'spawn' },
        { id: 'demo-ally', name: 'Marshal Vale', level: 2, race: 'Elf', class: 'Mage', currentRoomId: 'spawn' }
      ], 'demo-user', { isAdmin: false });
      UI.renderHealth({
        health: { ok: true, status: 'healthy' },
        'health/wiki': { ok: false, status: 'degraded' },
        'health/narrator': { ok: true, status: 'healthy' }
      });
    }, {
      session: userVisualSession,
      loginHints: visualLoginHints
    });

    await expect(page.locator('#dashboard')).toHaveScreenshot('user-workspace.png', {
      animations: 'disabled'
    });
  });

  test('@visual admin console remains stable', async ({ page }, testInfo) => {
    await openVisualHarness(page);

    await renderStableAdminVisualState(page);

    if (testInfo.project.name.includes('mobile')) {
      await page.evaluate(() => {
        const header = document.querySelector('.app-header');
        if (header) {
          header.style.position = 'static';
          header.style.top = 'auto';
        }
      });

      for (const { panelId, heading, snapshotName } of adminMobilePanelSnapshots) {
        const panel = await resolveAdminPanelLocator(page, panelId, heading);
        if (await panel.count() === 0) continue;
        await expect(panel).toBeVisible({ timeout: 15000 });
        await panel.scrollIntoViewIfNeeded();
        await expect(panel).toHaveScreenshot(snapshotName, {
          timeout: 15000,
          animations: 'disabled'
        });
      }
      return;
    }

    await expect(page.locator('#dashboard')).toHaveScreenshot('admin-console.png', {
      animations: 'disabled'
    });
  });
});
