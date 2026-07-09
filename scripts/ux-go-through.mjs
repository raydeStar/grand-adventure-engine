import { chromium, expect } from '@playwright/test';

const baseURL = process.env.GAE_BASE_URL || process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:8181';
const role = process.env.GAE_UX_ROLE || 'user';
const credentials = {
  user: {
    username: process.env.GAE_DASHBOARD_USER_USERNAME || 'user',
    password: process.env.GAE_DASHBOARD_USER_PASSWORD || 'GAE-User-Local!123'
  },
  admin: {
    username: process.env.GAE_DASHBOARD_ADMIN_USERNAME || 'admin',
    password: process.env.GAE_DASHBOARD_ADMIN_PASSWORD || 'GAE-Admin-Local!123'
  }
};

const account = credentials[role] || credentials.user;
const playerId = `ux-go-${Date.now()}`;
const playerName = `Bonk Pathfinder ${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`;
const commands = [
  'look',
  'talk to Mara',
  'ask Mara about the Waterway Infestation',
  'ask Mara for her honest opinion of me',
  'ask Mara why the Waterway matters',
  'tell Mara I licked a sewer pipe for science',
  'ask Mara what she thinks I should do next',
  'accept quest The Waterway Infestation',
  'tell Mara her cocktail mug looks like a suspicious bird and order the weirdest drink',
  'walk away from Mara',
  'go down',
  'look',
  'lick the glowing sewer pipe',
  'attack dire rat',
  'attack giant rat',
  'attack',
  'journal',
  'go up',
  'talk to Mara',
  'tell Mara the Waterway is clear'
];

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

  if (state.inputDisabled) {
    score -= 2;
    issues.push('command input remained disabled');
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
    score -= 3;
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
  if (result && /nothing happens|doesn't understand|cannot process|try again/i.test(`${mechanical} ${narration}`)) {
    score -= 2;
    issues.push('response felt generic or non-consequential');
  }
  if (result && /target .*not found|was not found|don't see .* anywhere/i.test(`${mechanical} ${narration}`)) {
    score -= 4;
    issues.push('expected target was not reachable by natural phrasing');
  }
  if (result && /moment passes|without any dramatic consequences|try \*\*talk to/i.test(`${mechanical} ${narration}`)) {
    score -= 3;
    issues.push('free-form response felt like a shrug instead of a consequence');
  }
  if (result && /let me think about that|go on|keep going|another side to this story/i.test(narration)) {
    score -= 3;
    issues.push('conversation response felt like filler');
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
    mechanical: summarize(mechanical, 220),
    narration: summarize(narration, 220),
    visibleStory: summarize(story, 320)
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
      storyEntryCount: entries.length,
      storyText: entries.slice(-3).map(e => e.textContent || '').join('\n---\n')
    };
  });
}

async function login(page) {
  await page.goto(baseURL);
  await page.locator('#auth-username').fill(account.username);
  await page.locator('#auth-password').fill(account.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await expect(page.locator('#dashboard')).toBeVisible({ timeout: 30_000 });
}

async function createCharacter(page) {
  await page.getByRole('button', { name: 'New Character' }).click();
  await expect(page.locator('#create-form')).toBeVisible({ timeout: 10_000 });
  await page.locator('#char-name').fill(playerName);
  await page.locator('#char-player-id').fill(playerId);
  await page.locator('#char-race').selectOption('Human');
  await page.locator('#char-class').selectOption('Warrior');
  await page.locator('#char-backstory').fill('A cheerful menace who wants to save the world and touch every suspicious object.');

  const responsePromise = page.waitForResponse(response =>
    response.url().includes('/api/dashboard/characters') && response.request().method() === 'POST',
    { timeout: 180_000 });
  await page.locator('#create-form').getByRole('button', { name: 'Create' }).click();
  const response = await responsePromise;
  const body = await response.json().catch(() => ({}));
  if (!response.ok()) {
    throw new Error(`Character create returned HTTP ${response.status()}: ${JSON.stringify(body).slice(0, 600)}`);
  }
  try {
    await expect(page.locator('#header-player')).toContainText(playerName, { timeout: 30_000 });
    await expect(page.locator('#command-input')).toBeEnabled({ timeout: 30_000 });
  } catch (error) {
    const state = await readState(page);
    throw new Error([
      error.message,
      `Create body keys: ${Object.keys(body || {}).join(', ')}`,
      `Create body preview: ${JSON.stringify(body).slice(0, 900)}`,
      `Visible state: ${JSON.stringify(state)}`,
      `Browser events: ${JSON.stringify(browserEvents.slice(-10))}`
    ].join('\n'));
  }
  return body;
}

async function sendCommand(page, command) {
  const responsePromise = page.waitForResponse(response => {
    if (!response.url().includes('/api/dashboard/action')) return false;
    if (response.request().method() !== 'POST') return false;
    return (response.request().postData() || '').includes(command);
  }, { timeout: 180_000 });

  await page.locator('#command-input').fill(command);
  await page.getByRole('button', { name: 'Send' }).click();
  const response = await responsePromise;
  const result = await response.json().catch(() => ({ success: false, mechanicalSummary: 'Non-JSON response.' }));
  await page.waitForFunction(() => {
    const input = document.getElementById('command-input');
    return !!input && !input.disabled && (!window.UI || !window.UI._streamNode);
  }, null, { timeout: 90_000 });
  return result;
}

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const report = [];
const browserEvents = [];

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

  for (const command of commands) {
    let result;
    try {
      result = await sendCommand(page, command);
    } catch (error) {
      result = {
        success: false,
        mechanicalSummary: `Automation error: ${error.message}`
      };
    }
    report.push(scoreStep(command, await readState(page), result));
  }
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
  failing: failing.map(row => ({ label: row.label, score: row.score, issues: row.issues })),
  report
};

console.log(JSON.stringify(summary, null, 2));
