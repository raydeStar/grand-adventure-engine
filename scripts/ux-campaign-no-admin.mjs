import { chromium, expect } from '@playwright/test';

const baseURL = process.env.GAE_BASE_URL || process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:8181';
const account = {
  username: process.env.GAE_DASHBOARD_USER_USERNAME || 'user',
  password: process.env.GAE_DASHBOARD_USER_PASSWORD || 'GAE-User-Local!123'
};

const playerId = `ux-campaign-${Date.now()}`;
const playerName = `Bonk Campaign ${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`;
let roomsCache = [];

function summarize(text, max = 260) {
  const cleaned = String(text || '').replace(/\s+/g, ' ').trim();
  return cleaned.length > max ? `${cleaned.slice(0, max - 1)}...` : cleaned;
}

function scoreStep(label, state, result = null, extraIssues = []) {
  const issues = [...extraIssues];
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
  if (/requires level|quest giver is not in this room|not ready to turn in|no quest matching/i.test(combined)) {
    score -= 4;
    issues.push('campaign progression was gated or unavailable');
  }
  if (/class has no abilities|don't have an ability called|haven't learned/i.test(combined)) {
    score -= 3;
    issues.push('command used an unavailable character ability');
  }
  if (result && result.success === false) {
    score -= 1;
    issues.push('interaction failed');
  }
  if (issues.some(issue => /player was defeated|expected hostile was unavailable|combat did not resolve|required campaign step failed|level progression was insufficient|path command sequence did not reach destination|room graph has no normal path/i.test(issue))) {
    score = Math.min(score, 6);
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
    player: state.player,
    mechanical: summarize(mechanical, 240),
    narration: summarize(narration, 240),
    visibleStory: summarize(story, 360)
  };
}

async function readState(page) {
  const visible = await page.evaluate(() => {
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

  const player = await page.evaluate(async playerId => {
    const response = await fetch(`/api/dashboard/players/${encodeURIComponent(playerId)}`, { credentials: 'same-origin' });
    if (!response.ok) return null;
    const data = await response.json();
    return {
      level: data.level,
      xp: data.xp,
      hp: data.hp,
      maxHp: data.maxHp,
      mp: data.mp,
      maxMp: data.maxMp,
      roomId: data.currentRoomId,
      gold: data.gold,
      quests: (data.questLog || []).map(q => ({ id: q.questId, status: q.status, stage: q.currentStageId }))
    };
  }, playerId).catch(() => null);

  return { ...visible, player };
}

async function login(page) {
  await page.goto(baseURL);
  await page.locator('#auth-username').fill(account.username);
  await page.locator('#auth-password').fill(account.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await expect(page.locator('#dashboard')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('#btn-new-char')).toBeVisible({ timeout: 30_000 });
}

async function createCharacter(page) {
  await page.getByRole('button', { name: 'New Character' }).click();
  await expect(page.locator('#create-form')).toBeVisible({ timeout: 10_000 });
  await page.locator('#char-name').fill(playerName);
  await page.locator('#char-player-id').fill(playerId);
  await page.locator('#char-race').selectOption('Human');
  await page.locator('#char-class').selectOption('Warrior');
  await page.locator('#char-backstory').fill('A curious sword-swinging menace who wants to save the world without skipping the walking parts.');

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

async function loadRooms(page) {
  roomsCache = await apiGet(page, 'rooms');
}

function getRoom(roomId) {
  return roomsCache.find(room => room.id === roomId) || null;
}

function roomHasLivingCombatants(roomId) {
  const room = getRoom(roomId);
  return (room?.npcs || []).some(npc => typeof npc.hp === 'number' && npc.hp > 0);
}

function findRoute(fromRoomId, toRoomId, { avoidCombatTransit = true } = {}) {
  if (fromRoomId === toRoomId) return [];
  const queue = [{ roomId: fromRoomId, path: [] }];
  const visited = new Set([fromRoomId]);

  while (queue.length > 0) {
    const current = queue.shift();
    const room = getRoom(current.roomId);
    for (const [direction, nextRoomId] of Object.entries(room?.exits || {})) {
      if (visited.has(nextRoomId)) continue;
      if (avoidCombatTransit && nextRoomId !== toRoomId && roomHasLivingCombatants(nextRoomId)) continue;
      const nextPath = [...current.path, direction];
      if (nextRoomId === toRoomId) return nextPath;
      visited.add(nextRoomId);
      queue.push({ roomId: nextRoomId, path: nextPath });
    }
  }

  return null;
}

async function goTo(page, targetRoomId, label = `go to ${targetRoomId}`) {
  const state = await readState(page);
  const currentRoomId = state.player?.roomId;
  if (!currentRoomId) {
    report.push(scoreStep(label, state, { success: false, mechanicalSummary: 'Current room was not available.' }, ['could not pathfind without current room']));
    return false;
  }

  const route = findRoute(currentRoomId, targetRoomId) || findRoute(currentRoomId, targetRoomId, { avoidCombatTransit: false });
  if (!route) {
    report.push(scoreStep(label, state, { success: false, mechanicalSummary: `No route from ${currentRoomId} to ${targetRoomId}.` }, ['room graph has no normal path']));
    return false;
  }

  for (const direction of route) {
    const step = await runCommand(page, `go ${direction}`, `${label}: go ${direction}`);
    if (step.result?.success === false) return false;
  }

  const finalState = await readState(page);
  if (finalState.player?.roomId !== targetRoomId) {
    report.push(scoreStep(label, finalState, {
      success: false,
      mechanicalSummary: `Expected to arrive at ${targetRoomId}, but current room is ${finalState.player?.roomId ?? 'unknown'}.`
    }, ['path command sequence did not reach destination']));
    return false;
  }

  return true;
}

async function requireGoTo(page, targetRoomId, label = `go to ${targetRoomId}`) {
  const ok = await goTo(page, targetRoomId, label);
  if (!ok) {
    const state = await readState(page);
    throw new Error(`${label}: required path failed from ${state.player?.roomId ?? 'unknown'} to ${targetRoomId}`);
  }
  return true;
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

async function runCommand(page, command, label = command, extraIssues = []) {
  let result;
  try {
    result = await sendCommand(page, command);
  } catch (error) {
    result = {
      success: false,
      mechanicalSummary: `Automation error while sending "${command}": ${error.message}`
    };
  }

  const state = await readState(page);
  const row = scoreStep(label, state, result, extraIssues);
  report.push(row);
  await stabilizeBrowserPage(page);
  return { result, state, row };
}

async function stabilizeBrowserPage(page) {
  commandCount++;
  if (commandCount % 30 !== 0) return;

  await page.reload({ waitUntil: 'domcontentloaded', timeout: 60_000 });
  await expect(page.locator('#command-input')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
}

function resultDefeatedEnemy(result) {
  const text = `${result?.mechanicalSummary || ''} ${result?.narration || ''}`;
  if (resultPlayerDefeated(result)) {
    return false;
  }
  if (/victory is close/i.test(text)) {
    return false;
  }

  return /falls|collapses|crumbles|fight is over|it's done|slain|(?:is|has been) defeated/i.test(text);
}

function resultPlayerDefeated(result) {
  const text = `${result?.mechanicalSummary || ''} ${result?.narration || ''}`;
  return /you (?:collapse|have been defeated|are defeated)|you awaken|defeated by|everything goes dark|your vision tunnels as you hit the ground/i.test(text);
}

async function rest(page, label = 'long rest') {
  await runCommand(page, 'long rest', label);
}

async function fight(page, target, label, maxRounds = 12) {
  await rest(page, `${label}: prepare`);
  let command = target ? `attack ${target}` : 'attack';
  const roundLimit = Math.max(maxRounds, 24);
  for (let round = 1; round <= roundLimit; round++) {
    const { result, state } = await runCommand(page, command, `${label}: ${command}`);
    if (resultPlayerDefeated(result)) {
      report.push(scoreStep(`${label}: player survived`, state, result, ['player was defeated']));
      throw new Error(`${label}: player was defeated`);
    }

    if (resultDefeatedEnemy(result)) {
      if (/combat/i.test(state.prompt || '')) {
        command = 'aimed strike';
        continue;
      }

      milestones.push(`${label}: defeated`);
      return true;
    }

    if (state.player && state.player.hp <= 0) {
      report.push(scoreStep(`${label}: player survived`, state, result, ['player was defeated']));
      throw new Error(`${label}: player was defeated`);
    }

    if (state.player && state.player.hp <= Math.ceil(state.player.maxHp * 0.60)) {
      const potion = await runCommand(page, 'use potion', `${label}: use potion`);
      if (resultPlayerDefeated(potion.result)) {
        report.push(scoreStep(`${label}: player survived`, potion.state, potion.result, ['player was defeated']));
        throw new Error(`${label}: player was defeated`);
      }

      if (potion.state.player && potion.state.player.hp <= 0) {
        report.push(scoreStep(`${label}: player survived`, potion.state, potion.result, ['player was defeated']));
        throw new Error(`${label}: player was defeated`);
      }
    }

    if (result?.success === false && /target .*not found|was not found|nothing here still willing or able to fight/i.test(`${result.mechanicalSummary || ''} ${result.narration || ''}`)) {
      const latestState = await readState(page);
      report.push(scoreStep(`${label}: found opponent`, latestState, result, ['expected hostile was unavailable']));
      throw new Error(`${label}: expected hostile was unavailable`);
    }

    command = 'aimed strike';
  }

  const finalState = await readState(page);
  report.push(scoreStep(`${label}: defeated within ${roundLimit} rounds`, finalState, { success: false, mechanicalSummary: 'Combat took too long.' }, ['combat did not resolve']));
  throw new Error(`${label}: combat did not resolve`);
}

async function runCommands(page, commands) {
  for (const entry of commands) {
    const command = typeof entry === 'string' ? entry : entry.command;
    const label = typeof entry === 'string' ? entry : entry.label || entry.command;
    await runCommand(page, command, label);
  }
}

async function buyIfAffordable(page, itemName, price, label) {
  const state = await readState(page);
  if ((state.player?.gold ?? 0) < price) {
    milestones.push(`deferred ${itemName}: ${state.player?.gold ?? 'unknown'} gold`);
    return false;
  }

  const { result } = await runCommand(page, `buy ${itemName}`, label || `buy ${itemName}`);
  if (result?.success === false) {
    throw new Error(`${label || itemName}: purchase failed`);
  }

  return true;
}

async function stockUp(page, label, { basics = 0, greaters = 0, gear = false } = {}) {
  const origin = (await readState(page)).player?.roomId;
  await requireGoTo(page, 'general_store', label);

  if (gear) {
    await buyIfAffordable(page, 'Warden Ring', 15, `${label}: buy Warden Ring`);
    await buyIfAffordable(page, 'Reinforced Gloves', 8, `${label}: buy Reinforced Gloves`);
    await buyIfAffordable(page, 'Swiftstep Sandals', 12, `${label}: buy Swiftstep Sandals`);
  }

  for (let i = 0; i < basics; i++) {
    await buyIfAffordable(page, 'Healing Draught', 15, `${label}: buy Healing Draught ${i + 1}`);
  }

  for (let i = 0; i < greaters; i++) {
    await buyIfAffordable(page, 'Greater Healing Healing Draught', 50, `${label}: buy Greater Healing Draught ${i + 1}`);
  }

  await runCommand(page, 'inventory', `${label}: check inventory`);
  if (origin && origin !== 'general_store') {
    await requireGoTo(page, origin, `${label}: return`);
  }
}

async function ensureLevelAtLeast(page, level, milestone) {
  const state = await readState(page);
  if ((state.player?.level || 0) >= level) {
    milestones.push(`${milestone}: level ${state.player.level}`);
    return true;
  }

  report.push(scoreStep(`${milestone}: level gate`, state, {
    success: false,
    mechanicalSummary: `Expected level ${level}, but ${playerName} is level ${state.player?.level ?? 'unknown'}.`
  }, ['level progression was insufficient for campaign gate']));
  return false;
}

async function noteLevel(page, milestone) {
  const state = await readState(page);
  milestones.push(`${milestone}: level ${state.player?.level ?? 'unknown'} (${state.player?.xp ?? 'unknown'} XP)`);
  return state.player?.level || 0;
}

async function acceptQuestIfReady(page, questName, requiredLevel, { required = true } = {}) {
  const state = await readState(page);
  if ((state.player?.level || 0) < requiredLevel) {
    report.push(scoreStep(`accept quest ${questName}: level gate`, state, {
      success: false,
      mechanicalSummary: `Expected level ${requiredLevel}, but ${playerName} is level ${state.player?.level ?? 'unknown'}.`
    }, ['level progression was insufficient for campaign gate']));
    if (required) {
      throw new Error(`accept quest ${questName}: level ${state.player?.level ?? 'unknown'} below ${requiredLevel}`);
    }
    return false;
  }

  const { result } = await runCommand(page, `accept quest ${questName}`, `accept quest ${questName}`);
  if (required && result?.success === false) {
    throw new Error(`accept quest ${questName}: command failed`);
  }
  return true;
}

async function runAtCityHall(page, label, commands, { required = true } = {}) {
  const reached = required
    ? await requireGoTo(page, 'city_hall', label)
    : await goTo(page, 'city_hall', label);
  const state = await readState(page);
  if (!reached || state.player?.roomId !== 'city_hall') {
    milestones.push(`skipped ${label}: still in ${state.player?.roomId ?? 'unknown'}`);
    if (required) {
      throw new Error(`${label}: city hall was required`);
    }
    return false;
  }

  await runCommands(page, commands);
  return true;
}

async function main() {
  await login(page);
  const createResult = await createCharacter(page);
  await loadRooms(page);
  report.push(scoreStep('create character', await readState(page), createResult.heroIntro ? {
    success: true,
    narration: createResult.heroIntro
  } : null));

  await runCommands(page, [
    'look',
    'talk to Mara',
    'ask Mara for her honest opinion of me',
    'ask Mara why the Waterway matters',
    'accept quest The Waterway Infestation',
    'tell Mara I licked a sewer pipe for science',
    'walk away'
  ]);
  await requireGoTo(page, 'tavern_cellar', 'reach the Waterway');
  await fight(page, null, 'Waterway Infestation', 6);
  await requireGoTo(page, 'spawn', 'return to Mara');
  await runCommands(page, ['talk to Mara', 'tell Mara the Waterway is clear', 'walk away']);
  await stockUp(page, 'early campaign supplies', { basics: 4, gear: true });

  await requireGoTo(page, 'town_square', 'reach the bazaar');
  await runCommands(page, [
    'talk to Bram',
    'accept quest Eyes on the Empire',
    'walk away'
  ]);
  await requireGoTo(page, 'town_gate', 'patrol eastern gate');
  await requireGoTo(page, 'back_alley', 'patrol Lowtown');
  await requireGoTo(page, 'market_stalls', 'patrol market');
  await requireGoTo(page, 'town_square', 'return to Bram');
  await runCommands(page, [
    'talk to Bram',
    'tell Bram I checked the gate, Lowtown, and the market',
    'walk away'
  ]);

  await requireGoTo(page, 'deep_forest', 'reach the Emberwood');
  await fight(page, null, 'Emberwood guardian', 10);
  await requireGoTo(page, 'volcanic_path', 'reach the volcanic path');
  await fight(page, null, 'Volcanic path', 8);
  await requireGoTo(page, 'water_temple_entrance', 'reach the flooded tunnels');
  await fight(page, null, 'Water Temple entrance', 8);

  await noteLevel(page, 'main quest readiness');

  if (await runAtCityHall(page, 'reach City Hall', [
    'talk to Marquis',
    'ask Marquis why the crystals matter'
  ])) {
    await acceptQuestIfReady(page, 'The Dying Crystal', 2);
    await runCommand(page, 'walk away', 'walk away');
  }
  await requireGoTo(page, 'water_temple_depths', 'reach Water Temple depths');
  await fight(page, null, 'Water Temple depths', 8);
  await requireGoTo(page, 'water_temple_sanctum', 'reach Water Crystal sanctum');
  await fight(page, null, 'Water Crystal guardian', 10);
  await runCommands(page, [
    'take Water Crystal'
  ]);
  await runAtCityHall(page, 'return Water Crystal to Marquis', [
    'tell Marquis I recovered the Water Crystal',
    'walk away'
  ]);
  await stockUp(page, 'post-water supplies', { greaters: 3 });

  await noteLevel(page, 'crystal crusade readiness');

  if (await runAtCityHall(page, 'accept crystal trials', [
    'talk to Marquis'
  ])) {
    await acceptQuestIfReady(page, 'Trial of Fire', 3);
    await acceptQuestIfReady(page, 'Trial of Earth', 3);
    await acceptQuestIfReady(page, 'Trial of Wind', 3);
    await runCommand(page, 'walk away', 'walk away');
  }
  await requireGoTo(page, 'fire_temple_entrance', 'reach Fire Temple');
  await fight(page, null, 'Fire temple entry', 8);
  await requireGoTo(page, 'fire_temple_core', 'reach Fire Crystal core');
  await fight(page, null, 'Fire Crystal guardian', 12);
  await runCommands(page, [
    'take Fire Crystal'
  ]);
  await runAtCityHall(page, 'return Fire Crystal to Marquis', [
    'tell Marquis I recovered the Fire Crystal',
    'walk away'
  ]);
  await stockUp(page, 'post-fire supplies', { basics: 2, greaters: 2 });

  await requireGoTo(page, 'mountain_trail', 'reach mountain trail');
  await fight(page, null, 'Mountain trail', 10);
  await requireGoTo(page, 'earth_temple_entrance', 'reach Earth Temple');
  await fight(page, null, 'Earth temple entry', 10);
  await requireGoTo(page, 'earth_temple_heart', 'reach Earth Crystal heart');
  await fight(page, null, 'Earth Crystal guardian', 12);
  await runCommands(page, [
    'take Earth Crystal'
  ]);
  await runAtCityHall(page, 'return Earth Crystal to Marquis', [
    'tell Marquis I recovered the Earth Crystal',
    'walk away'
  ]);
  await stockUp(page, 'post-earth supplies', { greaters: 2 });

  await requireGoTo(page, 'skyward_path', 'reach skyward path');
  await fight(page, null, 'Skyward path', 10);
  await requireGoTo(page, 'wind_temple_entrance', 'reach Wind Temple');
  await fight(page, null, 'Wind temple entry', 10);
  await requireGoTo(page, 'wind_temple_apex', 'reach Wind Crystal apex');
  await fight(page, null, 'Wind Crystal guardian', 12);
  await runCommands(page, [
    'take Wind Crystal'
  ]);
  await runAtCityHall(page, 'return Wind Crystal to Marquis', [
    'tell Marquis I recovered the Wind Crystal',
    'walk away'
  ]);

  await noteLevel(page, 'void quest readiness');

  if (await runAtCityHall(page, 'accept void quest', [
    'talk to Marquis'
  ])) {
    await acceptQuestIfReady(page, 'The Void Awaits', 4);
    await runCommand(page, 'walk away', 'walk away');
  }
  await stockUp(page, 'final supplies', { greaters: 4 });
  await requireGoTo(page, 'void_rift', 'enter the Void');
  await fight(page, null, 'Void rift', 12);
  await requireGoTo(page, 'void_corridor', 'reach Void corridor');
  await fight(page, null, 'Void corridor', 12);
  await requireGoTo(page, 'void_throne', 'reach final throne');
  finalBossDefeated = await fight(page, null, 'Final boss', 16);
  await runCommands(page, ['take Shard of Restored Light', 'inventory']);
}

const browser = await chromium.launch({
  headless: true,
  args: ['--disable-gpu', '--disable-dev-shm-usage', '--no-sandbox']
});
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const report = [];
const milestones = [];
const browserEvents = [];
let finalBossDefeated = false;
let campaignError = null;
let commandCount = 0;

page.on('console', message => {
  if (['error', 'warning'].includes(message.type())) {
    browserEvents.push({ type: message.type(), text: message.text() });
  }
});
page.on('pageerror', error => {
  browserEvents.push({ type: 'pageerror', text: error.message });
});

try {
  await main();
} catch (error) {
  campaignError = error?.message || String(error);
  const state = await readState(page).catch(() => ({
    roomName: 'unknown',
    prompt: '',
    storyEntryCount: 0,
    storyText: '',
    inputDisabled: false,
    inputHidden: false,
    player: null
  }));
  report.push(scoreStep('campaign aborted', state, {
    success: false,
    mechanicalSummary: campaignError
  }, ['required campaign step failed']));
} finally {
  await browser.close();
}

const average = report.reduce((sum, row) => sum + row.score, 0) / Math.max(1, report.length);
const failing = report.filter(row => row.score < 8);
const finalState = report.at(-1)?.player || null;
const summary = {
  baseURL,
  playerId,
  playerName,
  average: Number(average.toFixed(2)),
  failingCount: failing.length,
  finalBossDefeated,
  campaignError,
  finalState,
  milestones,
  failing: failing.map(row => ({ label: row.label, score: row.score, issues: row.issues, room: row.room, player: row.player, mechanical: row.mechanical })),
  browserEvents: browserEvents.slice(-20),
  report
};

console.log(JSON.stringify(summary, null, 2));

if (campaignError || !finalBossDefeated || failing.length > 0) {
  process.exitCode = 1;
}
