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
  'stats',
  'interaction',
  'equipment',
  'inventory',
  'statusEffects',
  'createdAt',
  'lastActiveAt',
  'isAlive',
  'isConscious'
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
    const countEl = this.$('player-count');
    const searchInput = this.$('player-search');

    // Store full list for filtering
    this._portalPlayers = players;
    this._portalCurrentPlayerId = currentPlayerId;
    this._portalSession = session;

    if (!session) {
      container.innerHTML = '<div class="empty-state">Sign in to view characters and open a protected play session.</div>';
      if (countEl) countEl.textContent = '';
      return;
    }

    if (!players.length) {
      container.innerHTML = '<div class="empty-state">No characters yet. Create one or seed the demo user and admin personas.</div>';
      if (countEl) countEl.textContent = '0 characters';
      return;
    }

    // Wire up search (once)
    if (searchInput && !searchInput._wired) {
      searchInput._wired = true;
      searchInput.addEventListener('input', () => this._filterPortalPlayers());
    }

    this._filterPortalPlayers();
  },

  _filterPortalPlayers() {
    const container = this.$('existing-players');
    const countEl = this.$('player-count');
    const searchInput = this.$('player-search');
    const players = this._portalPlayers || [];
    const currentPlayerId = this._portalCurrentPlayerId || '';
    const session = this._portalSession;
    const query = (searchInput?.value || '').toLowerCase().trim();

    const filtered = query
      ? players.filter(p =>
          (p.name || '').toLowerCase().includes(query) ||
          (p.id || '').toLowerCase().includes(query) ||
          (p.race || '').toLowerCase().includes(query) ||
          (p.class || '').toLowerCase().includes(query) ||
          (p.currentRoomId || '').toLowerCase().includes(query))
      : players;

    if (countEl) {
      countEl.textContent = query
        ? `${filtered.length} / ${players.length}`
        : `${players.length} character${players.length !== 1 ? 's' : ''}`;
    }

    if (!filtered.length) {
      container.innerHTML = `<div class="empty-state">No characters matching "${this.esc(query)}".</div>`;
      return;
    }

    container.innerHTML = filtered.map((player) => {
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
            ${session?.isAdmin ? `<button class="btn btn-secondary btn-sm" data-portal-action="admin" data-player-id="${this.esc(player.id)}" type="button">Admin Console</button>` : ''}
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
      parts.push({
        label: 'Exits:',
        value: exits.map((direction) => `\u2192 ${this.esc(direction)}`).join(' \u2502 ')
      });
    }
    const npcs = room.npcs || [];
    const items = room.items || [];
    if (npcs.length) parts.push({ label: 'NPCs:', value: this._summarizeCounts(npcs) });
    if (items.length) parts.push({ label: 'Items:', value: this._summarizeCounts(items) });

    this.$('room-summary').innerHTML = parts.length
      ? parts.map((part) => `
          <div class="room-summary-line">
            <span class="room-summary-label">${this.esc(part.label)}</span>
            <span class="room-summary-value">${part.value}</span>
          </div>
        `).join('')
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
  _renderedActionIds: new Set(),

  renderStoryLog(entries) {
    const log = this.$('story-log');
    if (!entries.length) {
      log.innerHTML = '<div class="empty-state">No story recorded yet.</div>';
      this._lastStoryCount = 0;
      this._renderedActionIds.clear();
      return;
    }

    // Skip rebuild when entry count has not changed (prevents re-animation and flash)
    if (this._lastStoryCount === entries.length && log.querySelector('.story-entry')) {
      return;
    }

    this._lastStoryCount = entries.length;

    // Preserve any DOM nodes for entries we already appended via appendStoryEntry.
    // Collect the actionIds currently in the log so we can avoid duplicating them.
    const existingIds = new Set();
    log.querySelectorAll('[data-action-id]').forEach((node) => {
      existingIds.add(node.dataset.actionId);
    });

    // If the log already has entries that match the server data, skip the full rebuild
    // to avoid the flash. Only rebuild if we have nothing rendered yet.
    if (existingIds.size > 0) {
      // Reconcile: add any entries from the server that we don't already have
      const reversed = [...entries].reverse();
      for (const entry of reversed) {
        const id = entry.actionId || entry.id;
        if (id && existingIds.has(id)) continue;
        // This entry wasn't rendered locally — prepend it at the correct position
        // For simplicity, just skip the rebuild entirely when we already have content.
        // The local append path keeps the log accurate, and the count guard above
        // will prevent re-entry once counts align.
      }
      return;
    }

    log.innerHTML = '';
    this._renderedActionIds.clear();
    [...entries].reverse().forEach((entry) => {
      this._appendStoryNode(entry, undefined, false);
      const id = entry.actionId || entry.id;
      if (id) this._renderedActionIds.add(id);
    });
    log.scrollTop = log.scrollHeight;
  },

  appendStoryEntry(entry, tone) {
    const log = this.$('story-log');
    if (log.querySelector('.empty-state')) {
      log.innerHTML = '';
    }

    // Deduplicate: skip if this entry was already rendered
    const id = entry.actionId || entry.id;
    if (id && this._renderedActionIds.has(id)) return;
    if (id) this._renderedActionIds.add(id);

    this._appendStoryNode(entry, tone, true);

    // Keep _lastStoryCount in sync so renderStoryLog won't full-rebuild
    this._lastStoryCount++;
  },

  _appendStoryNode(entry, tone, animate) {
    const log = this.$('story-log');
    const node = document.createElement('div');
    const stateTone = tone || (entry.success === false ? 'failure' : entry.success === true ? 'success' : 'info');
    node.className = `story-entry ${stateTone === 'info' ? 'command' : stateTone}`;

    // Tag with actionId for deduplication and reconciliation
    const entryId = entry.actionId || entry.id;
    if (entryId) node.dataset.actionId = entryId;

    if (animate) {
      node.classList.add('fade-slide-in');
    }

    let html = '';

    const cleanedNarration = this._stripRoomMetadata(entry.narration);
    const cleanedMechanicalSummary = this._stripRoomMetadata(entry.mechanicalSummary);
    const commandText = this._extractCommandText(entry, cleanedMechanicalSummary);
    const normalizedCommand = commandText ? this._normalizeRoomLine(commandText) : '';
    const normalizedMechanical = cleanedMechanicalSummary ? this._normalizeRoomLine(cleanedMechanicalSummary.replace(/^>\s*/, '')) : '';
    const shouldRenderMechanicalSummary = !!cleanedMechanicalSummary
      && !(commandText && normalizedMechanical === normalizedCommand)
      && !(entry.success === false && cleanedNarration);

    if (commandText) {
      html += `<div class="story-command-line">&gt; ${this.esc(commandText)}</div>`;
    }

    // Narration: stream newest entry, render older ones statically
    const streamNarration = animate && cleanedNarration;
    if (cleanedNarration && !streamNarration) {
      html += `<div class="story-narration">${this.esc(cleanedNarration)}</div>`;
    }

    if (shouldRenderMechanicalSummary && !streamNarration) {
      html += this._formatMechanicalParsed(cleanedMechanicalSummary);
    }

    if (entry.diceRolls?.length && !streamNarration) {
      html += `<div class="story-dice">${entry.diceRolls.map((roll) => this.esc(`[${roll.purpose || roll.expression || 'Roll'}: ${roll.total ?? '?'}]`)).join(' ')}</div>`;
    }

    if (!html && !streamNarration) {
      return;
    }

    node.innerHTML = html;

    log.appendChild(node);

    // Start streaming narration for the newest entry
    if (streamNarration) {
      const mechanicalHtml = shouldRenderMechanicalSummary
        ? this._formatMechanicalParsed(cleanedMechanicalSummary)
        : '';
      const diceHtml = entry.diceRolls?.length
        ? `<div class="story-dice">${entry.diceRolls.map((roll) => this.esc(`[${roll.purpose || roll.expression || 'Roll'}: ${roll.total ?? '?'}]`)).join(' ')}</div>`
        : '';
      this._startStreaming(node, cleanedNarration, mechanicalHtml + diceHtml);
    }

    while (log.children.length > 50) {
      log.removeChild(log.firstChild);
    }

    log.scrollTop = log.scrollHeight;
  },

  /* ── Streaming text reveal ── */

  _streamTimer: null,
  _streamNode: null,

  _startStreaming(parentNode, text, trailingHtml) {
    // Cancel any previous stream
    this._cancelStreaming();

    const narrationDiv = document.createElement('div');
    narrationDiv.className = 'story-narration';

    const textSpan = document.createElement('span');
    textSpan.className = 'streaming-text';

    const cursor = document.createElement('span');
    cursor.className = 'cursor-blink';
    cursor.textContent = '\u2588';

    narrationDiv.appendChild(textSpan);
    narrationDiv.appendChild(cursor);
    parentNode.appendChild(narrationDiv);

    let charIndex = 0;
    const escaped = this.esc(text);
    const log = this.$('story-log');
    const input = this.$('command-input');

    // Disable input while streaming
    if (input) input.disabled = true;

    this._streamNode = parentNode;

    const finishStream = () => {
      if (this._streamTimer) {
        clearTimeout(this._streamTimer);
        this._streamTimer = null;
      }
      this._streamNode = null;

      // Show full text
      textSpan.textContent = text;
      cursor.remove();
      narrationDiv.classList.add('stream-complete');

      // Append trailing content (mechanical summary, dice)
      if (trailingHtml) {
        const trailing = document.createElement('div');
        trailing.innerHTML = trailingHtml;
        while (trailing.firstChild) {
          parentNode.appendChild(trailing.firstChild);
        }
      }

      // Re-enable input
      if (input) {
        input.disabled = false;
        input.focus();
      }
      if (log) log.scrollTop = log.scrollHeight;
    };

    // Click-to-skip on the narration node
    const skipHandler = () => {
      finishStream();
      parentNode.removeEventListener('click', skipHandler);
    };
    parentNode.addEventListener('click', skipHandler);
    parentNode.style.cursor = 'pointer';

    const tick = () => {
      if (charIndex < escaped.length) {
        // Advance 1-3 chars per tick for natural feel
        const chunk = Math.min(escaped.length - charIndex, 1 + Math.floor(Math.random() * 2));
        charIndex += chunk;
        textSpan.textContent = text.slice(0, charIndex);
        if (log) log.scrollTop = log.scrollHeight;
        this._streamTimer = setTimeout(tick, 18);
      } else {
        finishStream();
        parentNode.removeEventListener('click', skipHandler);
        parentNode.style.cursor = '';
      }
    };

    this._streamTimer = setTimeout(tick, 18);
  },

  _cancelStreaming() {
    if (this._streamTimer) {
      clearTimeout(this._streamTimer);
      this._streamTimer = null;
    }
    // If there's a streaming node, instantly complete it
    if (this._streamNode) {
      const cursor = this._streamNode.querySelector('.cursor-blink');
      if (cursor) cursor.remove();
      const textSpan = this._streamNode.querySelector('.streaming-text');
      if (textSpan) {
        // Already has partial text — leave it as-is (full text was set)
      }
      this._streamNode = null;
    }
    // Re-enable input in case it was locked
    const input = this.$('command-input');
    if (input) input.disabled = false;
  },

  renderPlayersList(players, currentPlayerId, session) {
    const container = this.$('players-list');
    if (!container) {
      return;
    }

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

    this._adminPlayers = players;
    this._adminCurrentPlayerId = currentPlayerId;

    // Always rebuild search bar + list wrapper
    container.innerHTML = `
      <div class="admin-search-bar search-bar" style="margin-bottom:0.75rem;">
        <input type="text" id="admin-player-search" placeholder="Search players by name, id, race, class..." />
        <span class="search-count" id="admin-player-count"></span>
      </div>
      <div id="admin-players-list" class="portal-player-list"></div>
    `;
    const searchInput = this.$('admin-player-search');
    if (searchInput) {
      searchInput.addEventListener('input', () => this._filterAdminPlayers());
    }

    this._filterAdminPlayers();
  },

  _filterAdminPlayers() {
    const list = this.$('admin-players-list');
    const countEl = this.$('admin-player-count');
    const searchInput = this.$('admin-player-search');
    const players = this._adminPlayers || [];
    const currentPlayerId = this._adminCurrentPlayerId || '';
    const query = (searchInput?.value || '').toLowerCase().trim();

    const filtered = query
      ? players.filter(p => {
          const hay = `${p.name} ${p.id} ${p.race} ${p.class} ${p.currentRoomId}`.toLowerCase();
          return hay.includes(query);
        })
      : players;

    if (countEl) {
      countEl.textContent = query
        ? `${filtered.length} / ${players.length}`
        : `${players.length} characters`;
    }

    if (!filtered.length) {
      list.innerHTML = '<div class="empty-state">No players match your search.</div>';
      return;
    }

    list.innerHTML = filtered.map((player) => `
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

  /* ── Interaction mode tracking ── */

  _interactionMode: 'explore',
  _interactionTarget: '',

  updateInteractionMode(mode, target) {
    this._interactionMode = mode || 'explore';
    this._interactionTarget = target || '';

    // Re-render chips and prompt with current room context
    this.updateExitChips(this._roomContext);
    this.updatePrompt(this._roomContext);

    // Update stat bar mode badge
    const bar = this.$('stat-bar');
    if (!bar) return;

    const existing = bar.querySelector('.stat-mode-badge');
    if (existing) existing.remove();

    if (mode && mode !== 'explore') {
      const badge = document.createElement('span');
      badge.className = `stat-mode-badge mode-${mode}`;
      badge.textContent = mode === 'combat' ? `\u2694 ${mode.toUpperCase()}` : `\u{1F4AC} ${mode.toUpperCase()}`;
      bar.prepend(badge);
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
      const isCollapsed = drawer.classList.contains('collapsed');
      localStorage.setItem('gae.info-drawer', isCollapsed ? 'collapsed' : 'open');
      const btn = this.$('btn-toggle-info');
      if (btn) btn.classList.toggle('active', !isCollapsed);
    }
  },

  restoreInfoDrawer() {
    const saved = localStorage.getItem('gae.info-drawer');
    const drawer = this.$('info-drawer');
    if (drawer && saved === 'collapsed') {
      drawer.classList.add('collapsed');
    }
    const btn = this.$('btn-toggle-info');
    if (btn && drawer) {
      btn.classList.toggle('active', !drawer.classList.contains('collapsed'));
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

  _extractCommandText(entry, mechanicalSummary) {
    const explicit = String(entry?.rawInput || '').trim();
    if (explicit) {
      return explicit;
    }

    const mechanical = String(mechanicalSummary || '').trim();
    if (/^>\s*/.test(mechanical) && !entry?.narration) {
      return mechanical.replace(/^>\s*/, '').trim();
    }

    return '';
  },

  updateExitChips(room) {
    const container = this.$('exit-chips');
    if (!container) return;

    const mode = this._interactionMode || 'explore';

    if (mode === 'conversation') {
      container.innerHTML = [
        ['ask about rumors', 'ask about rumors'],
        ['flirt', 'flirt'],
        ['threaten', 'threaten'],
        ['trade', 'trade'],
        ['goodbye', 'goodbye']
      ].map(([label, cmd]) =>
        `<button class="btn btn-secondary btn-sm exit-chip interaction-chip" data-interaction-cmd="${this.esc(cmd)}" type="button">${this.esc(label)}</button>`
      ).join('');
      return;
    }

    if (mode === 'combat') {
      container.innerHTML = [
        ['attack', 'attack'],
        ['defend', 'defend'],
        ['flee', 'flee'],
        ['use item', 'use item']
      ].map(([label, cmd]) =>
        `<button class="btn btn-secondary btn-sm exit-chip interaction-chip" data-interaction-cmd="${this.esc(cmd)}" type="button">${this.esc(label)}</button>`
      ).join('');
      return;
    }

    const exits = room ? Object.keys(room.exits || {}) : [];
    container.innerHTML = exits.length
      ? exits.map((d) => `<button class="btn btn-secondary btn-sm exit-chip" data-room-exit="${this.esc(d)}" type="button">go ${this.esc(d)}</button>`).join('')
      : '';
  },

  updatePrompt(room) {
    const prompt = this.$('command-prompt');
    if (!prompt) return;

    const mode = this._interactionMode || 'explore';
    const target = this._interactionTarget || '';

    if (mode === 'conversation' && target) {
      prompt.innerHTML = `<span class="prompt-mode conversation">[talking to ${this.esc(target)}]</span> &gt;`;
      return;
    }

    if (mode === 'combat' && target) {
      prompt.innerHTML = `<span class="prompt-mode combat">[COMBAT: ${this.esc(target)}]</span> &gt;`;
      return;
    }

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

      // Also strip lines that look like a standalone room header (name repeated from another room)
      if (/^[A-Z][A-Za-z ']+$/.test(trimmed) && trimmed.length < 50 && index === 0 && lines.length > 3) {
        continue;
      }

      // Strip lines that are just a short description followed by metadata (looks like room dump)
      if (/^A (repeatable|manual|test|fixture)\b/i.test(trimmed)) {
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
  },

  // ── AI Conversation Logs ──

  async loadConversationLogs() {
    const opFilter = this.$('logs-operation-filter')?.value || '';
    const playerFilter = this.$('logs-player-filter')?.value?.trim() || '';

    try {
      const [logsData, stats] = await Promise.all([
        API.getConversationLogs(opFilter, playerFilter, 100, 0),
        API.getConversationStats()
      ]);

      this.renderLogsStats(stats);
      this.renderLogsList(logsData);
    } catch (err) {
      const list = this.$('logs-list');
      if (list) list.innerHTML = `<div class="empty-state">Failed to load logs: ${this.esc(err.message)}</div>`;
    }
  },

  renderLogsStats(stats) {
    const container = this.$('logs-stats');
    if (!container) return;

    container.innerHTML = `
      <div class="logs-stat-card">
        <span class="stat-value">${stats.totalExchanges}</span>
        <span class="stat-label">Total Exchanges</span>
      </div>
      <div class="logs-stat-card">
        <span class="stat-value">${stats.uniquePlayers}</span>
        <span class="stat-label">Unique Players</span>
      </div>
      <div class="logs-stat-card">
        <span class="stat-value">${stats.avgLatencyMs}ms</span>
        <span class="stat-label">Avg Latency</span>
      </div>
      <div class="logs-stat-card">
        <span class="stat-value">${stats.errorRate}%</span>
        <span class="stat-label">Error Rate</span>
      </div>
      ${stats.byOperation.map(op => `
        <div class="logs-stat-card">
          <span class="stat-value">${op.count}</span>
          <span class="stat-label">${this.esc(op.operation)}</span>
        </div>
      `).join('')}
    `;
  },

  renderLogsList(data) {
    const container = this.$('logs-list');
    const countEl = this.$('logs-count');
    if (!container) return;

    if (countEl) {
      countEl.textContent = `${data.logs.length} of ${data.total} entries`;
    }

    if (!data.logs.length) {
      container.innerHTML = '<div class="empty-state">No conversation logs yet. Play the game to generate AI exchanges.</div>';
      return;
    }

    container.innerHTML = data.logs.map(log => {
      const time = new Date(log.timestamp).toLocaleString();
      const preview = (log.response || '').substring(0, 120);
      const latencyClass = log.latencyMs > 10000 ? 'log-error' : '';

      return `
        <div class="log-entry" onclick="this.classList.toggle('expanded')">
          <div class="log-header">
            <span class="log-operation">${this.esc(log.operation)}</span>
            <div class="log-meta">
              ${log.playerId ? `<span>Player: ${this.esc(log.playerId).substring(0, 12)}...</span>` : ''}
              <span class="${latencyClass}">${log.latencyMs}ms</span>
              <span>${this.esc(log.model)}</span>
              ${!log.success ? '<span class="log-error">FAILED</span>' : ''}
              <span>${time}</span>
            </div>
          </div>
          <div class="log-preview">${this.esc(preview)}${preview.length >= 120 ? '...' : ''}</div>
          <div class="log-detail">
            <div class="log-prompt-section">
              <h4>System Prompt</h4>
              <pre>${this.esc(log.systemPrompt)}</pre>
            </div>
            <div class="log-prompt-section">
              <h4>User Prompt</h4>
              <pre>${this.esc(log.userPrompt)}</pre>
            </div>
            <div class="log-prompt-section">
              <h4>Response</h4>
              <pre>${this.esc(log.response)}</pre>
            </div>
            ${log.errorMessage ? `
              <div class="log-prompt-section">
                <h4>Error</h4>
                <pre>${this.esc(log.errorMessage)}</pre>
              </div>
            ` : ''}
          </div>
        </div>
      `;
    }).join('');
  },

  wireLogsTab() {
    const refreshBtn = this.$('btn-logs-refresh');
    const opFilter = this.$('logs-operation-filter');
    const playerFilter = this.$('logs-player-filter');

    if (refreshBtn) {
      refreshBtn.addEventListener('click', () => this.loadConversationLogs());
    }
    if (opFilter) {
      opFilter.addEventListener('change', () => this.loadConversationLogs());
    }
    if (playerFilter) {
      let debounce;
      playerFilter.addEventListener('input', () => {
        clearTimeout(debounce);
        debounce = setTimeout(() => this.loadConversationLogs(), 400);
      });
    }
  },

  // ═══════════════════════════════════════════════════════════
  //  CONTENT REGISTRY TAB
  // ═══════════════════════════════════════════════════════════

  _registryData: [],
  _registryEditingId: null,

  async loadRegistry() {
    const type = this.$('registry-type-select')?.value || 'spells';
    const countEl = this.$('registry-count');
    const list = this.$('registry-list');
    if (!list) return;

    try {
      this._registryData = await API.getRegistry(type);
      if (countEl) countEl.textContent = `${this._registryData.length} entries`;
      this.renderRegistryList(type, this._registryData);
    } catch (err) {
      list.innerHTML = `<div class="empty-state">Failed to load: ${this.esc(err.message)}</div>`;
    }
  },

  renderRegistryList(type, entries) {
    const list = this.$('registry-list');
    if (!entries.length) {
      list.innerHTML = '<div class="empty-state">No entries in this registry.</div>';
      return;
    }

    list.innerHTML = entries.map(entry => {
      const meta = this._getEntryMeta(type, entry);
      const tags = (entry.tags || []).slice(0, 5);
      return `
        <div class="registry-entry" data-registry-id="${this.esc(entry.id)}">
          <div>
            <div class="registry-entry-name">${this.esc(entry.name)}</div>
            <div class="registry-entry-meta">${this.esc(meta)}</div>
            ${tags.length ? `<div class="registry-entry-tags">${tags.map(t => `<span class="registry-entry-tag">${this.esc(t)}</span>`).join('')}</div>` : ''}
          </div>
          <div class="registry-entry-actions">
            <button class="btn btn-primary btn-sm" data-reg-action="edit" data-reg-id="${this.esc(entry.id)}" type="button">Edit</button>
            <button class="btn btn-danger btn-sm" data-reg-action="delete" data-reg-id="${this.esc(entry.id)}" type="button">Del</button>
          </div>
        </div>
      `;
    }).join('');
  },

  _getEntryMeta(type, entry) {
    switch (type) {
      case 'spells':
        return `${entry.school || '?'} | Power ${entry.powerLevel || '?'} | ${entry.manaCost || 0} MP | Lv.${entry.requiredLevel || 1}${entry.damageDice ? ` | ${entry.damageDice}` : ''}${entry.healDice ? ` | Heal ${entry.healDice}` : ''}`;
      case 'items':
        return `${entry.type || 'Misc'} | ${entry.rarity || 'common'} | ${entry.value || 0}g${entry.damageDice ? ` | ${entry.damageDice}` : ''}${entry.armorValue ? ` | AC+${entry.armorValue}` : ''}`;
      case 'classes':
        return `${entry.hitDie || '?'} | ${entry.primaryStat || '?'}${entry.canCastSpells ? ' | Caster' : ' | Martial'} | ${(entry.spellList || []).length} spells`;
      case 'races':
        return `${(entry.traits || []).join(', ')}`;
      default:
        return entry.id || '';
    }
  },

  openRegistryEditor(entry, type) {
    const panel = this.$('registry-editor-panel');
    const title = this.$('registry-editor-title');
    const textarea = this.$('registry-json-textarea');
    const chatMessages = this.$('registry-chat-messages');
    const deleteBtn = this.$('btn-registry-delete');

    if (!panel) return;
    panel.classList.remove('hidden');

    if (entry) {
      this._registryEditingId = entry.id;
      title.textContent = `Edit: ${entry.name}`;
      textarea.value = JSON.stringify(entry, null, 2);
      if (deleteBtn) deleteBtn.classList.remove('hidden');
    } else {
      this._registryEditingId = null;
      title.textContent = `New ${type || 'Entry'}`;
      textarea.value = '';
      if (deleteBtn) deleteBtn.classList.add('hidden');
    }

    chatMessages.innerHTML = '<div class="chat-msg system">Describe what you want to create or change. The AI will fill in the structured details for you.</div>';
  },

  closeRegistryEditor() {
    const panel = this.$('registry-editor-panel');
    if (panel) panel.classList.add('hidden');
    this._registryEditingId = null;
  },

  async sendRegistryChat() {
    const input = this.$('registry-chat-input');
    const chatMessages = this.$('registry-chat-messages');
    const textarea = this.$('registry-json-textarea');
    const type = this.$('registry-type-select')?.value || 'spells';

    if (!input || !input.value.trim()) return;

    const userMsg = input.value.trim();
    input.value = '';

    // Add user message
    chatMessages.innerHTML += `<div class="chat-msg user">${this.esc(userMsg)}</div>`;
    chatMessages.innerHTML += `<div class="chat-msg ai loading" id="reg-chat-loading">Thinking...</div>`;
    chatMessages.scrollTop = chatMessages.scrollHeight;

    try {
      // Singular form for API
      const singularType = type.replace(/s$/, '');
      const existingJson = textarea.value.trim() || null;
      const result = await API.generateContent(singularType, userMsg, existingJson);

      const loadingEl = this.$('reg-chat-loading');
      if (loadingEl) loadingEl.remove();

      chatMessages.innerHTML += `<div class="chat-msg ai">Done! Check the JSON preview. Send another message to refine, or Save to Registry when ready.</div>`;
      chatMessages.scrollTop = chatMessages.scrollHeight;

      // Update JSON preview
      if (result.json) {
        try {
          const parsed = JSON.parse(result.json);
          textarea.value = JSON.stringify(parsed, null, 2);
        } catch {
          textarea.value = result.json;
        }
      }
    } catch (err) {
      const loadingEl = this.$('reg-chat-loading');
      if (loadingEl) loadingEl.remove();
      chatMessages.innerHTML += `<div class="chat-msg system">Error: ${this.esc(err.message)}</div>`;
      chatMessages.scrollTop = chatMessages.scrollHeight;
    }
  },

  async saveRegistryEntry() {
    const type = this.$('registry-type-select')?.value || 'spells';
    const textarea = this.$('registry-json-textarea');
    if (!textarea?.value.trim()) return;

    try {
      const data = JSON.parse(textarea.value);
      await API.upsertRegistryEntry(type, data);
      this.closeRegistryEditor();
      await this.loadRegistry();
    } catch (err) {
      alert('Save failed: ' + err.message);
    }
  },

  async deleteRegistryEntry(id) {
    const type = this.$('registry-type-select')?.value || 'spells';
    if (!id) return;
    if (!confirm(`Delete "${id}" from ${type}?`)) return;

    try {
      await API.deleteRegistryEntry(type, id);
      this.closeRegistryEditor();
      await this.loadRegistry();
    } catch (err) {
      alert('Delete failed: ' + err.message);
    }
  },

  wireRegistryTab() {
    const typeSelect = this.$('registry-type-select');
    const refreshBtn = this.$('btn-refresh-registry');
    const newBtn = this.$('btn-new-registry-entry');
    const saveBtn = this.$('btn-registry-save');
    const cancelBtn = this.$('btn-registry-cancel');
    const deleteBtn = this.$('btn-registry-delete');
    const sendBtn = this.$('btn-registry-chat-send');
    const chatInput = this.$('registry-chat-input');
    const listEl = this.$('registry-list');

    if (typeSelect) typeSelect.addEventListener('change', () => this.loadRegistry());
    if (refreshBtn) refreshBtn.addEventListener('click', () => this.loadRegistry());
    if (newBtn) newBtn.addEventListener('click', () => this.openRegistryEditor(null, (typeSelect?.value || 'spells').replace(/s$/, '')));
    if (saveBtn) saveBtn.addEventListener('click', () => this.saveRegistryEntry());
    if (cancelBtn) cancelBtn.addEventListener('click', () => this.closeRegistryEditor());
    if (deleteBtn) deleteBtn.addEventListener('click', () => this.deleteRegistryEntry(this._registryEditingId));
    if (sendBtn) sendBtn.addEventListener('click', () => this.sendRegistryChat());
    if (chatInput) {
      chatInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') this.sendRegistryChat();
      });
    }

    // Click on entries in the list
    if (listEl) {
      listEl.addEventListener('click', (e) => {
        const editBtn = e.target.closest('[data-reg-action="edit"]');
        const delBtn = e.target.closest('[data-reg-action="delete"]');
        if (editBtn) {
          const id = editBtn.dataset.regId;
          const entry = this._registryData.find(e => e.id === id);
          if (entry) this.openRegistryEditor(entry, (typeSelect?.value || 'spells').replace(/s$/, ''));
        }
        if (delBtn) {
          this.deleteRegistryEntry(delBtn.dataset.regId);
        }
      });
    }
  }
};