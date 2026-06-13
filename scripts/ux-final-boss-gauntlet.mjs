import { chromium, expect } from '@playwright/test';

const baseURL = process.env.GAE_BASE_URL || process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:8181';
const account = {
  username: process.env.GAE_DASHBOARD_ADMIN_USERNAME || 'admin',
  password: process.env.GAE_DASHBOARD_ADMIN_PASSWORD || 'GAE-Admin-Local!123'
};

const playerId = `ux-boss-${Date.now()}`;
const playerName = `Bonk Endwalker ${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`;

const combatRooms = [
  { roomId: 'water_temple_sanctum', label: 'Water guardian', crystal: 'water crystal' },
  { roomId: 'fire_temple_core', label: 'Fire guardian', crystal: 'fire crystal' },
  { roomId: 'earth_temple_heart', label: 'Earth guardian', crystal: 'earth crystal' },
  { roomId: 'wind_temple_apex', label: 'Wind guardian', crystal: 'wind crystal' }
];

const finalRoute = [
  { roomId: 'void_rift', move: null, label: 'Void Rift' },
  { roomId: 'void_corridor', move: 'go down', label: 'Void corridor' },
  { roomId: 'void_throne', move: 'go north', label: 'Final throne' }
];

const fallbackBosses = {
  water_temple_sanctum: {
    npcId: 'leviathan_guardian',
    name: 'Leviathan, the Tidal Lord',
    personality: 'An ancient water guardian who tests mortals for the Water Crystal.',
    level: 3,
    hp: 25,
    maxHp: 25,
    attackBonus: 3,
    damageDice: '1d6+1',
    defense: 12
  },
  fire_temple_core: {
    npcId: 'ifrit_guardian',
    name: 'Ifrit, the Infernal Lord',
    personality: 'An ancient fire guardian who roars challenges from the magma.',
    level: 4,
    hp: 35,
    maxHp: 35,
    attackBonus: 3,
    damageDice: '1d6+2',
    defense: 12
  },
  earth_temple_heart: {
    npcId: 'titan_guardian',
    name: 'Titan, the Earthshaker',
    personality: 'An ancient earth guardian whose patience is geological and whose fists are mountains.',
    level: 4,
    hp: 38,
    maxHp: 38,
    attackBonus: 3,
    damageDice: '1d6+2',
    defense: 13
  },
  wind_temple_apex: {
    npcId: 'garuda_guardian',
    name: 'Garuda, the Storm Empress',
    personality: 'An ancient wind guardian who treats battle as art above the clouds.',
    level: 4,
    hp: 40,
    maxHp: 40,
    attackBonus: 4,
    damageDice: '1d6+2',
    defense: 12
  },
  void_rift: {
    npcId: 'void_wraith',
    name: 'Void Wraith',
    personality: 'A hollow thing of void energy that hungers for light.',
    level: 5,
    hp: 28,
    maxHp: 28,
    attackBonus: 5,
    damageDice: '2d6+1',
    defense: 13
  },
  void_corridor: {
    npcId: 'void_knight',
    name: 'Void Knight',
    personality: 'A fallen champion rebuilt from nothingness.',
    level: 5,
    hp: 30,
    maxHp: 30,
    attackBonus: 6,
    damageDice: '2d6+2',
    defense: 14
  },
  void_throne: {
    npcId: 'exdeath_void',
    name: 'Exdeath, Master of the Void',
    personality: 'The final boss, theatrical and convinced that nothingness is perfection.',
    level: 6,
    hp: 55,
    maxHp: 55,
    attackBonus: 4,
    damageDice: '1d8+2',
    defense: 14
  }
};

function summarize(text, max = 260) {
  const cleaned = String(text || '').replace(/\s+/g, ' ').trim();
  return cleaned.length > max ? `${cleaned.slice(0, max - 1)}...` : cleaned;
}

function scoreStep(label, state, result = null) {
  const issues = [];
  let score = 10;
  const story = state.storyText || '';
  const lowerStory = story.toLowerCase();
  const narration = result?.narration || '';
  const mechanical = result?.mechanicalSummary || '';
  const combined = `${mechanical} ${narration}`;

  if (state.inputDisabled) {
    score -= 2;
    issues.push('command input remained disabled');
  }
  if (state.inputHidden) {
    score -= 4;
    issues.push('command input was hidden');
  }
  if (lowerStory.includes('<think')) {
    score -= 5;
    issues.push('thinking text leaked into story log');
  }
  if (/(\bExits:|\bNPCs:|\bItems:|You see:)/i.test(story)) {
    score -= 3;
    issues.push('room metadata leaked into story log');
  }
  if (result && result.success === false && !mechanical && !narration) {
    score -= 4;
    issues.push('failed without useful feedback');
  }
  if (/Automation error/i.test(mechanical)) {
    score -= 7;
    issues.push('automation could not use the visible command input');
  }
  if (result && !mechanical && !narration) {
    score -= 2;
    issues.push('no mechanical or narrative response');
  }
  if (result && /\?/.test(narration)) {
    score -= 3;
    issues.push('narration asked a question');
  }
  if (/nothing happens|doesn't understand|cannot process|try again/i.test(combined)) {
    score -= 3;
    issues.push('response felt generic or non-consequential');
  }
  if (/target .*not found|was not found|don't see .* anywhere/i.test(combined)) {
    score -= 5;
    issues.push('expected target was not reachable by natural phrasing');
  }
  if (/moment passes|without any dramatic consequences|doesn't quite land|try (?:using )?(?:\*\*)?talk to/i.test(combined)) {
    score -= 3;
    issues.push('response felt like a shrug instead of a consequence');
  }
  if (result && result.success === false) {
    score -= 1;
    issues.push('interaction failed');
  }
  if (state.storyEntryCount === 0) {
    score -= 2;
    issues.push('story log has no entries');
  }
  if (!state.roomName || state.roomName === 'No active room') {
    score -= 1;
    issues.push('room context was missing');
  }

  return {
    label,
    score: Math.max(0, score),
    issues,
    room: state.roomName,
    prompt: state.prompt,
    storyEntryCount: state.storyEntryCount,
    success: result?.success ?? null,
    mechanical: summarize(mechanical, 240),
    narration: summarize(narration, 240),
    visibleStory: summarize(story, 360)
  };
}

async function readState(page) {
  return await page.evaluate(() => {
    const storyLog = document.getElementById('story-log');
    const entries = Array.from(storyLog?.querySelectorAll('.story-entry') || []);
    return {
      header: document.getElementById('header-player')?.textContent?.trim() || '',
      roomName: document.getElementById('room-name')?.textContent?.trim() || '',
      prompt: document.getElementById('prompt-label')?.textContent?.trim() || '',
      inputDisabled: !!document.getElementById('command-input')?.disabled,
      inputHidden: (() => {
        const input = document.getElementById('command-input');
        if (!input) return true;
        const rect = input.getBoundingClientRect();
        const style = window.getComputedStyle(input);
        return rect.width === 0 || rect.height === 0 || style.visibility === 'hidden' || style.display === 'none';
      })(),
      storyEntryCount: entries.length,
      storyText: entries.slice(-4).map(e => e.textContent || '').join('\n---\n')
    };
  });
}

async function login(page) {
  await page.goto(baseURL);
  await page.locator('#auth-username').fill(account.username);
  await page.locator('#auth-password').fill(account.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await expect(page.locator('#dashboard')).toBeVisible({ timeout: 30_000 });
  await page.locator('[data-mode-button="user"]').click();
  await expect(page.locator('#btn-new-char')).toBeVisible({ timeout: 10_000 });
}

async function createCharacter(page) {
  await page.getByRole('button', { name: 'New Character' }).click();
  await expect(page.locator('#create-form')).toBeVisible({ timeout: 10_000 });
  await page.locator('#char-name').fill(playerName);
  await page.locator('#char-player-id').fill(playerId);
  await page.locator('#char-race').selectOption('Human');
  await page.locator('#char-class').selectOption('Warrior');
  await page.locator('#char-backstory').fill('A reckless champion speedrunning destiny for UX science.');

  const responsePromise = page.waitForResponse(response =>
    response.url().includes('/api/dashboard/characters') && response.request().method() === 'POST',
    { timeout: 180_000 });
  await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();
  const response = await responsePromise;
  const body = await response.json().catch(() => ({}));
  if (!response.ok()) {
    throw new Error(`Character create returned HTTP ${response.status()}: ${JSON.stringify(body).slice(0, 600)}`);
  }
  await expect(page.locator('#header-player')).toContainText(playerName, { timeout: 30_000 });
  await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
  return body;
}

async function adminPost(page, path, body) {
  return await page.evaluate(async ({ path, body }) => {
    const response = await fetch(`/api/dashboard/${path}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(`${path} failed ${response.status}: ${JSON.stringify(data).slice(0, 500)}`);
    }
    return data;
  }, { path, body });
}

async function apiGet(page, path) {
  return await page.evaluate(async path => {
    const response = await fetch(`/api/dashboard/${path}`, { credentials: 'same-origin' });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(`${path} failed ${response.status}: ${JSON.stringify(data).slice(0, 500)}`);
    }
    return data;
  }, path);
}

async function refreshUi(page) {
  await page.reload();
  await expect(page.locator('#dashboard')).toBeVisible({ timeout: 30_000 });
  await page.locator('[data-mode-button="user"]').click();
  await expect(page.locator('#header-player')).toContainText(playerName, { timeout: 30_000 });
  await expect(page.locator('#command-input')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
}

async function sendCommand(page, command) {
  await page.locator('#command-input').fill(command);
  const [response] = await Promise.all([
    page.waitForResponse(response => {
      if (!response.url().includes('/api/dashboard/action')) return false;
      if (response.request().method() !== 'POST') return false;
      return (response.request().postData() || '').includes(command);
    }, { timeout: 180_000 }),
    page.getByRole('button', { name: 'Send' }).click()
  ]);
  const result = await response.json().catch(() => ({ success: false, mechanicalSummary: 'Non-JSON response.' }));
  await page.waitForFunction(() => {
    const input = document.getElementById('command-input');
    return !!input && !input.disabled && (!window.UI || !window.UI._streamNode);
  }, null, { timeout: 90_000 });
  return result;
}

async function runCommand(page, command, label, report) {
  try {
    const result = await sendCommand(page, command);
    report.push(scoreStep(label || command, await readState(page), result));
    return result;
  } catch (error) {
    const state = page.isClosed()
      ? { roomName: '(page closed)', storyEntryCount: 0, storyText: '', prompt: '', inputDisabled: true, inputHidden: true }
      : await readState(page).catch(() => ({ roomName: '(state unavailable)', storyEntryCount: 0, storyText: '', prompt: '', inputDisabled: true, inputHidden: true }));
    const result = { success: false, mechanicalSummary: `Automation error while sending "${command}": ${error.message}` };
    report.push(scoreStep(label || command, state, result));
    return result;
  }
}

async function setupChampion(page) {
  await adminPost(page, 'admin/mutations/edit-player', {
    playerId,
    level: 8,
    maxHp: 260,
    hp: 260,
    maxMp: 120,
    mp: 120,
    gold: 999,
    str: 24,
    dex: 18,
    con: 22,
    int: 12,
    wis: 14,
    cha: 18,
    luck: 18
  });

  await adminPost(page, 'admin/mutations/grant-item', {
    playerId,
    itemId: 'ux_dawnblade',
    name: 'UX Dawnblade',
    type: 'Weapon',
    damageDice: '8d8+30',
    damageStat: 'str',
    isEquippable: true,
    isTwoHanded: true,
    statBonuses: { str: 5, con: 5 },
    autoEquip: true
  });

  await adminPost(page, 'admin/mutations/grant-item', {
    playerId,
    itemId: 'ux_aegis',
    name: 'UX Aegis Plate',
    type: 'Armor',
    armorValue: 12,
    isEquippable: true,
    statBonuses: { con: 3 },
    autoEquip: true
  });
}

async function heal(page) {
  await adminPost(page, 'admin/mutations/resources', {
    playerId,
    setMaxHp: 260,
    setHp: 260,
    setMaxMp: 120,
    setMp: 120,
    setLevel: 8
  });
}

async function teleport(page, roomId) {
  await adminPost(page, 'admin/mutations/teleport', {
    playerId,
    roomId,
    createRoomIfMissing: false,
    connectFromCurrentRoom: false
  });
  await refreshUi(page);
}

async function roomHasLivingHostile(page, roomId) {
  const room = await apiGet(page, `rooms/${encodeURIComponent(roomId)}`);
  return (room.npcs || []).some(n => n.isHostile && (n.hp ?? 1) > 0);
}

async function ensureRoomHasBoss(page, roomId) {
  if (await roomHasLivingHostile(page, roomId)) return;
  const boss = fallbackBosses[roomId];
  if (!boss) return;
  const room = await apiGet(page, `rooms/${encodeURIComponent(roomId)}`);
  await adminPost(page, 'admin/mutations/room-fixture', {
    roomId,
    name: room.name,
    description: room.description,
    isDiscovered: true,
    clearNpcs: true,
    exits: room.exits || {},
    npcs: [{ ...boss, isHostile: true, faction: 'neutral' }]
  });
}

async function getLivingHostile(page, roomId) {
  const room = await apiGet(page, `rooms/${encodeURIComponent(roomId)}`);
  return (room.npcs || []).find(n => n.isHostile && (n.hp ?? 1) > 0) || null;
}

async function getFirstNpc(page, roomId) {
  const room = await apiGet(page, `rooms/${encodeURIComponent(roomId)}`);
  return (room.npcs || []).find(n => !n.isHostile) || (room.npcs || [])[0] || null;
}

function resultDefeatedEnemy(result) {
  const text = `${result?.mechanicalSummary || ''} ${result?.narration || ''}`;
  return /falls|collapses|crumbles|fight is over|it's done|victory|defeated|slain/i.test(text);
}

async function fightLivingHostile(page, roomId, label, report) {
  await ensureRoomHasBoss(page, roomId);
  await heal(page);
  const hostile = await getLivingHostile(page, roomId);
  if (!hostile) {
    const state = await readState(page);
    report.push({
      ...scoreStep(`${label}: find hostile`, state, { success: false, mechanicalSummary: `No living hostile in ${roomId}.` }),
      bossDefeated: false
    });
    return false;
  }

  let command = `attack ${hostile.name}`;
  for (let round = 1; round <= 8; round++) {
    await heal(page);
    const result = await runCommand(page, command, `${label}: ${command}`, report);
    if (resultDefeatedEnemy(result)) {
      return true;
    }

    if (result?.success === false && /nothing here still willing or able to fight/i.test(result.mechanicalSummary || '')) {
      return true;
    }

    if (result?.success === false && /target .*not found|was not found/i.test(`${result.mechanicalSummary || ''} ${result.narration || ''}`)) {
      return false;
    }

    command = 'attack';
  }

  return false;
}

async function takeIfPresent(page, itemName, report) {
  await runCommand(page, `take ${itemName}`, `take ${itemName}`, report);
}

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const report = [];
const scaffolding = [];
const browserEvents = [];
let finalBossDefeated = false;

page.on('console', message => {
  if (['error', 'warning'].includes(message.type())) {
    browserEvents.push({ type: message.type(), text: message.text() });
  }
});
page.on('pageerror', error => {
  browserEvents.push({ type: 'pageerror', text: error.message });
});

try {
  await login(page);
  const createResult = await createCharacter(page);
  report.push(scoreStep('create character', await readState(page), createResult.heroIntro ? {
    success: true,
    narration: createResult.heroIntro
  } : null));

  await setupChampion(page);
  scaffolding.push('admin: level/stat/gear boost for endgame UX gauntlet');
  await refreshUi(page);

  await teleport(page, 'city_hall');
  scaffolding.push('admin: teleport city_hall');
  const cityHallNpc = await getFirstNpc(page, 'city_hall');
  const cityHallTarget = cityHallNpc?.name || 'the Marquis';
  for (const command of ['look', `talk to ${cityHallTarget}`, 'ask why the crystals matter', 'walk away']) {
    await runCommand(page, command, command, report);
  }

  for (const phase of combatRooms) {
    await teleport(page, phase.roomId);
    scaffolding.push(`admin: teleport ${phase.roomId}`);
    await runCommand(page, 'look', `${phase.label}: look`, report);
    const defeated = await fightLivingHostile(page, phase.roomId, phase.label, report);
    if (!defeated) break;
    await takeIfPresent(page, phase.crystal, report);
    await teleport(page, 'city_hall');
    await runCommand(page, `tell ${cityHallTarget} I recovered the ${phase.crystal}`, `${phase.label}: report crystal`, report);
  }

  await teleport(page, 'void_rift');
  scaffolding.push('admin: teleport void_rift');
  await runCommand(page, 'look', 'Void Rift: look', report);

  for (const step of finalRoute) {
    if (step.move) {
      await runCommand(page, step.move, `${step.label}: ${step.move}`, report);
    }
    const defeated = await fightLivingHostile(page, step.roomId, step.label, report);
    if (!defeated) break;
    if (step.roomId === 'void_throne') {
      finalBossDefeated = true;
    }
  }

  await runCommand(page, 'inventory', 'inventory after final boss', report);
} finally {
  await browser.close();
}

const average = report.reduce((sum, row) => sum + row.score, 0) / Math.max(1, report.length);
const failing = report.filter(row => row.score < 8);
const summary = {
  baseURL,
  playerId,
  playerName,
  average: Number(average.toFixed(2)),
  failingCount: failing.length,
  finalBossDefeated,
  scaffolding,
  failing: failing.map(row => ({ label: row.label, score: row.score, issues: row.issues })),
  browserEvents: browserEvents.slice(-20),
  report
};

console.log(JSON.stringify(summary, null, 2));

if (!finalBossDefeated || failing.length > 0) {
  process.exitCode = 1;
}
