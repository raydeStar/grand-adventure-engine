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
    interactionTarget: '',
    llmActive: '',
    llmModels: [],
    worlds: [],
    selectedWorldId: localStorage.getItem('gae.admin.world') || '',
    selectedWorld: null,
    selectedWorldPlayers: []
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

  function appendOverviewMessage(text) {
    const messages = UI.$('ov-chat-messages');
    if (!messages) return;
    messages.innerHTML += `<div class="dm-ai-msg system">${UI.esc(text)}</div>`;
    messages.classList.remove('hidden');
    messages.scrollTop = messages.scrollHeight;
  }

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
      return;
    }

    await onAuthenticated(session);
  }

  function bindEvents() {
    const bind = (id, eventName, handler) => {
      const element = UI.$(id);
      if (!element) {
        console.warn(`Dashboard boot: missing expected element #${id}`);
        return null;
      }

      element.addEventListener(eventName, handler);
      return element;
    };

    const bindOptional = (id, eventName, handler) => {
      UI.$(id)?.addEventListener(eventName, handler);
    };

    bind('auth-form', 'submit', handleLogin);
    bind('btn-fill-user', 'click', () => fillCredentials('user'));
    bind('btn-fill-admin', 'click', () => fillCredentials('admin'));
    bind('btn-logout-header', 'click', () => void handleLogout());
    bind('btn-logout-portal', 'click', () => void handleLogout());

    // Theme toggle
    const themeBtn = UI.$('btn-theme-toggle');
    if (themeBtn) themeBtn.addEventListener('click', () => UI.toggleTheme());

    bind('btn-new-char', 'click', () => {
      UI.showCreateForm(true);
    });
    bind('btn-cancel-create', 'click', () => UI.showCreateForm(false));
    bind('btn-open-portal', 'click', () => {
      UI.showPortal(true);
      UI.showDashboard(false);
    });
    bind('btn-refresh', 'click', () => {
      if (!ensureAuthenticated()) return;
      void refreshAll().catch((error) => handleError(error, { portal: true, logId: 'workflow-log' }));
      UI.appendActivity('workflow-log', 'Manual refresh requested.', 'info');
    });
    bind('btn-seed-demo-admin', 'click', () => void seedDemoCharacters(false));
    bind('btn-llm-refresh', 'click', () => void refreshLlmModels());
    bind('llm-models-list', 'click', handleLlmModelClick);

    bind('create-form', 'submit', handleCreateCharacter);
    bind('command-input', 'keydown', handleCommandKeydown);
    bind('command-form', 'submit', handleUserCommand);
    bind('admin-command-form', 'submit', handleAdminCommand);
    bindOptional('room-fixture-form', 'submit', handleRoomFixtureMutation);
    bindOptional('btn-send-msg', 'click', () => void handleSendMessage());
    bindOptional('msg-player-select', 'change', updateSendButtonLabel);
    bindOptional('btn-warp-to-spawn', 'click', () => void handleWarpToSpawn());
    bindOptional('btn-warp-all-spawn', 'click', () => void handleWarpAllToSpawn());
    bindOptional('btn-reseed-world', 'click', () => void handleReseedWorld());
    bindOptional('btn-seed-demo-world', 'click', () => void handleSeedDemoWorld());
    bindOptional('btn-reset-world', 'click', () => void handleResetWorld());
    bindOptional('btn-export-world', 'click', () => void handleExportWorld());
    bindOptional('btn-import-world', 'click', () => handleImportWorldClick());
    bindOptional('import-world-file', 'change', (e) => void handleImportWorldFile(e));

    // World management events
    bindOptional('btn-create-world', 'click', showCreateWorldForm);
    bindOptional('world-form', 'submit', handleWorldFormSubmit);
    bindOptional('world-form-cancel', 'click', hideWorldForm);
    bindOptional('btn-edit-world', 'click', showEditWorldForm);
    bindOptional('btn-delete-world', 'click', () => void handleDeleteWorld());
    bindOptional('btn-create-portal', 'click', showCreatePortalForm);
    bindOptional('portal-form', 'submit', handlePortalFormSubmit);
    bindOptional('portal-form-cancel', 'click', hidePortalForm);
    bindOptional('btn-realm-transfer', 'click', () => void handleRealmTransfer());
    bindOptional('world-list', 'click', handleWorldListClick);
    bindOptional('portal-list', 'click', handlePortalListClick);

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

    // Admin tab switching
    document.querySelectorAll('[data-admin-tab]').forEach((tab) => {
      tab.addEventListener('click', () => switchAdminTab(tab.dataset.adminTab));
    });
    // Restore persisted admin tab styling without triggering protected loads before auth.
    const savedTab = localStorage.getItem('gae.admin.tab');
    const normalizedSavedTab = savedTab === 'dm-console' ? 'overview' : savedTab;
    const validTabs = new Set([...document.querySelectorAll('[data-admin-tab]')].map(t => t.dataset.adminTab));
    if (normalizedSavedTab && validTabs.has(normalizedSavedTab)) switchAdminTab(normalizedSavedTab);

    // Wire up AI logs tab events
    UI.wireLogsTab();

    // Wire up content registry tab
    UI.wireRegistryTab();

    // Wire up overview browser (players + rooms)
    UI.wireOverviewBrowser();

    // Handle play-as-player from overview detail panel
    document.addEventListener('overview-play-player', async (e) => {
      const { playerId } = e.detail;
      if (playerId) void activatePlayer(playerId, 'user');
    });

    // Smoke test from overview
    document.addEventListener('overview-smoke-player', async (e) => {
      const { playerId } = e.detail;
      if (!playerId || !ensureAdmin()) return;
      const messages = UI.$('ov-chat-messages');
      if (messages) { messages.classList.remove('hidden'); }
      const commands = workflowScenarios['smoke-user'];
      for (const command of commands) {
        try {
          if (messages) messages.innerHTML += `<div class="dm-ai-msg system">${UI.esc(playerId)} &gt; ${UI.esc(command)}</div>`;
          const result = await API.sendCommand(playerId, command);
          if (result.actionId) state.recentActionIds.add(result.actionId);
          const summary = result.mechanicalSummary || 'OK';
          if (messages) messages.innerHTML += `<div class="dm-ai-msg ai">${UI.esc(summary)}</div>`;
          if (playerId === state.currentPlayerId) UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
          await afterCommand(playerId, result);
        } catch (err) {
          if (messages) messages.innerHTML += `<div class="dm-ai-msg system">Error: ${UI.esc(err.message)}</div>`;
          break;
        }
      }
      if (messages) {
        messages.innerHTML += `<div class="dm-ai-msg system">Smoke test complete.</div>`;
        messages.scrollTop = messages.scrollHeight;
      }
    });

    // Teleport to spawn from overview
    document.addEventListener('overview-teleport-spawn', async (e) => {
      const { playerId } = e.detail;
      if (!playerId || !ensureAdmin()) return;
      try {
        await API.teleportPlayer({ playerId, roomId: 'spawn' });
        const messages = UI.$('ov-chat-messages');
        if (messages) {
          messages.innerHTML += `<div class="dm-ai-msg system">Teleported to spawn.</div>`;
          messages.classList.remove('hidden');
          messages.scrollTop = messages.scrollHeight;
        }
        if (playerId === state.currentPlayerId) await refreshCurrentPlayer();
        // Refresh the card data
        await UI._ovFetchAndSelect(playerId, 'player');
      } catch (err) {
        alert('Teleport failed: ' + err.message);
      }
    });

    // Send Discord message from overview
    document.addEventListener('overview-discord-msg', async (e) => {
      const { playerId, message } = e.detail;
      if (!playerId || !message || !ensureAdmin()) return;
      try {
        await API.sendMessage({ playerId, message });
        const messages = UI.$('ov-chat-messages');
        if (messages) {
          messages.innerHTML += `<div class="dm-ai-msg system">Discord message sent.</div>`;
          messages.classList.remove('hidden');
          messages.scrollTop = messages.scrollHeight;
        }
      } catch (err) {
        alert('Discord send failed: ' + err.message);
      }
    });

    // Add item from overview item picker
    document.addEventListener('overview-add-item', async (e) => {
      const { playerId, template, autoEquip } = e.detail;
      if (!playerId || !template || !ensureAdmin()) return;
      try {
        const result = await API.grantItem({
          playerId,
          name: template.name,
          type: template.type || 'Misc',
          quantity: 1,
          value: template.value || 0,
          description: template.description || '',
          damageDice: template.damageDice || null,
          damageStat: template.damageStat || null,
          armorValue: template.armorValue || 0,
          isEquippable: template.isEquippable,
          isConsumable: template.isConsumable,
          isTwoHanded: template.isTwoHanded || false,
          effect: template.effect || null,
          statBonuses: template.statBonuses || {},
          autoEquip
        });
        const summary = result.summary || `Granted ${template.name}.`;
        if (playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(playerId, 'player');
        appendOverviewMessage(summary);
      } catch (err) {
        const messages = UI.$('ov-chat-messages');
        if (messages) {
          messages.innerHTML += `<div class="dm-ai-msg system">Error: ${UI.esc(err.message)}</div>`;
          messages.classList.remove('hidden');
        } else {
          alert('Grant item failed: ' + err.message);
        }
      }
    });

    document.addEventListener('overview-item-action', async (e) => {
      const { playerId, itemId, action } = e.detail;
      if (!playerId || !itemId || !action || !ensureAdmin()) return;
      try {
        const result = await API.itemAction({ playerId, itemId, action });
        const summary = result.summary || 'Item updated.';
        if (playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(playerId, 'player');
        appendOverviewMessage(summary);
      } catch (err) {
        const messages = UI.$('ov-chat-messages');
        if (messages) {
          messages.innerHTML += `<div class="dm-ai-msg system">Error: ${UI.esc(err.message)}</div>`;
          messages.classList.remove('hidden');
          messages.scrollTop = messages.scrollHeight;
        } else {
          alert('Item update failed: ' + err.message);
        }
      }
    });

    // Run command from overview detail panel
    document.addEventListener('overview-run-command', async (e) => {
      const { playerId, command } = e.detail;
      if (!playerId || !command || !ensureAdmin()) return;
      try {
        const result = await API.sendCommand(playerId, command);
        if (result.actionId) state.recentActionIds.add(result.actionId);
        appendOverviewMessage(`${playerId} > ${command}`);
        appendOverviewMessage(result.mechanicalSummary || 'OK');
        if (playerId === state.currentPlayerId) UI.appendStoryEntry(result, result.success ? 'success' : 'failure');
        await afterCommand(playerId, result);
        await UI._ovFetchAndSelect(playerId, 'player');
        const input = UI.$('ov-cmd-input');
        if (input) input.value = '';
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Adjust resources from overview detail panel
    document.addEventListener('overview-adjust-resources', async (e) => {
      const d = e.detail;
      if (!d.playerId || !ensureAdmin()) return;
      try {
        const result = await API.adjustResources(d);
        appendOverviewMessage(result.summary || 'Resources updated.');
        if (d.playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(d.playerId, 'player');
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Teleport from overview detail panel
    document.addEventListener('overview-teleport', async (e) => {
      const { playerId, roomId } = e.detail;
      if (!playerId || !roomId || !ensureAdmin()) return;
      try {
        await API.teleportPlayer({ playerId, roomId });
        appendOverviewMessage(`Teleported to ${roomId}.`);
        if (playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(playerId, 'player');
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Apply status from overview detail panel
    document.addEventListener('overview-apply-status', async (e) => {
      const d = e.detail;
      if (!d.playerId || !d.name || !ensureAdmin()) return;
      try {
        const result = await API.applyStatus(d);
        appendOverviewMessage(result.summary || `Applied ${d.name}.`);
        if (d.playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(d.playerId, 'player');
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Grant custom item from overview detail panel
    document.addEventListener('overview-grant-item', async (e) => {
      const { playerId, name, type } = e.detail;
      if (!playerId || !name || !ensureAdmin()) return;
      try {
        const result = await API.grantItem({ playerId, name, type, quantity: 1 });
        appendOverviewMessage(result.summary || `Granted ${name}.`);
        if (playerId === state.currentPlayerId) await refreshCurrentPlayer();
        await UI._ovFetchAndSelect(playerId, 'player');
        const input = UI.$('ov-grant-name');
        if (input) input.value = '';
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Add item to room from overview detail panel
    document.addEventListener('overview-room-add-item', async (e) => {
      const { roomId, name, type } = e.detail;
      if (!roomId || !name || !ensureAdmin()) return;
      try {
        const room = await API.getRoom(roomId);
        const items = [...(room?.items || []), { name, type, quantity: 1 }];
        await API.upsertRoomFixture({ roomId, items });
        appendOverviewMessage(`Added ${name} to room.`);
        await UI._ovFetchAndSelect(roomId, 'room');
        const input = UI.$('ov-room-item-name');
        if (input) input.value = '';
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    // Add NPC to room from overview detail panel
    document.addEventListener('overview-room-add-npc', async (e) => {
      const { roomId, name, isHostile } = e.detail;
      if (!roomId || !name || !ensureAdmin()) return;
      try {
        const room = await API.getRoom(roomId);
        const npcs = [...(room?.npcs || []), { name, isHostile }];
        await API.upsertRoomFixture({ roomId, npcs });
        appendOverviewMessage(`Added NPC "${name}" to room.`);
        await UI._ovFetchAndSelect(roomId, 'room');
        const input = UI.$('ov-room-npc-name');
        if (input) input.value = '';
      } catch (err) {
        appendOverviewMessage(`Error: ${err.message}`);
      }
    });

    UI.$('resume-form').addEventListener('submit', handleResumeCharacter);

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
    state.mode = UI.setMode(session.isAdmin ? 'admin' : 'user', state.session);
    localStorage.setItem('gae.operator.mode', state.mode);
    UI.renderNoActivePlayer(true);
    UI.setConnectionStatus('connecting');

    // Enter the authenticated shell immediately; background data loads can finish after the
    // dashboard is visible instead of keeping the user on the signed-in portal.
    openDashboard(state.mode);

    try {
      await refreshAll();
    } catch (error) {
      await handleError(error, { logId: 'workflow-log' });
    }

    startRefreshLoop();
    void GameHub.connect();
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
    UI.clearActivity('mutation-log');
    UI.setPortalMessage(message, tone);
  }

  function fillCredentials(role) {
    const hint = state.loginHints.find((entry) => entry.role === role);
    if (!hint) return;

    UI.$('auth-username').value = hint.username;
    UI.$('auth-password').value = hint.password;
    UI.$('auth-form').requestSubmit();
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
      UI.showPlayerSelect(true);
    }
  }

  function switchAdminTab(tabName) {
    // Migrate legacy stored tab names to current names
    if (tabName === 'dm-console') tabName = 'overview';
    if (tabName === 'world-actions' || tabName === 'commands' || tabName === 'mutations') tabName = 'tools';

    document.querySelectorAll('[data-admin-tab]').forEach((btn) => {
      btn.classList.toggle('active', btn.dataset.adminTab === tabName);
    });
    document.querySelectorAll('[data-admin-panel]').forEach((panel) => {
      panel.classList.toggle('active', panel.dataset.adminPanel === tabName);
    });
    localStorage.setItem('gae.admin.tab', tabName);

    if (!state.session?.isAdmin) {
      return;
    }

    // Auto-load data when switching tabs
    if (tabName === 'logs') {
      UI.loadConversationLogs();
    }
    if (tabName === 'registry') {
      UI.populateRegistryWorldFilter(state.worlds);
      UI.loadRegistry();
    }
    // World management tab
    if (tabName === 'worlds' || tabName === 'overview') {
      void refreshWorlds();
    }
  }

  async function refreshAll() {
    if (!state.session) return;

    await Promise.all([
      refreshPlayers(),
      refreshRooms(),
      refreshSummary(),
      refreshHealth(),
      refreshLlmModels(),
      refreshWorlds()
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

  async function refreshLlmModels() {
    if (!state.session?.isAdmin) return;

    try {
      const data = await API.getLlmModels();
      state.llmActive = data.active || 'unknown';
      state.llmModels = data.available || [];
      renderLlmPanel();
    } catch (error) {
      UI.$('llm-active-model').textContent = 'Error loading model info';
      UI.$('llm-models-list').innerHTML = '<div class="empty-state">Could not reach LM Studio.</div>';
    }
  }

  function renderLlmPanel() {
    UI.$('llm-active-model').textContent = state.llmActive;

    const list = UI.$('llm-models-list');
    if (!state.llmModels.length) {
      list.innerHTML = '<div class="empty-state">No models loaded in LM Studio.</div>';
      return;
    }

    list.innerHTML = state.llmModels.map((id) => {
      const isActive = id === state.llmActive;
      return `<div class="llm-model-card${isActive ? ' active' : ''}" data-llm-model="${id}">
        <span class="model-id">${id}</span>
        ${isActive ? '<span class="model-badge">Active</span>' : '<button class="btn btn-primary btn-sm">Use This Model</button>'}
      </div>`;
    }).join('');
  }

  async function handleLlmModelClick(event) {
    const card = event.target.closest('[data-llm-model]');
    if (!card) return;

    const model = card.dataset.llmModel;
    if (model === state.llmActive) return;
    if (!ensureAdmin()) return;

    try {
      const result = await API.setLlmModel(model);
      state.llmActive = result.active || model;
      renderLlmPanel();
      UI.appendActivity('llm-log', result.summary || `Switched to ${model}.`, 'success');
    } catch (error) {
      UI.appendActivity('llm-log', error.message || 'Failed to switch model.', 'failure');
    }
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
    UI.showPlayerSelect(false);
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
      await refreshAll();
      await activatePlayer(player.id, 'user');
    } catch (error) {
      UI.setResumeMessage(error.message || 'Failed to create character.', 'error');
    } finally {
      submit.disabled = false;
      submit.textContent = 'Create';
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

  async function handleResumeCharacter(event) {
    event.preventDefault();
    if (!ensureAuthenticated()) return;
    const playerId = UI.$('resume-player-id')?.value.trim() || '';
    if (!playerId) return;
    try {
      await activatePlayer(playerId, 'user');
    } catch (error) {
      UI.setResumeMessage(error.message || 'Could not find that character.', 'error');
    }
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
      if (!UI._streamNode) {
        input.disabled = false;
        UI.$('command-submit').disabled = false;
        input.focus();
      }
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

  function updateSendButtonLabel() {
    const btn = UI.$('btn-send-msg');
    const playerId = UI.$('msg-player-select').value;
    if (playerId) {
      const sel = UI.$('msg-player-select');
      const name = sel.options[sel.selectedIndex]?.text || 'Player';
      btn.textContent = `Send to ${name.split(' (')[0]}`;
    } else {
      btn.textContent = 'Broadcast to All Players';
    }
  }

  async function handleSendMessage() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    const resultEl = UI.$('send-message-result');
    const message = UI.$('msg-text').value.trim();
    if (!message) {
      resultEl.textContent = 'Message cannot be empty.';
      resultEl.style.color = 'var(--bad)';
      return;
    }

    const playerId = UI.$('msg-player-select').value;
    const isBroadcast = !playerId;

    if (isBroadcast && !confirm('Broadcast this message to ALL players?')) return;

    resultEl.textContent = 'Sending...';
    resultEl.style.color = 'var(--dim)';

    try {
      const result = await API.sendMessage({ playerId: playerId || undefined, message });
      const label = playerId ? `Sent to 1 player.` : `Broadcast sent to ${result.sent} player(s).`;
      resultEl.textContent = label;
      resultEl.style.color = 'var(--ok)';
      UI.appendActivity('mutation-log', label, 'success');
      UI.$('msg-text').value = '';
    } catch (error) {
      resultEl.textContent = `Error: ${error.message}`;
      resultEl.style.color = 'var(--bad)';
      UI.appendActivity('mutation-log', `Send message failed: ${error.message}`, 'error');
    }
  }

  // ── World Management handlers ──

  async function refreshWorlds() {
    if (!state.session?.isAdmin) return;
    try {
      state.worlds = await API.getWorlds();
      UI.renderWorldList(state.worlds, state.selectedWorldId);
      UI.populateWorldSelects(state.worlds, state.selectedWorldId);
      UI.populateOverviewWorldFilter(state.worlds);
      UI.populateTransferPlayerSelect(state.players);

      // Re-select world if previously selected
      if (state.selectedWorldId) {
        await selectWorld(state.selectedWorldId);
      }
    } catch { /* ignore if not admin */ }
  }

  async function selectWorld(worldId) {
    state.selectedWorldId = worldId;
    localStorage.setItem('gae.admin.world', worldId);
    UI.renderWorldList(state.worlds, worldId);
    const ctx = UI.$('world-context-section');

    if (!worldId) {
      state.selectedWorld = null;
      state.selectedWorldPlayers = [];
      UI.renderWorldDetail(null);
      UI.renderPortalList(null);
      UI.populateWorldSelects(state.worlds, null);
      if (ctx) ctx.classList.add('hidden');
      return;
    }

    try {
      const [world, players] = await Promise.all([
        API.getWorld(worldId),
        API.getWorldPlayers(worldId)
      ]);
      state.selectedWorld = world;
      state.selectedWorldPlayers = players || [];
      UI.renderWorldDetail(world, state.selectedWorldPlayers);
      await wireWorldNarratorControls(world);
      wireStatControls(world);
      UI.renderPortalList(world?.portals || [], state.worlds);
      UI.populateWorldSelects(state.worlds, worldId);
      if (ctx) ctx.classList.remove('hidden');
    } catch (e) {
      state.selectedWorld = null;
      state.selectedWorldPlayers = [];
      UI.renderWorldDetail(null);
      UI.renderPortalList(null);
      if (ctx) ctx.classList.add('hidden');
    }
  }

  function handleWorldListClick(event) {
    const card = event.target.closest('[data-world-id]');
    if (!card) return;
    const worldId = card.dataset.worldId;
    void selectWorld(worldId === state.selectedWorldId ? '' : worldId);
  }

  async function wireWorldNarratorControls(world) {
    // Populate narrator select & checkboxes from registry
    try {
      const summary = await API.getRegistrySummary();
      const presets = summary.narratorPresets || 0;
      let allPresets = [];
      if (presets > 0) {
        allPresets = await API.getRegistry('narrator_presets');
      }

      // Default narrator select
      const sel = document.getElementById('world-default-narrator');
      if (sel) {
        sel.innerHTML = '<option value="">System default</option>' +
          allPresets.map(p => `<option value="${UI.esc(p.id)}" ${p.id === (world.defaultNarratorPresetId || '') ? 'selected' : ''}>${UI.esc(p.name || p.id)}</option>`).join('');
      }

      // Available narrators checkboxes
      const cbContainer = document.getElementById('world-narrator-checkboxes');
      if (cbContainer) {
        const selected = world.narratorPresetIds || [];
        cbContainer.innerHTML = allPresets.map(p => {
          const checked = selected.includes(p.id) ? 'checked' : '';
          return `<label style="display:flex;align-items:center;gap:0.35rem;cursor:pointer;">
            <input type="checkbox" class="world-narrator-cb" value="${UI.esc(p.id)}" ${checked}>
            ${UI.esc(p.name || p.id)} <span style="color:var(--dim);font-size:10px;">(${UI.esc(p.archetype || '')})</span>
          </label>`;
        }).join('');
      }
    } catch (e) {
      console.error('Failed to load narrator presets for world settings:', e);
      const cbContainer = document.getElementById('world-narrator-checkboxes');
      if (cbContainer) cbContainer.innerHTML = `<div style="color:var(--error);font-size:11px;">Error loading presets: ${e.message}</div>`;
    }

    // Wire save button
    const saveBtn = document.getElementById('world-intro-save');
    if (saveBtn) {
      saveBtn.onclick = async () => {
        const intro = document.getElementById('world-intro-text')?.value || '';
        const defaultNarrator = document.getElementById('world-default-narrator')?.value || '';
        const cbs = document.querySelectorAll('.world-narrator-cb:checked');
        const narratorIds = Array.from(cbs).map(cb => cb.value);

        try {
          saveBtn.disabled = true;
          saveBtn.textContent = 'Saving...';
          await API.updateWorld(world.id, {
            characterCreationIntro: intro || '',
            defaultNarratorPresetId: defaultNarrator || '',
            narratorPresetIds: narratorIds
          });
          saveBtn.textContent = 'Saved!';
          setTimeout(() => { saveBtn.textContent = 'Save Settings'; saveBtn.disabled = false; }, 1500);
        } catch (e) {
          saveBtn.textContent = 'Error';
          setTimeout(() => { saveBtn.textContent = 'Save Settings'; saveBtn.disabled = false; }, 2000);
          console.error('Failed to save world settings:', e);
        }
      };
    }

    // Wire generate button
    const genBtn = document.getElementById('world-intro-generate');
    if (genBtn) {
      genBtn.onclick = async () => {
        const textarea = document.getElementById('world-intro-text');
        const wid = world?.id;
        console.log('Generate intro clicked, world.id:', wid);
        if (!wid) { genBtn.textContent = 'Error: no world'; return; }
        try {
          genBtn.disabled = true;
          genBtn.textContent = 'Generating...';
          const result = await API.generateWorldIntro(wid);
          console.log('Generate result:', result);
          if (textarea && result.intro) textarea.value = result.intro;
          genBtn.textContent = 'AI Generate';
          genBtn.disabled = false;
        } catch (e) {
          console.error('Generate intro failed:', e, 'status:', e.status, 'code:', e.code);
          genBtn.textContent = `Failed (${e.status || '?'})`;
          setTimeout(() => { genBtn.textContent = 'AI Generate'; genBtn.disabled = false; }, 4000);
        }
      };
    }
  }

  // ── Stat Definitions CRUD ─────────────────────────────
  function wireStatControls(world) {
    const listEl = document.getElementById('world-stat-list');
    const addBtn = document.getElementById('world-stat-add');
    if (!listEl) return;

    // Edit button handlers
    listEl.querySelectorAll('[data-stat-edit]').forEach(btn => {
      btn.onclick = () => showStatEditor(world, btn.dataset.statEdit);
    });

    // Delete button handlers
    listEl.querySelectorAll('[data-stat-delete]').forEach(btn => {
      btn.onclick = async () => {
        const key = btn.dataset.statDelete;
        if (!confirm(`Delete stat "${key}"?`)) return;
        const stats = { ...(world.rules?.stats || {}) };
        delete stats[key];
        try {
          await API.updateWorld(world.id, { rules: { ...world.rules, stats } });
          await selectWorld(world.id);
        } catch (e) { alert('Failed to delete stat: ' + e.message); }
      };
    });

    // Add button
    if (addBtn) {
      addBtn.onclick = () => showStatEditor(world, null);
    }
  }

  function showStatEditor(world, editKey) {
    const existing = editKey ? (world.rules?.stats || {})[editKey] : null;
    const listEl = document.getElementById('world-stat-list');
    if (!listEl) return;

    // Remove any existing editor
    document.getElementById('stat-editor-form')?.remove();

    const categories = ['attribute', 'resource', 'currency'];
    const form = document.createElement('div');
    form.id = 'stat-editor-form';
    form.style.cssText = 'padding:0.5rem;border:1px solid var(--accent);border-radius:4px;margin-top:0.35rem;background:var(--bg-secondary);';
    form.innerHTML = `
      <div style="font-size:11px;font-weight:600;margin-bottom:0.35rem;">${editKey ? 'Edit' : 'Add'} Stat</div>
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:0.35rem;font-size:11px;">
        <label>Key<input id="stat-ed-key" type="text" value="${UI.esc(editKey || '')}" ${editKey ? 'disabled' : ''} style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;" placeholder="e.g. str"></label>
        <label>Display Name<input id="stat-ed-display" type="text" value="${UI.esc(existing?.display || '')}" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;" placeholder="e.g. Strength"></label>
        <label>Category<select id="stat-ed-category" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;">
          ${categories.map(c => `<option value="${c}" ${(existing?.category || 'attribute') === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></label>
        <label>Base<input id="stat-ed-base" type="number" value="${existing?.base ?? 10}" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;"></label>
        <label>Min<input id="stat-ed-min" type="number" value="${existing?.min ?? 1}" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;"></label>
        <label>Max<input id="stat-ed-max" type="number" value="${existing?.max ?? 20}" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;"></label>
      </div>
      <label style="display:block;margin-top:0.35rem;font-size:11px;">Semantic Tags <span style="color:var(--dim);">(comma separated)</span>
        <input id="stat-ed-tags" type="text" value="${UI.esc((existing?.semanticTags || []).join(', '))}" style="width:100%;padding:3px 5px;font-size:11px;background:var(--bg);color:var(--text);border:1px solid var(--border);border-radius:3px;" placeholder="e.g. physical, melee">
      </label>
      <div style="display:flex;gap:0.5rem;margin-top:0.5rem;">
        <button class="btn btn-primary btn-sm" id="stat-ed-save" type="button">${editKey ? 'Save' : 'Add'}</button>
        <button class="btn btn-secondary btn-sm" id="stat-ed-cancel" type="button">Cancel</button>
      </div>
    `;
    listEl.parentElement.appendChild(form);

    document.getElementById('stat-ed-cancel').onclick = () => form.remove();
    document.getElementById('stat-ed-save').onclick = async () => {
      const key = (document.getElementById('stat-ed-key').value || '').trim().toLowerCase();
      if (!key) { alert('Stat key is required.'); return; }
      if (!editKey && (world.rules?.stats || {})[key]) { alert(`Stat "${key}" already exists.`); return; }

      const stat = {
        display: document.getElementById('stat-ed-display').value.trim() || key.toUpperCase(),
        category: document.getElementById('stat-ed-category').value,
        base: parseInt(document.getElementById('stat-ed-base').value) || 10,
        min: parseInt(document.getElementById('stat-ed-min').value) || 0,
        max: parseInt(document.getElementById('stat-ed-max').value) || 20,
        semanticTags: document.getElementById('stat-ed-tags').value.split(',').map(t => t.trim()).filter(Boolean)
      };

      const stats = { ...(world.rules?.stats || {}), [key]: stat };
      try {
        const saveBtn = document.getElementById('stat-ed-save');
        saveBtn.disabled = true;
        saveBtn.textContent = 'Saving...';
        await API.updateWorld(world.id, { rules: { ...world.rules, stats } });
        form.remove();
        await selectWorld(world.id);
      } catch (e) { alert('Failed to save stat: ' + e.message); }
    };

    // Focus first input
    setTimeout(() => document.getElementById(editKey ? 'stat-ed-display' : 'stat-ed-key')?.focus(), 50);
  }

  function showCreateWorldForm() {
    const panel = UI.$('world-form-panel');
    if (!panel) return;
    panel.classList.remove('hidden');
    UI.$('world-form-title').textContent = 'Create World';
    UI.$('world-form-submit').textContent = 'Create World';
    UI.$('world-form-id').value = '';
    UI.$('world-form-name').value = '';
    UI.$('world-form-desc').value = '';
    UI.$('world-form-spawn').value = '';
    UI.$('world-form-tags').value = '';
    UI.$('world-form-result').textContent = '';
  }

  function showEditWorldForm() {
    if (!state.selectedWorld) return;
    const w = state.selectedWorld;
    const panel = UI.$('world-form-panel');
    if (!panel) return;
    panel.classList.remove('hidden');
    UI.$('world-form-title').textContent = 'Edit World';
    UI.$('world-form-submit').textContent = 'Save Changes';
    UI.$('world-form-id').value = w.id;
    UI.$('world-form-name').value = w.name || '';
    UI.$('world-form-desc').value = w.description || '';
    UI.$('world-form-spawn').value = w.spawnRoomId || '';
    UI.$('world-form-tags').value = (w.tags || []).join(', ');
    UI.$('world-form-result').textContent = '';
  }

  function hideWorldForm() {
    UI.$('world-form-panel')?.classList.add('hidden');
  }

  async function handleWorldFormSubmit(event) {
    event.preventDefault();
    if (!ensureAdmin()) return;
    const resultEl = UI.$('world-form-result');
    const editId = UI.$('world-form-id').value;
    const name = UI.$('world-form-name').value.trim();
    const description = UI.$('world-form-desc').value.trim();
    const spawnRoomId = UI.$('world-form-spawn').value.trim() || 'spawn';
    const tags = UI.$('world-form-tags').value.split(',').map(t => t.trim()).filter(Boolean);

    resultEl.textContent = editId ? 'Saving...' : 'Creating...';
    resultEl.className = 'world-action-result';

    try {
      if (editId) {
        await API.updateWorld(editId, { name, description, spawnRoomId, tags });
        resultEl.textContent = 'World updated!';
      } else {
        await API.createWorld({ name, description, spawnRoomId, tags });
        resultEl.textContent = 'World created!';
      }
      hideWorldForm();
      await refreshWorlds();
    } catch (e) {
      resultEl.textContent = `Error: ${e.message}`;
      resultEl.className = 'world-action-result error';
    }
  }

  async function handleDeleteWorld() {
    if (!state.selectedWorldId || !ensureAdmin()) return;
    const worldName = state.selectedWorld?.name || state.selectedWorldId;
    if (!confirm(`Delete world "${worldName}"? This cannot be undone.`)) return;

    try {
      await API.deleteWorld(state.selectedWorldId);
      state.selectedWorldId = '';
      state.selectedWorld = null;
      localStorage.removeItem('gae.admin.world');
      UI.renderWorldDetail(null);
      UI.renderPortalList(null);
      await refreshWorlds();
    } catch (e) {
      alert(`Cannot delete: ${e.message}`);
    }
  }

  // ── Portal handlers ──

  function showCreatePortalForm() {
    const wrapper = UI.$('portal-form-wrapper');
    if (!wrapper) return;
    wrapper.classList.remove('hidden');
    UI.$('portal-form-title').textContent = 'Create Portal';
    UI.$('portal-form-submit').textContent = 'Create Portal';
    UI.$('portal-form-id').value = '';
    UI.$('portal-form-source-room').value = '';
    UI.$('portal-form-dest-world').value = '';
    UI.$('portal-form-dest-room').value = '';
    UI.$('portal-form-desc').value = '';
    UI.$('portal-form-hint').value = '';
    UI.$('portal-form-min-level').value = '';
    UI.$('portal-form-admin-only').checked = false;
    UI.$('portal-form-result').textContent = '';
  }

  function hidePortalForm() {
    UI.$('portal-form-wrapper')?.classList.add('hidden');
  }

  function handlePortalListClick(event) {
    const editBtn = event.target.closest('[data-portal-edit]');
    if (editBtn) {
      const portalId = editBtn.dataset.portalEdit;
      const portal = (state.selectedWorld?.portals || []).find(p => p.id === portalId);
      if (portal) showEditPortalForm(portal);
      return;
    }
    const delBtn = event.target.closest('[data-portal-delete]');
    if (delBtn) {
      const portalId = delBtn.dataset.portalDelete;
      void handleDeletePortal(portalId);
    }
  }

  function showEditPortalForm(portal) {
    const wrapper = UI.$('portal-form-wrapper');
    if (!wrapper) return;
    wrapper.classList.remove('hidden');
    UI.$('portal-form-title').textContent = 'Edit Portal';
    UI.$('portal-form-submit').textContent = 'Save Portal';
    UI.$('portal-form-id').value = portal.id;
    UI.$('portal-form-source-room').value = portal.sourceRoomId || '';
    UI.$('portal-form-dest-world').value = portal.destinationWorldId || '';
    UI.$('portal-form-dest-room').value = portal.destinationRoomId || '';
    UI.$('portal-form-desc').value = portal.description || '';
    UI.$('portal-form-hint').value = portal.narratorHint || '';
    UI.$('portal-form-min-level').value = portal.minLevel || '';
    UI.$('portal-form-admin-only').checked = portal.isAdminOnly || false;
    UI.$('portal-form-result').textContent = '';
  }

  async function handlePortalFormSubmit(event) {
    event.preventDefault();
    if (!state.selectedWorldId || !ensureAdmin()) return;
    const resultEl = UI.$('portal-form-result');
    const editId = UI.$('portal-form-id').value;
    const data = {
      sourceRoomId: UI.$('portal-form-source-room').value.trim(),
      destinationWorldId: UI.$('portal-form-dest-world').value,
      destinationRoomId: UI.$('portal-form-dest-room').value.trim() || null,
      description: UI.$('portal-form-desc').value.trim() || null,
      narratorHint: UI.$('portal-form-hint').value.trim() || null,
      minLevel: parseInt(UI.$('portal-form-min-level').value) || null,
      isAdminOnly: UI.$('portal-form-admin-only').checked
    };

    resultEl.textContent = editId ? 'Saving...' : 'Creating...';
    resultEl.className = 'world-action-result';

    try {
      if (editId) {
        await API.updatePortal(state.selectedWorldId, editId, data);
        resultEl.textContent = 'Portal updated!';
      } else {
        await API.createPortal(state.selectedWorldId, data);
        resultEl.textContent = 'Portal created!';
      }
      hidePortalForm();
      await selectWorld(state.selectedWorldId);
    } catch (e) {
      resultEl.textContent = `Error: ${e.message}`;
      resultEl.className = 'world-action-result error';
    }
  }

  async function handleDeletePortal(portalId) {
    if (!state.selectedWorldId || !ensureAdmin()) return;
    if (!confirm('Delete this portal?')) return;
    try {
      await API.deletePortal(state.selectedWorldId, portalId);
      await selectWorld(state.selectedWorldId);
    } catch (e) {
      alert(`Error: ${e.message}`);
    }
  }

  // ── Realm Transfer ──

  async function handleRealmTransfer() {
    if (!ensureAdmin()) return;
    const resultEl = UI.$('realm-transfer-result');
    const playerId = UI.$('transfer-player-select').value;
    const destWorldId = UI.$('transfer-world-select').value;

    if (!playerId || !destWorldId) {
      resultEl.textContent = 'Select both a player and destination world.';
      resultEl.className = 'world-action-result error';
      return;
    }

    if (!confirm(`Transfer this player to another world? This triggers stat translation.`)) return;

    resultEl.textContent = 'Transferring...';
    resultEl.className = 'world-action-result';

    try {
      await API.transferPlayerToWorld(playerId, destWorldId);
      resultEl.textContent = 'Transfer complete!';
      resultEl.className = 'world-action-result';
      await Promise.all([refreshPlayers(), refreshWorlds()]);
    } catch (e) {
      resultEl.textContent = `Error: ${e.message}`;
      resultEl.className = 'world-action-result error';
    }
  }

  // ── World Actions handlers ──

  async function handleWarpToSpawn() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    const resultEl = UI.$('warp-result');
    const playerId = UI.$('warp-player-select').value;
    if (!playerId) { resultEl.textContent = 'Select a player.'; resultEl.className = 'world-action-result error'; return; }
    resultEl.textContent = 'Warping...'; resultEl.className = 'world-action-result';
    try {
      await API.teleportPlayer({ playerId, roomId: 'spawn' });
      resultEl.textContent = 'Player warped to spawn!'; resultEl.className = 'world-action-result';
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  async function handleWarpAllToSpawn() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    if (!confirm('Warp ALL players back to spawn? This will interrupt any active encounters.')) return;
    const resultEl = UI.$('warp-all-result');
    resultEl.textContent = 'Warping all players...'; resultEl.className = 'world-action-result';
    try {
      const players = await API.getPlayers();
      let count = 0;
      for (const p of players) {
        await API.teleportPlayer({ playerId: p.id, roomId: 'spawn' });
        count++;
      }
      resultEl.textContent = `Warped ${count} player(s) to spawn.`; resultEl.className = 'world-action-result';
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  async function handleReseedWorld() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    if (!confirm('Re-seed all rooms from lore YAML? Existing room data will be overwritten.')) return;
    const resultEl = UI.$('reseed-result');
    resultEl.textContent = 'Re-seeding...'; resultEl.className = 'world-action-result';
    try {
      await API.resetWorld(true); // keep players, just reset rooms
      resultEl.textContent = 'World re-seeded! Rooms restored from lore.'; resultEl.className = 'world-action-result';
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  async function handleSeedDemoWorld() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    const replace = UI.$('seed-demo-replace')?.checked || false;
    const resultEl = UI.$('seed-demo-result');
    resultEl.textContent = 'Seeding demo characters...'; resultEl.className = 'world-action-result';
    try {
      const result = await API.seedDemoCharacters(replace);
      resultEl.textContent = `Seeded ${result.created ?? 0} demo character(s).`; resultEl.className = 'world-action-result';
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  async function handleResetWorld() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    const keepPlayers = UI.$('reset-keep-players')?.checked ?? true;
    const warning = keepPlayers
      ? 'Reset world? All rooms will be re-seeded. Players will be kept but moved to spawn with full HP/MP.'
      : 'FULL RESET — All rooms AND all players will be DELETED. This cannot be undone!';
    if (!confirm(warning)) return;
    if (!keepPlayers && !confirm('Are you REALLY sure? All player data will be permanently lost.')) return;
    const resultEl = UI.$('reset-world-result');
    resultEl.textContent = 'Resetting world...'; resultEl.className = 'world-action-result';
    try {
      await API.resetWorld(keepPlayers);
      resultEl.textContent = keepPlayers ? 'World reset! Players preserved at spawn.' : 'Full world reset complete.';
      resultEl.className = 'world-action-result';
      await refreshAll();
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  async function handleExportWorld() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    const worldId = state.selectedWorldId;
    if (!worldId) { alert('Select a world first.'); return; }
    const resultEl = UI.$('export-world-result');
    resultEl.textContent = 'Starting download...'; resultEl.className = 'world-action-result';
    try {
      API.exportWorldYaml(worldId);
      resultEl.textContent = `Download started: world-${worldId}.yaml`; resultEl.className = 'world-action-result';
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
  }

  function handleImportWorldClick() {
    if (!ensureAuthenticated() || !ensureAdmin()) return;
    UI.$('import-world-file').value = '';
    UI.$('import-world-file').click();
  }

  async function handleImportWorldFile(e) {
    const file = e.target.files?.[0];
    if (!file) return;
    const resultEl = UI.$('import-world-result');
    resultEl.textContent = 'Importing...'; resultEl.className = 'world-action-result';
    try {
      const result = await API.importWorldYaml(file);
      resultEl.textContent = result.summary; resultEl.className = 'world-action-result';
      await refreshWorlds();
    } catch (e) { resultEl.textContent = `Error: ${e.message}`; resultEl.className = 'world-action-result error'; }
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
    }, 120000); // Poll every 2 minutes instead of 15s to reduce LM Studio stuttering
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