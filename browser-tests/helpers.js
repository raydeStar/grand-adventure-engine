const { expect } = require('@playwright/test');

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

function uniqueId(prefix, projectName) {
  return `${prefix}-${projectName.replace(/[^a-z0-9]+/gi, '-').toLowerCase()}-${Date.now()}`;
}

async function waitForAuthenticatedShell(page) {
  await expect(page.locator('#auth-form')).toBeHidden();
  // After login, the app auto-navigates to the dashboard
  await expect(page.locator('#dashboard')).toBeVisible();
  await page.waitForFunction(() => {
    const badge = document.getElementById('session-badge');
    return badge && !badge.textContent?.includes('Signed out');
  });
}

async function login(page, role = 'user') {
  const account = credentials[role];
  if (!account) {
    throw new Error(`Unknown dashboard role '${role}'.`);
  }

  await page.goto('/');
  await page.locator('#auth-username').fill(account.username);
  await page.locator('#auth-password').fill(account.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await waitForAuthenticatedShell(page);
  return account;
}

async function openCreateCharacter(page) {
  await page.getByRole('button', { name: 'New Character' }).click();
  await expect(page.locator('#create-form')).toBeVisible();
}

async function openAdminConsole(page) {
  await page.locator('[data-mode-button="admin"]').click();
  await expect(page.locator('#workspace-admin')).toBeVisible();
}

async function switchAdminTab(page, tabName) {
  await page.locator(`.admin-tab[data-admin-tab="${tabName}"]`).click();
  await expect(page.locator(`.admin-tab-content[data-admin-panel="${tabName}"]`)).toBeVisible();
}

async function seedDemoViaApi(page, replaceExisting = true) {
  return await page.evaluate(async (replace) => {
    return await API.seedDemoCharacters(replace);
  }, replaceExisting);
}

/**
 * Delete all players whose ID starts with any of the given prefixes.
 * Requires an admin-authenticated page.
 */
async function cleanupTestPlayers(page, prefixes = ['pw-']) {
  await page.evaluate(async (pfx) => {
    const players = await API.getPlayers();
    const toDelete = players.filter(p => pfx.some(prefix => p.id.startsWith(prefix)));
    await Promise.allSettled(
      toDelete.map(p => API.deletePlayer(p.id))
    );
  }, prefixes);
}

/**
 * Clean up test players via HTTP requests so teardown does not depend on UI state.
 */
async function cleanupTestPlayersViaApi(request, prefixes = ['pw-']) {
  const loginResponse = await request.post('/api/dashboard/auth/login', {
    data: {
      username: credentials.admin.username,
      password: credentials.admin.password
    }
  });

  if (!loginResponse.ok()) {
    return;
  }

  const playersResponse = await request.get('/api/dashboard/players');
  if (!playersResponse.ok()) {
    return;
  }

  const players = await playersResponse.json();
  const toDelete = players.filter(p => prefixes.some(prefix => p.id.startsWith(prefix)));

  await Promise.allSettled(
    toDelete.map(p => request.delete(`/api/dashboard/admin/players/${encodeURIComponent(p.id)}`))
  );
}

module.exports = {
  credentials,
  login,
  openAdminConsole,
  openCreateCharacter,
  seedDemoViaApi,
  switchAdminTab,
  cleanupTestPlayers,
  cleanupTestPlayersViaApi,
  uniqueId
};