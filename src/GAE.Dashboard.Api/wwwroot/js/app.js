(async function () {
  'use strict';

  const state = {
    mode: localStorage.getItem('gae.operator.mode') || 'user',
    currentPlayerId: localStorage.getItem('gae.active.player') || '',
    currentRoomId: '',
    currentPlayer: null,
    currentRoom: null,
    players: [],
    rooms: [],
    summary: null,
    health: null,
    session: null,
    loginHints: [],
    refreshTimer: null,
    transportLabel: 'Offline',
    recentActionIds: new Set(),
    commandHistory: [],
    commandHistoryIndex: -1,
    interactionMode: 'explore',
    interactionTarget: ''
  };

  // C# InteractionMode enum serializes as int — map to lowercase strings
  const INTERACTION_MODES = ['explore', 'conversation', 'combat', 'trading', 'stealth', 'event'];
  function normalizeMode(v) {
    if (typeof v === 'number') return INTERACTION_MODES[v] || 'explore';
    if (typeof v === 'string' && v.length > 0) return v.toLowerCase();
    return 'explore';
  }

  const workflowScenarios = {
    'smoke-user': ['look', 'stats', 'inventory', 'help'],
    exploration: ['look', 'go north', 'look'],
    'ops-check': ['help', 'rest', 'look']
  };

  async function init() {
    UI.restoreTheme();
    bindEvents();
    state.mode = UI.setMode(state.mode, null);
    UI.showPortal(true);
    UI.showDashboard(false);
    UI.setSession(null, []);
    UI.setConnectionStatus('disconnected');
    UI.renderNoActivePlayer(false);
    UI.renderPortalPlayers([], state.currentPlayerId, null);
    UI.renderPlayersList([], state.currentPlayerId, null);
    UI.renderAdminPlayers([], state.currentPlayerId, null);

    GameHub.on('status', handleTransportStatus);
    GameHub.on('gameEvent', handleRealtimeGameEvent);
    GameHub.on('roomEvent', handleRealtimeRoomEvent);
    GameHub.on('playerEvent', handleRealtimePlayerEvent);
    GameHub.on('actionResult', handleRealtimeActionResult);

    state.loginHints = await API.getLoginOptions();
    UI.setSession(null, state.loginHints);

    const session = await API.getSession();
    if (!session) {
      UI.setPortalMessage('Sign in to unlock gameplay, admin workflows, and protected endpoints.', 'info');
      return;
    }

    await onAuthenticated(session, { announce: false });
  }

  function bindEvents() {
    UI.$('auth-form').addEventListener('submit', handleLogin);
    UI.$('btn-fill-user').addEventListener('click', () => fillUsername('user'));
    UI.$('btn-fill-admin').addEventListener('click', () => fillUsername('admin'));
    UI.$('btn-logout-header').addEventListener('click', () => void handleLogout());
    UI.$('btn-logout-portal').addEventListener('click', () => void handleLogout());

    // Theme toggle
    const themeBtn = UI.$('btn-theme-toggle');
    if (themeBtn) themeBtn.addEventListener('click', () => UI.toggleTheme());

    UI.$('btn-new-char').addEventListener('click', () => {
      if (!ensureAuthenticated()) return;
      UI.setPortalMessage('');
      UI.showCreateForm(true);
    });
    UI.$('btn-cancel-create').addEventListener('click', () => UI.showCreateForm(false));
    UI.$('btn-open-admin').addEventListener('click', () => openDashboard('admin'));
    UI.$('btn-open-portal').addEventListener('click', () => {
      UI.showPortal(true);
      UI.showDashboard(false);
    });
    UI.$('btn-refresh').addEventListener('click', () => {
      if (!ensureAuthenticated()) return;
      void refreshAll().catch((error) => handleError(error, { portal: true, logId: 'workflow-log' }));
      UI.appendActivity('workflow-log', 'Manual refresh requested.', 'info');
    });
    UI.$('btn-seed-demo').addEventListener('click', () => void seedDemoCharacters(false));
    UI.$('btn-seed-demo-admin').addEventListener('click', () => void seedDemoCharacters(false));

    UI.$('create-form').addEventListener('submit', handleCreateCharacter);
    UI.$('command-input').addEventListener('keydown', handleCommandKeydown);
    UI.$('command-form').addEventListener('submit', handleUserCommand);
    UI.$('admin-command-form').addEventListener('submit', handleAdminCommand);
    UI.$('mutation-resource-form').addEventListener('submit', handleResourceMutation);
    UI.$('mutation-teleport-form').addEventListener('submit', handleTeleportMutation);
    UI.$('mutation-item-form').addEventListener('submit', handleItemMutation);
    UI.$('mutation-status-form').addEventListener('submit', handleStatusMutation);
    UI.$('room-fixture-form').addEventListener('submit', handleRoomFixtureMutation);

    document.querySelectorAll('[data-role-choice]').forEach((button) => {
      button.addEventListener('click', () => openDashboard(button.dataset.roleChoice || 'user'));
    });

    document.querySelectorAll('[data-mode-button]').forEach((button) => {
      button.addEventListener('click', () => openDashboard(button.dataset.modeButton || 'user'));
    });

    document.querySelectorAll('.qcmd').forEach((button) => {
      button.addEventListener('click', () => {
        if (!state.currentPlayerId) return;
        UI.$('command-input').value = button.dataset.cmd || '';
        void executeUserCommand();
      });
    });

    document.querySelectorAll('[data-scenario]').forEach((button) => {
      button.addEventListener('click', () => void runScenario(button.dataset.scenario || ''));
    });

    UI.$('existing-players').addEventListener('click', handlePortalPlayerClick);
    UI.$('admin-players-table').addEventListener('click', handleAdminRegistryClick);

    document.addEventListener('click', (event) => {
      const exit = event.target.closest('[data-room-exit]');
      if (exit && state.currentPlayerId) {
        UI.$('command-input').value = `go ${exit.dataset.roomExit}`;
        void executeUserCommand();
      }
      const interactionChip = event.target.closest('[data-interaction-cmd]');
      if (interactionChip && state.currentPlayerId) {
        UI.$('command-input').value = interactionChip.dataset.interactionCmd;
        void executeUserCommand();
      }
    });
  }

  async function onAuthenticated(session, options = {}) {
    state.session = session;
    UI.setSession(state.session, state.loginHints);
    UI.setAuthMessage('');
    state.mode = UI.setMode(state.mode, state.session);
    localStorage.setItem('gae.operator.mode', state.mode);
    UI.renderNoActivePlayer(true);
    UI.setConnectionStatus('connecting');

    await refreshAll();
    startRefreshLoop();
    void GameHub.connect();

    if (options.announce !== false) {
      UI.setPortalMessage(`Signed in as ${session.username}.`, 'success');
    }
  }

  async function handleLogin(event) {
    event.preventDefault();
    const submit = UI.$('auth-submit');
    const username = UI.$('auth-username').value.trim();
    const password = UI.$('auth-password').value;
    const rememberMe = UI.$('auth-remember').checked;

    submit.disabled = true;
    submit.textContent = 'Signing In...';
    UI.setAuthMessage('');

    try {
      const session = await API.login(username, password, rememberMe);
      UI.$('auth-form').reset();
      await onAuthenticated(session);
    } catch (error) {
      await handleError(error, { auth: true });
    } finally {
      submit.disabled = false;
      submit.textContent = 'Sign In';
    }
  }

  async function handleLogout() {
    try {
      await API.logout();
    } catch {
      // Best effort logout; clear local session state either way.
    }

    await forceSignedOut('Signed out. Use the login form to resume.', 'info');
  }

  async function forceSignedOut(message, tone = 'error') {
    state.session = null;
    state.summary = null;
    state.players = [];
    state.rooms = [];
    state.currentPlayer = null;
    state.currentRoom = null;
    state.currentPlayerId = '';
    state.currentRoomId = '';
    state.transportLabel = 'Offline';

    localStorage.removeItem('gae.active.player');
    stopRefreshLoop();
    await GameHub.disconnect();

    UI.showDashboard(false);
    UI.showPortal(true);
    UI.setSession(null, state.loginHints);
    UI.setConnectionStatus('disconnected');
    UI.renderNoActivePlayer(false);
    UI.renderPortalPlayers([], '', null);
    UI.renderPlayersList([], '', null);
    UI.renderAdminPlayers([], '', null);
    UI.renderRoomCatalogue([]);
    UI.renderSummary(null, state.transportLabel, null);
    UI.clearActivity('workflow-log');
    UI.clearActivity('admin-command-log');
    UI.clearActivity('mutation-log');
    UI.setPortalMessage(message, tone);
  }

  function fillUsername(role) {
    const hint = state.loginHints.find((entry) => entry.role === role);
    if (!hint) return;

    UI.$('auth-username').value = hint.username;
    UI.$('auth-password').focus();
  }

  function ensureAuthenticated() {
    if (state.session) return true;
    UI.setAuthMessage('Sign in before using the dashboard.', 'error');
    UI.showPortal(true);
    UI.showDashboard(false);
    UI.$('auth-username').focus();
    return false;
  }

  function ensureAdmin() {
    if (state.session?.isAdmin) return true;
    UI.setPortalMessage('Admin login required for this operation.', 'error');
    return false;
  }

  function setMode(mode) {
    state.mode = UI.setMode(mode, state.session);
    localStorage.setItem('gae.operator.mode', state.mode);
  }

  function openDashboard(mode) {
    if (!ensureAuthenticated()) return;
    if (mode === 'admin' && !ensureAdmin()) return;

    setMode(mode);
    UI.showDashboard(true);
    UI.showPortal(false);
    if (mode === 'user' && !state.currentPlayerId) {
      UI.renderNoActivePlayer(true);
    }
  }

  async function refreshAll() {
    if (!state.session) return;

    await Promise.all([
      refreshPlayers(),
      refreshRooms(),
      refreshSummary(),
      refreshHealth()
    ]);

    if (state.currentPlayerId) {
      await refreshCurrentPlayer();
      await refreshStory();
    } else {
      UI.renderNoActivePlayer(true);
    }
  }

  async function refreshPlayers() {
    state.players = await API.getPlayers();
    UI.renderPortalPlayers(state.players, state.currentPlayerId, state.session);
    UI.renderPlayersList(state.players, state.currentPlayerId, state.session);
    UI.renderAdminPlayers(state.players, state.currentPlayerId, state.session);
    UI.renderSelectOptions(state.players, state.currentPlayerId);

    if (state.currentPlayerId && !state.players.some((player) => player.id === state.currentPlayerId)) {
      state.currentPlayerId = '';
      state.currentRoomId = '';
      state.currentPlayer = null;
      state.currentRoom = null;
      localStorage.removeItem('gae.active.player');
      UI.renderNoActivePlayer(true);
    }
  }

  async function refreshRooms() {
    state.rooms = await API.getRooms();
    UI.renderRoomCatalogue(state.rooms);
  }

  async function refreshSummary() {
    if (!state.session?.isAdmin) {
      state.summary = null;
      UI.renderSummary(null, state.transportLabel, state.session);
      return;
    }

    state.summary = await API.getAdminSummary();
    UI.renderSummary(state.summary, state.transportLabel, state.session);
  }

  async function refreshHealth() {
    state.health = await API.getHealth();
    UI.renderHealth(state.health);
  }

  async function refreshCurrentPlayer() {
    if (!state.currentPlayerId) {
      state.currentPlayer = null;
      state.currentRoom = null;
      UI.renderNoActivePlayer(true);
      return;
    }

    const player = await API.getPlayer(state.currentPlayerId);
    if (!player) {
      state.currentPlayerId = '';
      state.currentRoomId = '';
      state.currentPlayer = null;
      state.currentRoom = null;
      localStorage.removeItem('gae.active.player');
      UI.renderNoActivePlayer(true);
      return;
    }

    state.currentPlayer = player;
    UI.renderPlayer(player);

    // Sync interaction state from player
    if (player.interaction) {
      state.interactionMode = normalizeMode(player.interaction.mode);
      state.interactionTarget = player.interaction.target || '';
    } else {
      state.interactionMode = 'explore';
      state.interactionTarget = '';
    }
    UI.updateInteractionMode(state.interactionMode, state.interactionTarget);

    if (player.currentRoomId !== state.currentRoomId) {
      if (state.currentRoomId) {
        await GameHub.leaveRoomFeed(state.currentRoomId);
      }
      state.currentRoomId = player.currentRoomId;
      await GameHub.joinRoomFeed(state.currentRoomId);
    }

    const room = await API.getRoom(player.currentRoomId);
    state.currentRoom = room;
    UI.renderRoom(room);
    UI.renderPayloads(state.currentPlayer, state.currentRoom);
  }

  async function refreshStory() {
    if (!state.currentPlayerId) {
      UI.$('story-log').innerHTML = '<div class="empty-state">Story entries will appear once a character starts acting.</div>';
      return;
    }

    const story = await API.getStory(state.currentPlayerId, 30);
    UI.renderStoryLog(story);
  }

  async function activatePlayer(playerId, mode) {
    if (!playerId || !state.session) return;
    if (mode === 'admin' && !state.session.isAdmin) mode = 'user';

    state.currentPlayerId = playerId;
    state.currentRoomId = '';
    localStorage.setItem('gae.active.player', playerId);
    setMode(mode);
    UI.showDashboard(true);
    UI.showPortal(false);
    await GameHub.joinPlayerFeed(playerId);
    await refreshCurrentPlayer();
    await refreshStory();
    await refreshPlayers();
    await refreshSummary();
    UI.$('command-input').focus();
  }

  async function handleCreateCharacter(event) {
    event.preventDefault();
    if (!ensureAuthenticated()) return;

    const submit = UI.$('create-submit');
    submit.disabled = true;
    submit.textContent = 'Creating...';
    UI.setPortalMessage('');

    try {
      const player = await API.createCharacter({
        playerId: nullableText(UI.$('char-player-id').value),
        name: UI.$('char-name').value.trim(),
        race: UI.$('char-race').value,
        class: UI.$('char-class').value,
        statMethod: UI.$('char-stats').value,
        backstory: nullableText(UI.$('char-backstory').value)
      });

      UI.$('create-form').reset();
      UI.showCreateForm(false);
      UI.setPortalMessage(`Created ${player.name} (${player.id}).`, 'success');
      await refreshAll();
      await activatePlayer(player.id, state.session?.isAdmin && state.mode === 'admin' ? 'admin' : 'user');
    } catch (error) {
      await handleError(error, { portal: true });
    } finally {
      submit.disabled = false;
      submit.textContent = 'Create Character';
    }
  }

  async function seedDemoCharacters(replaceExisting) {
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    try {
      const result = await API.seedDemoCharacters(replaceExisting);
      const message = result.createdCount > 0
        ? `Seeded ${result.createdCount} demo personas.`
        : 'Demo personas already existed. Nothing changed.';
      UI.setPortalMessage(message, 'success');
      UI.appendActivity('workflow-log', message, 'success');
      await refreshAll();
    } catch (error) {
      await handleError(error, { portal: true, logId: 'workflow-log' });
    }
  }

  function handlePortalPlayerClick(event) {
    const button = event.target.closest('[data-portal-action]');
    if (!button) return;
    void activatePlayer(button.dataset.playerId || '', button.dataset.portalAction || 'user');
  }

  function handleAdminRegistryClick(event) {
    const button = event.target.closest('[data-admin-action]');
    if (!button) return;

    const playerId = button.dataset.playerId || '';
    const action = button.dataset.adminAction || '';

    if (action === 'copy-id') {
      navigator.clipboard?.writeText(playerId);
      UI.appendActivity('admin-command-log', `Copied player id ${playerId}.`, 'info');
      return;
    }

    void activatePlayer(playerId, action === 'user' ? 'user' : 'admin');
  }

  function handleCommandKeydown(e) {
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (state.commandHistory.length === 0) return;
      if (state.commandHistoryIndex < state.commandHistory.length - 1) {
        state.commandHistoryIndex++;
      }
      UI.$('command-input').value = state.commandHistory[state.commandHistoryIndex] || '';
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (state.commandHistoryIndex > 0) {
        state.commandHistoryIndex--;
        UI.$('command-input').value = state.commandHistory[state.commandHistoryIndex] || '';
      } else {
        state.commandHistoryIndex = -1;
        UI.$('command-input').value = '';
      }
    }
  }

  async function handleUserCommand(event) {
    event.preventDefault();
    await executeUserCommand();
  }

  async function executeUserCommand() {
    if (!ensureAuthenticated()) return;

    const input = UI.$('command-input');
    const command = input.value.trim();
    if (!command || !state.currentPlayerId) return;

    // If text is still streaming, skip it before sending the next command
    UI._cancelStreaming();

    state.commandHistory.unshift(command);
    if (state.commandHistory.length > 20) state.commandHistory.pop();
    state.commandHistoryIndex = -1;

    input.value = '';
    input.disabled = true;
    UI.$('command-submit').disabled = true;

    try {
      const result = await API.sendCommand(state.currentPlayerId, command);
      if (result.actionId) state.recentActionIds.add(result.actionId);
      UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
      await afterCommand(state.currentPlayerId, result);
    } catch (error) {
      await handleError(error, { story: true });
    } finally {
      input.disabled = false;
      UI.$('command-submit').disabled = false;
      input.focus();
    }
  }

  async function handleAdminCommand(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const playerId = UI.$('admin-player-select').value;
    const input = UI.$('admin-command-input');
    const command = input.value.trim();
    if (!playerId || !command) return;

    input.value = '';
    UI.appendActivity('admin-command-log', `${playerId} > ${command}`, 'info');

    try {
      const result = await API.sendCommand(playerId, command);
      if (result.actionId) state.recentActionIds.add(result.actionId);
      UI.appendActivity('admin-command-log', result.mechanicalSummary || 'No response summary.', result.success ? 'success' : 'failure');
      if (playerId === state.currentPlayerId) {
        UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
      }
      await afterCommand(playerId, result);
    } catch (error) {
      await handleError(error, { logId: 'admin-command-log' });
    }
  }

  async function runScenario(name) {
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const commands = workflowScenarios[name];
    const playerId = UI.$('workflow-player-select').value;
    if (!playerId || !commands?.length) {
      UI.appendActivity('workflow-log', 'Choose a player before running a workflow.', 'failure');
      return;
    }

    UI.appendActivity('workflow-log', `Running ${name} for ${playerId}.`, 'info');

    for (const command of commands) {
      try {
        const result = await API.sendCommand(playerId, command);
        if (result.actionId) state.recentActionIds.add(result.actionId);
        UI.appendActivity('workflow-log', `${playerId} > ${command}`, 'info');
        UI.appendActivity('workflow-log', result.mechanicalSummary || 'No response summary.', result.success ? 'success' : 'failure');

        if (playerId === state.currentPlayerId) {
          UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
        }

        await afterCommand(playerId, result);
      } catch (error) {
        await handleError(error, { logId: 'workflow-log' });
        break;
      }
    }
  }

  async function handleResourceMutation(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const form = event.currentTarget;
    try {
      const result = await API.adjustResources({
        playerId: UI.$('resource-player-select').value,
        hpDelta: numberValue(UI.$('resource-hp-delta').value),
        mpDelta: numberValue(UI.$('resource-mp-delta').value),
        goldDelta: numberValue(UI.$('resource-gold-delta').value),
        xpDelta: numberValue(UI.$('resource-xp-delta').value),
        levelDelta: numberValue(UI.$('resource-level-delta').value),
        setHp: nullableNumber(UI.$('resource-set-hp').value),
        setMp: nullableNumber(UI.$('resource-set-mp').value),
        setGold: nullableNumber(UI.$('resource-set-gold').value),
        setXp: nullableNumber(UI.$('resource-set-xp').value),
        setLevel: nullableNumber(UI.$('resource-set-level').value)
      });
      UI.appendActivity('mutation-log', result.summary || 'Resources updated.', 'success');
      form.reset();
      await afterMutation(result);
    } catch (error) {
      await handleError(error, { logId: 'mutation-log' });
    }
  }

  async function handleTeleportMutation(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const form = event.currentTarget;
    try {
      const result = await API.teleportPlayer({
        playerId: UI.$('teleport-player-select').value,
        roomId: UI.$('teleport-room-id').value.trim(),
        roomName: nullableText(UI.$('teleport-room-name').value),
        roomDescription: nullableText(UI.$('teleport-room-description').value),
        connectFromCurrentRoom: UI.$('teleport-connect').checked,
        entryDirection: UI.$('teleport-direction').value,
        environmentTags: csvList(UI.$('teleport-tags').value)
      });
      UI.appendActivity('mutation-log', result.summary || 'Teleport complete.', 'success');
      form.reset();
      await afterMutation(result);
    } catch (error) {
      await handleError(error, { logId: 'mutation-log' });
    }
  }

  async function handleItemMutation(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const form = event.currentTarget;
    try {
      const result = await API.grantItem({
        playerId: UI.$('item-player-select').value,
        name: UI.$('item-name').value.trim(),
        type: UI.$('item-type').value,
        quantity: Math.max(1, numberValue(UI.$('item-quantity').value, 1)),
        description: nullableText(UI.$('item-description').value),
        effect: nullableText(UI.$('item-effect').value),
        autoEquip: UI.$('item-auto-equip').checked
      });
      UI.appendActivity('mutation-log', result.summary || 'Item granted.', 'success');
      form.reset();
      await afterMutation(result);
    } catch (error) {
      await handleError(error, { logId: 'mutation-log' });
    }
  }

  async function handleStatusMutation(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const form = event.currentTarget;
    try {
      const result = await API.applyStatus({
        playerId: UI.$('status-player-select').value,
        name: UI.$('status-name').value.trim(),
        type: UI.$('status-type').value,
        remainingTurns: Math.max(1, numberValue(UI.$('status-turns').value, 1)),
        description: nullableText(UI.$('status-description').value),
        statModifiersText: nullableText(UI.$('status-modifiers').value)
      });
      UI.appendActivity('mutation-log', result.summary || 'Status applied.', 'success');
      form.reset();
      await afterMutation(result);
    } catch (error) {
      await handleError(error, { logId: 'mutation-log' });
    }
  }

  async function handleRoomFixtureMutation(event) {
    event.preventDefault();
    if (!ensureAuthenticated() || !ensureAdmin()) return;

    const form = event.currentTarget;
    const exitDirection = nullableText(UI.$('fixture-exit-direction').value);
    const exitTarget = nullableText(UI.$('fixture-exit-target').value);
    const fixtureItemName = nullableText(UI.$('fixture-item-name').value);
    const fixtureNpcName = nullableText(UI.$('fixture-npc-name').value);

    try {
      const result = await API.upsertRoomFixture({
        roomId: UI.$('fixture-room-id').value.trim(),
        name: nullableText(UI.$('fixture-room-name').value),
        description: nullableText(UI.$('fixture-room-description').value),
        environmentTags: csvList(UI.$('fixture-tags').value),
        exits: exitDirection && exitTarget ? { [exitDirection]: exitTarget } : null,
        items: fixtureItemName ? [{ name: fixtureItemName, type: 'Misc', quantity: 1 }] : [],
        npcs: fixtureNpcName ? [{ name: fixtureNpcName, isHostile: UI.$('fixture-npc-hostile').checked }] : []
      });
      UI.appendActivity('mutation-log', result.summary || 'Room fixture updated.', 'success');
      form.reset();
      await afterMutation(result);
    } catch (error) {
      await handleError(error, { logId: 'mutation-log' });
    }
  }

  async function afterMutation(result) {
    await Promise.all([
      refreshPlayers(),
      refreshRooms(),
      refreshSummary(),
      refreshHealth()
    ]);

    if (state.currentPlayerId) {
      await refreshCurrentPlayer();
    }

    if (result?.player?.id && result.player.id === state.currentPlayerId) {
      await refreshStory();
    }
  }

  async function afterCommand(playerId, result) {
    // Track interaction mode from result
    if (result.interactionUpdate) {
      state.interactionMode = normalizeMode(result.interactionUpdate.mode);
      state.interactionTarget = result.interactionUpdate.target || state.interactionTarget;
    }

    await Promise.all([
      refreshPlayers(),
      refreshRooms(),
      refreshSummary(),
      refreshHealth()
    ]);

    if (playerId === state.currentPlayerId) {
      if (result.newRoom) {
        state.currentRoomId = result.newRoom.id;
        UI.renderRoom(result.newRoom);
      }
      await refreshCurrentPlayer();
      UI.updateInteractionMode(state.interactionMode, state.interactionTarget);
    }
  }

  function handleTransportStatus(status) {
    state.transportLabel = status === 'connected' ? 'SignalR' : status === 'polling' ? 'Polling' : 'Offline';
    UI.setConnectionStatus(status);
    UI.renderSummary(state.summary, state.transportLabel, state.session);
  }

  function handleRealtimeActionResult(result) {
    if (!state.currentPlayerId) return;
    // Skip if we already rendered this from the HTTP response
    if (result.actionId && state.recentActionIds.has(result.actionId)) {
      state.recentActionIds.delete(result.actionId);
      return;
    }
    UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
    void afterCommand(state.currentPlayerId, result);
  }

  function handleRealtimeGameEvent(event) {
    if (!state.currentPlayerId) return;
    void refreshCurrentPlayer().catch((error) => handleError(error, { story: true }));
  }

  function handleRealtimeRoomEvent(event) {
    if (!state.currentPlayerId) return;
    void refreshCurrentPlayer().catch((error) => handleError(error, { story: true }));
  }

  function handleRealtimePlayerEvent() {
    if (!state.currentPlayerId) return;
    void refreshCurrentPlayer().catch((error) => handleError(error, { story: true }));
  }

  function startRefreshLoop() {
    stopRefreshLoop();
    state.refreshTimer = window.setInterval(() => {
      if (!state.session) return;
      void refreshAll().catch((error) => handleError(error, { portal: true }));
    }, 15000);
  }

  function stopRefreshLoop() {
    if (state.refreshTimer) {
      clearInterval(state.refreshTimer);
      state.refreshTimer = null;
    }
  }

  async function handleError(error, options = {}) {
    if (error?.code === 'unauthorized' || error?.status === 401) {
      await forceSignedOut('Session expired. Sign in again to continue.', 'error');
      return;
    }

    const message = error?.message || 'The requested operation failed.';
    if (options.auth) {
      UI.setAuthMessage(message, 'error');
    }
    if (options.portal) {
      UI.setPortalMessage(message, 'error');
    }
    if (options.story) {
      UI.appendStoryEntry({ mechanicalSummary: message }, 'failure');
    }
    if (options.logId) {
      UI.appendActivity(options.logId, message, 'failure');
    }
  }

  function nullableText(value) {
    const trimmed = String(value || '').trim();
    return trimmed || null;
  }

  function numberValue(value, fallback = 0) {
    const parsed = Number.parseInt(value, 10);
    return Number.isFinite(parsed) ? parsed : fallback;
  }

  function nullableNumber(value) {
    const trimmed = String(value || '').trim();
    if (!trimmed) return null;
    const parsed = Number.parseInt(trimmed, 10);
    return Number.isFinite(parsed) ? parsed : null;
  }

  function csvList(value) {
    return String(value || '')
      .split(',')
      .map((entry) => entry.trim())
      .filter(Boolean);
  }

  init().catch((error) => {
    console.error('Dashboard boot failed:', error);
    UI.setPortalMessage(error.message || 'Dashboard boot failed.', 'error');
  });
})();