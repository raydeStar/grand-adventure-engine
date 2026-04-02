const MODE_DESCRIPTIONS = {
  user: 'Play through the same commands Discord users will send.',
  admin: 'Inspect state, stage fixtures, and run manual smoke tests against any character.'
};

const KNOWN_ABILITY_KEYS = ['str', 'dex', 'con', 'int', 'wis', 'cha', 'luck'];
const RESERVED_PLAYER_KEYS = new Set([
  'id',
  'name',
  'race',
  'class',
  'backstory',
  'currentRoomId',
  'hp',
  'maxHp',
  'mp',
  'maxMp',
  'gold',
  'xp',
  'level',
  'defense',
  'equipment',
  'inventory',
  'statusEffects',
  'createdAt',
  'lastActiveAt'
]);

const PLAYER_SELECT_IDS = [
  'workflow-player-select',
  'admin-player-select',
  'resource-player-select',
  'teleport-player-select',
  'item-player-select',
  'status-player-select'
];

const UI = {
  _roomContext: null,

  $(id) {
    return document.getElementById(id);
  },

  showPortal(show) {
    this.$('portal-overlay').classList.toggle('hidden', !show);
  },

  showDashboard(show) {
    this.$('dashboard').classList.toggle('hidden', !show);
  },

  showCreateForm(show) {
    const form = this.$('create-form');
    form.classList.toggle('hidden', !show);
    if (show) {
      this.$('char-name').focus();
    }
  },

  setMode(mode, session = null) {
    const effectiveMode = mode === 'admin' && !session?.isAdmin ? 'user' : mode;

    document.querySelectorAll('[data-mode-button]').forEach((button) => {
      button.classList.toggle('active', button.dataset.modeButton === effectiveMode);
    });

    document.querySelectorAll('[data-role-choice]').forEach((button) => {
      button.classList.toggle('active', button.dataset.roleChoice === effectiveMode);
    });

    this.$('workspace-user').classList.toggle('hidden', effectiveMode !== 'user');
    this.$('workspace-admin').classList.toggle('hidden', effectiveMode !== 'admin');
    this.$('mode-description').textContent = MODE_DESCRIPTIONS[effectiveMode] || '';
    return effectiveMode;
  },

  setSession(session, loginHints = []) {
    const canAdmin = !!session?.isAdmin;
    const signedIn = !!session;

    const summary = this.$('session-summary');
    summary.textContent = session
      ? `${session.displayName} signed in as ${session.username}. ${canAdmin ? 'Admin workflows and mutations are unlocked.' : 'Admin console remains protected.'}`
      : 'Sign in to unlock gameplay, manual test controls, and the protected API.';

    const badge = this.$('session-badge');
    badge.textContent = session ? `${session.username} | ${session.role}` : 'Signed out';
    badge.classList.toggle('admin', canAdmin);

    this.$('auth-form').classList.toggle('hidden', signedIn);
    this.$('btn-logout-header').classList.toggle('hidden', !signedIn);
    this.$('btn-logout-portal').classList.toggle('hidden', !signedIn);
    this.$('btn-fill-user').classList.toggle('hidden', signedIn);
    this.$('btn-fill-admin').classList.toggle('hidden', signedIn);

    this.$('btn-new-char').disabled = !signedIn;

    const roleAdmin = document.querySelector('[data-role-choice="admin"]');
    const modeAdmin = document.querySelector('[data-mode-button="admin"]');
    if (roleAdmin) roleAdmin.classList.toggle('hidden', !canAdmin);
    if (modeAdmin) modeAdmin.classList.toggle('hidden', !canAdmin);

    this.$('btn-open-admin').classList.toggle('hidden', !canAdmin);
    this.$('btn-seed-demo').classList.toggle('hidden', !canAdmin);
    this.$('btn-seed-demo-admin').classList.toggle('hidden', !canAdmin);

    if (!signedIn) {
      this.showCreateForm(false);
    }

    this.renderLoginHints(loginHints);
    this.setUserCommandState(false, signedIn);
  },

  renderLoginHints(hints) {
    const container = this.$('login-hints');
    if (!hints.length) {
      container.innerHTML = '<div class="login-hint">No login hints available.</div>';
      return;
    }

    container.innerHTML = hints.map((hint) => `
      <div class="login-hint">
        <span class="hint-role">${this.esc(hint.displayName)}</span>
        <span class="hint-user">${this.esc(hint.username)}</span>
      </div>
    `).join('');
  },

  setPortalMessage(message, tone = 'success') {
    const element = this.$('portal-message');
    if (!message) {
      element.textContent = '';
      element.className = 'inline-message hidden';
      return;
    }

    element.textContent = message;
    element.className = `inline-message ${tone}`;
  },

  setAuthMessage(message, tone = 'info') {
    const element = this.$('auth-message');
    if (!message) {
      element.textContent = '';
      element.className = 'inline-message hidden';
      return;
    }

    element.textContent = message;
    element.className = `inline-message ${tone}`;
  },

  setConnectionStatus(status) {
    const dot = this.$('connection-dot');
    const text = this.$('connection-status');
    const label = {
      connecting: 'Connecting...',
      connected: 'Realtime active',
      reconnecting: 'Reconnecting...',
      polling: 'Polling fallback',
      disconnected: 'Disconnected'
    }[status] || 'Unknown';

    dot.className = 'status-dot';
    if (status === 'connected') {
      dot.classList.add('connected');
    } else if (status === 'polling' || status === 'reconnecting' || status === 'connecting') {
      dot.classList.add('polling');
    } else {
      dot.classList.add('disconnected');
    }

    text.textContent = label;
  },

  setUserCommandState(enabled, isAuthenticated = true) {
    this.$('command-input').disabled = !enabled;
    this.$('command-submit').disabled = !enabled;
    this.$('command-input').placeholder = enabled
      ? 'Enter a command or natural-language probe such as look, go north, or what can I do here?'
      : isAuthenticated
        ? 'Choose a character to start playing'
        : 'Sign in to send commands';
  },

  renderPortalPlayers(players, currentPlayerId, session) {
    const container = this.$('existing-players');
    if (!session) {
      container.innerHTML = '<div class="empty-state">Sign in to view characters and open a protected play session.</div>';
      return;
    }

    if (!players.length) {
      container.innerHTML = '<div class="empty-state">No characters yet. Create one or seed the demo user and admin personas.</div>';
      return;
    }

    container.innerHTML = players.map((player) => {
      const isActive = player.id === currentPlayerId;
      return `
        <div class="player-card">
          <div class="player-card-main">
            <div>
              <div class="player-card-name">${this.esc(player.name)}</div>
              <div class="player-card-meta">${this.esc(player.id)} | Lv.${player.level} ${this.esc(player.race)} ${this.esc(player.class)} | Room ${this.esc(player.currentRoomId)}</div>
            </div>
            ${isActive ? '<span class="role-chip active">Active</span>' : ''}
          </div>
          <div class="player-card-actions">
            <button class="btn btn-primary btn-sm" data-portal-action="user" data-player-id="${this.esc(player.id)}" type="button">User Flow</button>
            ${session.isAdmin ? `<button class="btn btn-secondary btn-sm" data-portal-action="admin" data-player-id="${this.esc(player.id)}" type="button">Admin Console</button>` : ''}
          </div>
        </div>
      `;
    }).join('');
  },

  renderRoom(room) {
    this._roomContext = room || null;

    if (!room) {
      this.$('room-name').textContent = 'No active room';
      this.$('room-desc').textContent = 'Select a character to load the room view.';
      this.$('room-ascii').textContent = '';
      this.$('room-ascii').classList.add('hidden');
      this.$('room-summary').innerHTML = '';
      RoomMap.clear(this.$('room-map-container'));
      this.updateExitChips(null);
      this.updatePrompt(null);
      return;
    }

    this.$('room-name').textContent = room.name || 'Unknown location';
    this.$('room-desc').textContent = room.description || '';

    const ascii = this.$('room-ascii');
    ascii.textContent = room.asciiArt || '';
    ascii.classList.toggle('hidden', !room.asciiArt);

    RoomMap.render(this.$('room-map-container'), room);

    const parts = [];
    const exits = Object.keys(room.exits || {});
    if (exits.length) {
      parts.push(exits.map((d) => `\u2192 ${this.esc(d)}`).join('\u2003'));
    }
    const npcSummary = this._summarizeCounts(room.npcs || []);
    if (npcSummary) parts.push(`NPCs: ${npcSummary}`);
    const itemSummary = this._summarizeCounts(room.items || []);
    if (itemSummary) parts.push(`Items: ${itemSummary}`);

    this.$('room-summary').innerHTML = parts.length
      ? parts.map((p) => `<span class="room-summary-segment">${p}</span>`).join('')
      : '';

    this.updateExitChips(room);
    this.updatePrompt(room);
  },

  renderNoActivePlayer(isAuthenticated = true) {
    this.$('header-player').textContent = 'No active character';
    this.$('char-title').textContent = 'Character';
    this.$('char-meta').textContent = isAuthenticated
      ? 'Choose a character from the portal or keep working in the admin console.'
      : 'Sign in, then choose a character from the portal or seed a demo actor.';
    this.setBar('hp-bar', 'hp-text', 0, 0);
    this.setBar('mp-bar', 'mp-text', 0, 0);
    this.setBar('xp-bar', 'xp-text', 0, 100);
    this.$('stats-grid').innerHTML = '<div class="empty-state">No stats loaded.</div>';
    this.$('character-details').innerHTML = '<div class="empty-state">Dynamic character details will appear here.</div>';
    this.$('char-gold').textContent = 'Gold 0';
    this.$('char-level').textContent = 'Level 1';
    this.$('char-defense').textContent = 'Defense 10';
    this.$('equipment-slots').innerHTML = '<div class="empty-state">No equipment loaded.</div>';
    this.$('inventory-list').innerHTML = '<div class="inv-empty">Inventory unavailable until a character is selected.</div>';
    this.$('status-effects').innerHTML = '<span class="no-effects">No active effects.</span>';
    this.renderPayloads(null, null);
    this.renderRoom(null);
    this.renderStatBar(null);
    this.$('story-log').innerHTML = '<div class="empty-state">Story entries will appear once a character starts acting.</div>';
    this.setUserCommandState(false, isAuthenticated);
  },

  renderPlayer(player) {
    if (!player) {
      this.renderNoActivePlayer(true);
      return;
    }

    this.$('header-player').textContent = `${player.name} | Lv.${player.level} ${player.race} ${player.class}`;
    this.$('char-title').textContent = player.name;
    this.$('char-meta').textContent = `${player.race} ${player.class} | Room ${player.currentRoomId}`;
    this.setUserCommandState(true, true);

    this.setBar('hp-bar', 'hp-text', player.hp, player.maxHp);
    this.setBar('mp-bar', 'mp-text', player.mp, player.maxMp);

    const xpGoal = Math.max(100, player.level * 100);
    this.setBar('xp-bar', 'xp-text', player.xp, xpGoal);

    // Update block-char stat bar
    this.renderStatBar(player);

    const statEntries = this.getStatEntries(player);
    this.$('stats-grid').innerHTML = statEntries.length
      ? statEntries.map((entry) => {
        const modifierText = entry.modifier === null || entry.modifier === undefined
          ? ''
          : (entry.modifier >= 0 ? `+${entry.modifier}` : `${entry.modifier}`);
        return `
          <div class="stat-box">
            <div class="stat-label">${this.esc(entry.label)}</div>
            <div class="stat-value">${this.esc(entry.value)}</div>
            <div class="stat-mod">${modifierText || '&nbsp;'}</div>
          </div>
        `;
      }).join('')
      : '<div class="empty-state">No stat fields detected.</div>';

    const detailEntries = this.getDetailEntries(player);
    this.$('character-details').innerHTML = detailEntries.length
      ? detailEntries.map((entry) => `
          <div class="detail-card">
            <div class="detail-label">${this.esc(entry.label)}</div>
            <div class="detail-value">${this.esc(entry.value)}</div>
          </div>
        `).join('')
      : '<div class="empty-state">No extra character fields detected.</div>';

    this.$('char-gold').textContent = `Gold ${player.gold}`;
    this.$('char-level').textContent = `Level ${player.level}`;
    this.$('char-defense').textContent = `Defense ${player.defense ?? 10}`;

    const equipment = player.equipment || {};
    const slots = Object.entries(equipment);
    this.$('equipment-slots').innerHTML = slots.length
      ? slots.map(([name, item]) => `
      <div class="equip-slot">
        <div class="slot-name">${this.esc(this.humanizeKey(name))}</div>
        <div class="${item ? 'slot-item' : 'slot-empty'}">${this.esc(item ? this.summarizeEntity(item) : 'Empty')}</div>
      </div>
    `).join('')
      : '<div class="empty-state">No equipment slots populated.</div>';

    const inventory = player.inventory || [];
    this.$('inventory-list').innerHTML = inventory.length
      ? inventory.map((item) => `
          <div class="inv-item">
            <strong>${this.esc(this.summarizeEntity(item))}</strong>
            <span class="player-card-meta">${this.esc(this.describeSupplementaryFields(item))}</span>
          </div>
        `).join('')
      : '<div class="inv-empty">Inventory empty.</div>';

    const effects = player.statusEffects || [];
    this.$('status-effects').innerHTML = effects.length
      ? effects.map((effect) => `<span class="status-tag">${this.esc(this.summarizeEntity(effect))}</span>`).join('')
      : '<span class="no-effects">No active effects.</span>';
  },

  renderPayloads(player, room) {
    const playerPayload = this.$('player-payload');
    const roomPayload = this.$('room-payload');
    if (playerPayload) {
      playerPayload.textContent = player ? JSON.stringify(player, null, 2) : 'Select a character to inspect live payloads.';
    }
    if (roomPayload) {
      roomPayload.textContent = room ? JSON.stringify(room, null, 2) : 'Select a character to inspect the current room payload.';
    }
  },

  setBar(fillId, textId, current, max) {
    const safeMax = Math.max(0, max || 0);
    const pct = safeMax > 0 ? Math.max(0, Math.min(100, (current / safeMax) * 100)) : 0;
    this.$(fillId).style.width = `${pct}%`;
    this.$(textId).textContent = `${current} / ${safeMax}`;
  },

  _lastStoryCount: 0,

  renderStoryLog(entries) {
    const log = this.$('story-log');
    if (!entries.length) {
      log.innerHTML = '<div class="empty-state">No story recorded yet.</div>';
      this._lastStoryCount = 0;
      return;
    }

    // Skip rebuild when entry count has not changed (prevents re-animation)
    if (this._lastStoryCount === entries.length && log.querySelector('.story-entry')) {
      return;
    }

    this._lastStoryCount = entries.length;
    log.innerHTML = '';
    [...entries].reverse().forEach((entry) => this._appendStoryNode(entry, undefined, false));
    log.scrollTop = log.scrollHeight;
  },

  appendStoryEntry(entry, tone) {
    const log = this.$('story-log');
    if (log.querySelector('.empty-state')) {
      log.innerHTML = '';
    }

    this._appendStoryNode(entry, tone, true);
  },

  _appendStoryNode(entry, tone, animate) {
    const log = this.$('story-log');
    const node = document.createElement('div');
    const stateTone = tone || (entry.success === false ? 'failure' : entry.success === true ? 'success' : 'info');
    node.className = `story-entry ${stateTone === 'info' ? 'command' : stateTone}`;

    if (animate) {
      node.classList.add('fade-slide-in');
    }

    let html = '';

    const cleanedNarration = this._stripRoomMetadata(entry.narration);
    const cleanedMechanicalSummary = this._stripRoomMetadata(entry.mechanicalSummary);

    if (cleanedNarration) {
      html += `<div class="story-narration">${this.esc(cleanedNarration)}</div>`;
    }

    if (cleanedMechanicalSummary) {
      html += this._formatMechanicalParsed(cleanedMechanicalSummary);
    }

    if (entry.diceRolls?.length) {
      html += `<div class="story-dice">${entry.diceRolls.map((roll) => this.esc(`[${roll.purpose || roll.expression || 'Roll'}: ${roll.total ?? '?'}]`)).join(' ')}</div>`;
    }

    if (!html) {
      return;
    }

    node.innerHTML = html;

    log.appendChild(node);

    while (log.children.length > 50) {
      log.removeChild(log.firstChild);
    }

    log.scrollTop = log.scrollHeight;
  },

  renderPlayersList(players, currentPlayerId, session) {
    const container = this.$('players-list');
    if (!session) {
      container.innerHTML = '<div class="inv-empty">Sign in to inspect the current party.</div>';
      return;
    }

    if (!players.length) {
      container.innerHTML = '<div class="inv-empty">No adventurers available.</div>';
      return;
    }

    container.innerHTML = players.map((player) => `
      <div class="player-row">
        <strong>${this.esc(player.name)}${player.id === currentPlayerId ? ' (active)' : ''}</strong>
        <span class="player-card-meta">Lv.${player.level} ${this.esc(player.race)} ${this.esc(player.class)} | Room ${this.esc(player.currentRoomId)}</span>
      </div>
    `).join('');
  },

  renderHealth(checks) {
    const items = [
      ['health', 'Core API'],
      ['health/wiki', 'Wiki.js'],
      ['health/narrator', 'Narrator']
    ];

    const html = items.map(([key, label]) => {
      const check = checks?.[key];
      const dotClass = check?.ok ? 'ok' : check?.status === 'degraded' ? 'degraded' : 'down';
      return `
        <div class="health-item">
          <span class="health-dot ${dotClass}"></span>
          <div>
            <div class="health-label">${label}</div>
            <div class="health-note">${this.esc(check?.status || 'unknown')}</div>
          </div>
        </div>
      `;
    }).join('');

    ['system-health', 'admin-health'].forEach((id) => {
      const target = this.$(id);
      if (target) {
        target.innerHTML = html;
      }
    });
  },

  renderSummary(summary, connectionMode, session) {
    const container = this.$('summary-cards');
    if (!session?.isAdmin) {
      container.innerHTML = '<div class="empty-state">Admin login required to inspect aggregate dashboard state.</div>';
      return;
    }

    if (!summary) {
      container.innerHTML = '<div class="empty-state">Summary unavailable.</div>';
      return;
    }

    const cards = [
      ['Players', summary.playerCount],
      ['Active (30m)', summary.activePlayerCount],
      ['Rooms', summary.roomCount],
      ['Discovered', summary.discoveredRoomCount],
      ['Story Entries', summary.storyEntryCount],
      ['Transport', connectionMode]
    ];

    container.innerHTML = cards.map(([label, value]) => `
      <div class="summary-card">
        <div class="summary-label">${this.esc(String(label))}</div>
        <div class="summary-metric">
          <span class="summary-value">${this.esc(String(value))}</span>
        </div>
      </div>
    `).join('');
  },

  renderSelectOptions(players, preferredPlayerId) {
    PLAYER_SELECT_IDS.forEach((id) => {
      const select = this.$(id);
      if (!select) return;

      const previousValue = select.value;
      if (!players.length) {
        select.innerHTML = '<option value="">No players available</option>';
        return;
      }

      select.innerHTML = players.map((player) => `
        <option value="${this.esc(player.id)}">${this.esc(player.name)} (${this.esc(player.id)})</option>
      `).join('');

      const nextValue = players.some((player) => player.id === previousValue)
        ? previousValue
        : players.some((player) => player.id === preferredPlayerId)
          ? preferredPlayerId
          : players[0].id;
      select.value = nextValue;
    });
  },

  renderAdminPlayers(players, currentPlayerId, session) {
    const container = this.$('admin-players-table');
    if (!session?.isAdmin) {
      container.innerHTML = '<div class="empty-state">Admin login required.</div>';
      return;
    }

    if (!players.length) {
      container.innerHTML = '<div class="empty-state">No players seeded yet.</div>';
      return;
    }

    container.innerHTML = players.map((player) => `
      <div class="registry-row">
        <div class="registry-meta">
          <div>
            <div class="registry-name">${this.esc(player.name)}${player.id === currentPlayerId ? ' (active)' : ''}</div>
            <div class="registry-subtext">${this.esc(player.id)} | Lv.${player.level} ${this.esc(player.race)} ${this.esc(player.class)} | Room ${this.esc(player.currentRoomId)}</div>
          </div>
        </div>
        <div class="registry-actions">
          <button class="btn btn-primary btn-sm" data-admin-action="user" data-player-id="${this.esc(player.id)}" type="button">Play</button>
          <button class="btn btn-secondary btn-sm" data-admin-action="admin" data-player-id="${this.esc(player.id)}" type="button">Admin View</button>
          <button class="btn btn-secondary btn-sm" data-admin-action="copy-id" data-player-id="${this.esc(player.id)}" type="button">Copy Id</button>
        </div>
      </div>
    `).join('');
  },

  renderRoomCatalogue(rooms) {
    const container = this.$('admin-rooms-list');
    if (!rooms.length) {
      container.innerHTML = '<div class="empty-state">No rooms available.</div>';
      return;
    }

    container.innerHTML = rooms.map((room) => `
      <div class="room-card">
        <div class="room-card-top">
          <span class="room-card-title">${this.esc(room.name)}</span>
          <span class="role-chip ${room.isDiscovered ? 'active' : ''}">${room.isDiscovered ? 'Discovered' : 'Seeded only'}</span>
        </div>
        <div class="room-card-meta">${this.esc(room.id)} | Exits ${room.exitCount ?? Object.keys(room.exits || {}).length} | NPCs ${room.npcCount ?? (room.npcs || []).length} | Items ${room.itemCount ?? (room.items || []).length}</div>
        <div class="room-card-description">${this.esc(room.description || 'No description available.')}</div>
      </div>
    `).join('');
  },

  appendActivity(containerId, text, tone = 'info') {
    const container = this.$(containerId);
    if (!container) return;

    const node = document.createElement('div');
    node.className = `activity-item ${tone}`;
    node.innerHTML = `
      <div class="activity-meta">${new Date().toLocaleTimeString()}</div>
      <div class="activity-text">${this.esc(text)}</div>
    `;
    container.appendChild(node);
    container.scrollTop = container.scrollHeight;
  },

  clearActivity(containerId) {
    const container = this.$(containerId);
    if (container) {
      container.innerHTML = '';
    }
  },

  /* ── Block-char stat bar ── */

  renderStatBar(player) {
    const bar = this.$('stat-bar');
    if (!bar) return;

    if (!player) {
      bar.innerHTML =
        '<span class="stat-label">HP</span> <span class="stat-hp stat-empty">\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591</span> <span class="stat-hp">0/0</span>' +
        '<span class="stat-sep"> \u2502 </span>' +
        '<span class="stat-label">MP</span> <span class="stat-mp stat-empty">\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591</span> <span class="stat-mp">0/0</span>' +
        '<span class="stat-sep"> \u2502 </span>' +
        '<span class="stat-label">XP</span> <span class="stat-xp stat-empty">\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591</span> <span class="stat-xp">0/100</span>' +
        '<span class="stat-sep"> \u2502 </span>' +
        '<span class="stat-gold">Gold:0</span>';
      return;
    }

    const hp = this._blockBar(player.hp, player.maxHp, 'stat-hp');
    const mp = this._blockBar(player.mp, player.maxMp, 'stat-mp');
    const xpGoal = Math.max(100, player.level * 100);
    const xp = this._blockBar(player.xp, xpGoal, 'stat-xp');
    const sep = '<span class="stat-sep"> \u2502 </span>';

    bar.innerHTML =
      `<span class="stat-label">HP</span> ${hp} <span class="stat-hp">${player.hp}/${player.maxHp}</span>${sep}` +
      `<span class="stat-label">MP</span> ${mp} <span class="stat-mp">${player.mp}/${player.maxMp}</span>${sep}` +
      `<span class="stat-label">XP</span> ${xp} <span class="stat-xp">${player.xp}/${xpGoal}</span>${sep}` +
      `<span class="stat-gold">Gold:${player.gold}</span>${sep}` +
      `<span class="stat-level">Lv.${player.level}</span>`;
  },

  _blockBar(current, max, cssClass, width = 10) {
    const safeMax = Math.max(1, max || 1);
    const filled = Math.round((Math.max(0, current) / safeMax) * width);
    const empty = width - filled;
    const filledStr = '\u2588'.repeat(filled);
    const emptyStr = '\u2591'.repeat(empty);
    return `<span class="stat-bar-track" style="width:${width}ch"><span class="${cssClass} stat-filled">${filledStr}</span><span class="${cssClass} stat-empty">${emptyStr}</span></span>`;
  },

  /* ── Theme toggle ── */

  toggleTheme() {
    const current = document.body.getAttribute('data-theme') || 'green';
    const next = current === 'green' ? 'amber' : 'green';
    document.body.setAttribute('data-theme', next);
    localStorage.setItem('gae.theme', next);
  },

  restoreTheme() {
    const saved = localStorage.getItem('gae.theme');
    if (saved) {
      document.body.setAttribute('data-theme', saved);
    }
  },

  /* ── Info drawer toggle ── */

  toggleInfoDrawer() {
    const drawer = this.$('info-drawer');
    if (drawer) {
      drawer.classList.toggle('collapsed');
      localStorage.setItem('gae.info-drawer', drawer.classList.contains('collapsed') ? 'collapsed' : 'open');
    }
  },

  restoreInfoDrawer() {
    const saved = localStorage.getItem('gae.info-drawer');
    const drawer = this.$('info-drawer');
    if (drawer && saved === 'collapsed') {
      drawer.classList.add('collapsed');
    }
  },

  getStatEntries(player) {
    const preferred = KNOWN_ABILITY_KEYS
      .filter((key) => this.isScalar(player[key]))
      .map((key) => ({
        label: key.toUpperCase(),
        value: player[key],
        modifier: typeof player[key] === 'number' ? Math.trunc((player[key] - 10) / 2) : null
      }));

    const extras = Object.entries(player)
      .filter(([key, value]) => !RESERVED_PLAYER_KEYS.has(key) && !KNOWN_ABILITY_KEYS.includes(key) && typeof value === 'number')
      .map(([key, value]) => ({
        label: this.humanizeKey(key),
        value,
        modifier: null
      }));

    return [...preferred, ...extras];
  },

  getDetailEntries(player) {
    return Object.entries(player)
      .filter(([key, value]) => !RESERVED_PLAYER_KEYS.has(key) && !KNOWN_ABILITY_KEYS.includes(key) && value !== null)
      .map(([key, value]) => ({
        label: this.humanizeKey(key),
        value: this.summarizeEntity(value)
      }));
  },

  describeSupplementaryFields(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      return '';
    }

    const extras = Object.entries(value)
      .filter(([key, field]) => !['id', 'name', 'title', 'label', 'description'].includes(key) && this.isScalar(field))
      .slice(0, 3)
      .map(([key, field]) => `${this.humanizeKey(key)}: ${field}`);

    return extras.join(' | ');
  },

  summarizeEntity(value) {
    if (value === null || value === undefined) return 'Unknown';
    if (this.isScalar(value)) return String(value);
    if (Array.isArray(value)) {
      return value.length ? this._summarizeCounts(value) : 'Empty list';
    }

    if (typeof value === 'object') {
      const primary = value.name || value.title || value.label || value.id;
      if (primary) {
        return primary;
      }

      const preview = Object.entries(value)
        .filter(([, field]) => this.isScalar(field))
        .slice(0, 3)
        .map(([key, field]) => `${this.humanizeKey(key)}: ${field}`)
        .join(', ');
      return preview || 'Object';
    }

    return String(value);
  },

  isScalar(value) {
    return ['string', 'number', 'boolean'].includes(typeof value);
  },

  humanizeKey(key) {
    return String(key)
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/[_-]+/g, ' ')
      .replace(/\b\w/g, (char) => char.toUpperCase());
  },

  esc(value) {
    if (value === null || value === undefined) return '';
    const div = document.createElement('div');
    div.textContent = String(value);
    return div.innerHTML;
  },

  _summarizeCounts(entities) {
    if (!entities.length) return '';
    const counts = {};
    for (const ent of entities) {
      const name = typeof ent === 'string' ? ent : (ent.name || ent.title || ent.label || 'Unknown');
      counts[name] = (counts[name] || 0) + (ent.quantity || 1);
    }
    return Object.entries(counts)
      .map(([name, count]) => count > 1 ? `${this.esc(name)} (x${count})` : this.esc(name))
      .join(', ');
  },

  updateExitChips(room) {
    const container = this.$('exit-chips');
    if (!container) return;
    const exits = room ? Object.keys(room.exits || {}) : [];
    container.innerHTML = exits.length
      ? exits.map((d) => `<button class="btn btn-secondary btn-sm exit-chip" data-room-exit="${this.esc(d)}" type="button">go ${this.esc(d)}</button>`).join('')
      : '';
  },

  updatePrompt(room) {
    const prompt = this.$('command-prompt');
    if (!prompt) return;
    prompt.innerHTML = room ? `${this.esc(room.name)} &gt;` : '&gt;';
  },

  _formatMechanicalParsed(text) {
    const cleaned = this._stripRoomMetadata(text);
    if (!cleaned) {
      return '';
    }

    const lines = cleaned.split('\n');
    const parts = [];

    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) continue;

      let safe = this.esc(trimmed);
      safe = safe.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
      safe = safe.replace(/`(.+?)`/g, '<code>$1</code>');
      parts.push(`<div class="story-mechanical">${safe}</div>`);
    }

    return parts.join('');
  },

  _stripRoomMetadata(text) {
    if (!text) {
      return '';
    }

    const room = this._roomContext;
    const roomName = room?.name ? this._normalizeRoomLine(room.name) : '';
    const roomDescription = room?.description ? this._normalizeRoomLine(room.description) : '';
    const lines = String(text).split('\n');
    const metadataIndexes = lines
      .map((line, index) => ({ line: this._normalizeRoomLine(line), index }))
      .filter(({ line }) => /^(Exits?|You see|Items?|NPCs?|Creatures?|Objects?|Nearby)\s*:/i.test(line))
      .map(({ index }) => index);
    const looksLikeRoomBlock = metadataIndexes.length >= 2 && metadataIndexes[0] <= 2;
    const filtered = [];

    for (let index = 0; index < lines.length; index += 1) {
      const line = lines[index];
      const trimmed = line.trim();
      const normalized = this._normalizeRoomLine(trimmed);

      if (!normalized) {
        continue;
      }

      if (looksLikeRoomBlock && index < metadataIndexes[0]) {
        continue;
      }

      if (/^(Exits?|You see|Items?|NPCs?|Creatures?|Objects?|Nearby)\s*:/i.test(normalized)) {
        continue;
      }

      if (roomName && normalized === roomName) {
        continue;
      }

      if (roomDescription && normalized === roomDescription) {
        continue;
      }

      filtered.push(line);
    }

    return filtered.join('\n').trim();
  },

  _normalizeRoomLine(text) {
    return String(text || '')
      .trim()
      .replace(/^\*\*(.*)\*\*$/, '$1')
      .replace(/\s+/g, ' ')
      .trim();
  },

  formatMechanical(text) {
    // Escape first, then convert **bold** markers and `code` backticks
    let safe = this.esc(text);
    safe = safe.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    safe = safe.replace(/`(.+?)`/g, '<code>$1</code>');
    return safe;
  }
};