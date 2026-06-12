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
  'admin-player-select',
  'transfer-player-select',
  'msg-player-select'
];

const UI = {
  _roomContext: null,
  _creationOptions: null,

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
      this.updateCreationDestinationMode(this.$('char-destination-mode')?.value || 'world');
      this.refreshCreationDestinationPreview();
      this.$('char-name').focus();
    }
  },

  showPlayerSelect(show) {
    const panel = this.$('player-select-panel');
    if (panel) panel.classList.toggle('hidden', !show);
  },

  populateCreationOptions(options) {
    this._creationOptions = options || { worlds: [], blindStorylines: [], defaultWorldId: 'default-world' };

    const worlds = this._creationOptions.worlds || [];
    const blindStorylines = this._creationOptions.blindStorylines || [];
    const defaultWorldId = this._creationOptions.defaultWorldId || 'default-world';

    const worldSelect = this.$('char-world');
    if (worldSelect) {
      const previousWorldId = worldSelect.value;
      worldSelect.innerHTML = worlds.length
        ? worlds.map((world) => `<option value="${this.esc(world.id)}">${this.esc(world.name)}</option>`).join('')
        : '<option value="">No worlds available</option>';

      const preferredWorldId = worlds.some((world) => world.id === previousWorldId)
        ? previousWorldId
        : worlds.some((world) => world.id === defaultWorldId)
          ? defaultWorldId
          : (worlds[0]?.id || '');
      worldSelect.value = preferredWorldId;
    }

    const blindSelect = this.$('char-blind-storyline');
    if (blindSelect) {
      const previousStorylineId = blindSelect.value;
      blindSelect.innerHTML = blindStorylines.length
        ? blindStorylines.map((storyline) => `<option value="${this.esc(storyline.id)}">${this.esc(storyline.name)}</option>`).join('')
        : '<option value="">No Blind templates available</option>';

      if (blindStorylines.length > 0) {
        blindSelect.value = blindStorylines.some((storyline) => storyline.id === previousStorylineId)
          ? previousStorylineId
          : blindStorylines[0].id;
      }
    }

    this.updateCreationDestinationMode(this.$('char-destination-mode')?.value || 'world');
    this.refreshCreationDestinationPreview();
  },

  updateCreationDestinationMode(mode) {
    this.$('char-world-group')?.classList.toggle('hidden', mode !== 'world');
    this.$('char-blind-group')?.classList.toggle('hidden', mode !== 'blind');
    this.refreshCreationDestinationPreview();
  },

  refreshCreationDestinationPreview() {
    const preview = this.$('char-destination-preview');
    if (!preview) return;

    const mode = this.$('char-destination-mode')?.value || 'world';
    if (mode === 'blind') {
      const storylineId = this.$('char-blind-storyline')?.value || '';
      const storyline = (this._creationOptions?.blindStorylines || []).find((item) => item.id === storylineId);
      preview.textContent = storyline
        ? `${storyline.setting}. Tone: ${storyline.tone}. Theme: ${storyline.theme}.`
        : 'Choose a Blind Adventure template to begin with an authored scenario.';
      return;
    }

    const worldId = this.$('char-world')?.value || '';
    const world = (this._creationOptions?.worlds || []).find((item) => item.id === worldId);
    preview.textContent = world
      ? (world.characterCreationIntro || world.description || `Begin in ${world.name}.`)
      : 'Choose a world to decide where this character enters the story.';
  },

  setResumeMessage(message, tone = 'info') {
    const el = this.$('resume-message');
    if (!el) return;
    if (!message) {
      el.textContent = '';
      el.className = 'inline-message hidden';
      return;
    }
    el.textContent = message;
    el.className = `inline-message ${tone}`;
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
      ? `Signed in as ${session.username}.`
      : 'Authentication required.';

    const badge = this.$('session-badge');
    badge.textContent = session ? `${session.username} | ${session.role}` : 'Signed out';
    badge.classList.toggle('admin', canAdmin);

    this.$('auth-form').classList.toggle('hidden', signedIn);
    this.$('btn-logout-header').classList.toggle('hidden', !signedIn);
    this.$('btn-logout-portal').classList.toggle('hidden', !signedIn);
    this.$('btn-fill-user').classList.toggle('hidden', signedIn);
    this.$('btn-fill-admin').classList.toggle('hidden', signedIn);

    const modeAdmin = document.querySelector('[data-mode-button="admin"]');
    if (modeAdmin) modeAdmin.classList.toggle('hidden', !canAdmin);

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

  renderPortalPlayers(_players, _currentPlayerId, _session) {
    // Character list removed — players resume by entering their ID directly.
  },

  _filterPortalPlayers() {},

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
    const set = (id, text) => { const el = this.$(id); if (el) el.textContent = text; };
    const html = (id, markup) => { const el = this.$(id); if (el) el.innerHTML = markup; };

    set('header-player', 'No active character');
    set('char-title', 'Character');
    set('char-meta', '');
    this.setBar('hp-bar', 'hp-text', 0, 0);
    this.setBar('mp-bar', 'mp-text', 0, 0);
    this.setBar('xp-bar', 'xp-text', 0, 100);
    html('stats-grid', '<div class="empty-state">No stats loaded.</div>');
    html('character-details', '<div class="empty-state">Dynamic character details will appear here.</div>');
    set('char-gold', 'Gold 0');
    set('char-level', 'Level 1');
    set('char-defense', 'Defense 10');
    html('equipment-slots', '<div class="empty-state">No equipment loaded.</div>');
    html('inventory-list', '<div class="inv-empty">Inventory unavailable until a character is selected.</div>');
    html('status-effects', '<span class="no-effects">No active effects.</span>');
    this.renderPayloads(null, null);
    this.renderRoom(null);
    this.renderStatBar(null);
    html('story-log', '');
    this.setUserCommandState(false, isAuthenticated);
    this.showPlayerSelect(true);
  },

  renderPlayer(player) {
    if (!player) {
      this.renderNoActivePlayer(true);
      return;
    }

    const set = (id, text) => { const el = this.$(id); if (el) el.textContent = text; };
    const setHtml = (id, markup) => { const el = this.$(id); if (el) el.innerHTML = markup; };

    set('header-player', `${player.name} | Lv.${player.level} ${player.race} ${player.class}`);
    set('char-title', player.name);
    set('char-meta', `${player.race} ${player.class} | Room ${player.currentRoomId}`);
    this.setUserCommandState(true, true);

    this.setBar('hp-bar', 'hp-text', player.hp, player.maxHp);
    this.setBar('mp-bar', 'mp-text', player.mp, player.maxMp);

    const xpGoal = Math.max(100, player.level * 100);
    this.setBar('xp-bar', 'xp-text', player.xp, xpGoal);

    // Update block-char stat bar
    this.renderStatBar(player);

    const statEntries = this.getStatEntries(player);
    setHtml('stats-grid', statEntries.length
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
      : '<div class="empty-state">No stat fields detected.</div>');

    const detailEntries = this.getDetailEntries(player);
    setHtml('character-details', detailEntries.length
      ? detailEntries.map((entry) => `
          <div class="detail-card">
            <div class="detail-label">${this.esc(entry.label)}</div>
            <div class="detail-value">${this.esc(entry.value)}</div>
          </div>
        `).join('')
      : '<div class="empty-state">No extra character fields detected.</div>');

    set('char-gold', `Gold ${player.gold}`);
    set('char-level', `Level ${player.level}`);
    set('char-defense', `Defense ${player.defense ?? 10}`);

    const equipment = player.equipment || {};
    const slots = Object.entries(equipment);
    setHtml('equipment-slots', slots.length
      ? slots.map(([name, item]) => `
      <div class="equip-slot">
        <div class="slot-name">${this.esc(this.humanizeKey(name))}</div>
        <div class="${item ? 'slot-item' : 'slot-empty'}">${this.esc(item ? this.summarizeEntity(item) : 'Empty')}</div>
      </div>
    `).join('')
      : '<div class="empty-state">No equipment slots populated.</div>');

    const inventory = player.inventory || [];
    setHtml('inventory-list', inventory.length
      ? inventory.map((item) => `
          <div class="inv-item">
            <strong>${this.esc(this.summarizeEntity(item))}</strong>
            <span class="player-card-meta">${this.esc(this.describeSupplementaryFields(item))}</span>
          </div>
        `).join('')
      : '<div class="inv-empty">Inventory empty.</div>');

    const effects = player.statusEffects || [];
    setHtml('status-effects', effects.length
      ? effects.map((effect) => `<span class="status-tag">${this.esc(this.summarizeEntity(effect))}</span>`).join('')
      : '<span class="no-effects">No active effects.</span>');
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
    const fill = this.$(fillId);
    const text = this.$(textId);
    if (fill) fill.style.width = `${pct}%`;
    if (text) text.textContent = `${current} / ${safeMax}`;
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
    const normalizedNarration = cleanedNarration ? this._normalizeRoomLine(cleanedNarration) : '';
    // Suppress mechanical summary when it duplicates narration (exact match or content overlap)
    const mechanicalRedundant = normalizedNarration && (
      normalizedMechanical === normalizedNarration
      || (normalizedMechanical.length > 20 && normalizedNarration.includes(normalizedMechanical))
      || this._mechanicalSubsumedByNarration(normalizedMechanical, normalizedNarration)
    );
    const shouldRenderMechanicalSummary = !!cleanedMechanicalSummary
      && !(commandText && normalizedMechanical === normalizedCommand)
      && !(entry.success === false && cleanedNarration)
      && !mechanicalRedundant;

    if (commandText) {
      html += `<div class="story-command-line">&gt; ${this.esc(commandText)}</div>`;
    }

    // Narration: stream newest entry, render older ones statically
    const streamNarration = animate && cleanedNarration;
    if (cleanedNarration && !streamNarration) {
      html += `<div class="story-narration">${this.formatNarration(cleanedNarration)}</div>`;
    }

    if (shouldRenderMechanicalSummary && !streamNarration) {
      html += this._formatMechanicalParsed(cleanedMechanicalSummary);
    }

    if (entry.diceRolls?.length && !streamNarration) {
      html += `<div class="story-dice">${entry.diceRolls.map((roll) => this._formatDiceRoll(roll)).join(' ')}</div>`;
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
        ? `<div class="story-dice">${entry.diceRolls.map((roll) => this._formatDiceRoll(roll)).join(' ')}</div>`
        : '';
      this._startStreaming(node, cleanedNarration, mechanicalHtml + diceHtml);
    }

    while (log.children.length > 50) {
      log.removeChild(log.firstChild);
    }

    log.scrollTop = log.scrollHeight;
  },

  /* ── Dice roll formatting ── */

  _formatDiceRoll(roll) {
    const label = this.esc(roll.purpose || roll.expression || 'Roll');
    const rolls = (roll.individualRolls || []).join(', ');
    const mod = roll.modifier ? (roll.modifier > 0 ? `+${roll.modifier}` : `${roll.modifier}`) : '';
    const vs = roll.targetNumber != null ? ` vs ${roll.targetNumber}` : '';
    const badge = this._outcomeBadge(roll.outcome);
    return `<span class="dice-roll outcome-${(roll.outcome || 'none').toLowerCase()}">`
      + `🎲 [${rolls}]${mod} = <strong>${roll.total ?? '?'}</strong>${vs} (${label})${badge}</span>`;
  },

  _outcomeBadge(outcome) {
    switch ((outcome || '').toLowerCase()) {
      case 'criticalhit': return ' <span class="outcome-badge crit">💥 CRITICAL!</span>';
      case 'hit': return ' <span class="outcome-badge hit">✅ Hit</span>';
      case 'glancinghit': return ' <span class="outcome-badge glancing">🔶 Glancing</span>';
      case 'miss': return ' <span class="outcome-badge miss">❌ Miss</span>';
      case 'criticalmiss': return ' <span class="outcome-badge fumble">💀 Fumble</span>';
      default: return '';
    }
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
    const submit = this.$('command-submit');

    // Disable input while streaming
    if (input) input.disabled = true;
    if (submit) submit.disabled = true;

    this._streamNode = parentNode;

    const finishStream = () => {
      if (this._streamTimer) {
        clearTimeout(this._streamTimer);
        this._streamTimer = null;
      }
      this._streamNode = null;

      // Show full text with formatting
      textSpan.innerHTML = UI.formatNarration(text);
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
      if (submit) submit.disabled = false;
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
    const submit = this.$('command-submit');
    if (submit) submit.disabled = false;
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
      ['Worlds', summary.worldCount ?? '?']
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
    const OPTIONAL_SELECTS = new Set(['msg-player-select']);
    PLAYER_SELECT_IDS.forEach((id) => {
      const select = this.$(id);
      if (!select) return;

      const isOptional = OPTIONAL_SELECTS.has(id);
      const previousValue = select.value;
      if (!players.length) {
        select.innerHTML = isOptional
          ? '<option value="">All Players (Broadcast)</option>'
          : '<option value="">No players available</option>';
        return;
      }

      const optionsHtml = players.map((player) => `
        <option value="${this.esc(player.id)}">${this.esc(player.name)} (${this.esc(player.id)})</option>
      `).join('');

      select.innerHTML = isOptional
        ? `<option value="">All Players (Broadcast)</option>${optionsHtml}`
        : optionsHtml;

      if (!isOptional) {
        const nextValue = players.some((player) => player.id === previousValue)
          ? previousValue
          : players.some((player) => player.id === preferredPlayerId)
            ? preferredPlayerId
            : players[0].id;
        select.value = nextValue;
      } else if (previousValue) {
        select.value = players.some((player) => player.id === previousValue) ? previousValue : '';
      }
    });
  },

  // ── Overview admin browser ──

  _ovSelectedItem: null,
  _ovSelectedType: null,
  _ovJsonMode: false,
  _ovWorldMap: {},

  _ovGetWorldFilter() {
    return this.$('overview-world-filter')?.value || '';
  },

  _ovWorldLabel(worldIds) {
    if (!worldIds || !worldIds.length) return '';
    return worldIds.map(id => this._ovWorldMap[id] || id).join(', ');
  },

  populateOverviewWorldFilter(worlds) {
    this._ovWorldMap = {};
    const sel = this.$('overview-world-filter');
    if (!sel) return;
    const current = sel.value;
    sel.innerHTML = '<option value="">All Worlds</option>';
    for (const w of worlds) {
      this._ovWorldMap[w.id] = w.name;
      const opt = document.createElement('option');
      opt.value = w.id;
      opt.textContent = w.name;
      sel.appendChild(opt);
    }
    if (current) sel.value = current;
  },

  async ovSearch(query) {
    const results = this.$('overview-results');
    if (!results) return;
    if (!query.trim()) {
      this._ovShowWelcome();
      return;
    }

    const typeFilter = this.$('overview-type-filter')?.value || '';
    const worldFilter = this._ovGetWorldFilter();
    results.innerHTML = '<div class="dm-result-count">Searching...</div>';
    try {
      const data = await API.dmSearch(query, typeFilter || undefined, worldFilter || undefined);
      this._ovRenderResults(data.results, query);
    } catch (err) {
      results.innerHTML = `<div class="dm-no-results">Error: ${this.esc(err.message)}</div>`;
    }
  },

  async ovBrowse(type) {
    const results = this.$('overview-results');
    const input = this.$('overview-search-input');
    if (!results) return;
    if (input) input.value = '';

    const worldFilter = this._ovGetWorldFilter();
    results.innerHTML = '<div class="dm-result-count">Loading...</div>';
    try {
      const data = await API.dmBrowse(type, worldFilter || undefined);
      this._ovRenderResults(data.results, null, type);
    } catch (err) {
      results.innerHTML = `<div class="dm-no-results">Error: ${this.esc(err.message)}</div>`;
    }
  },

  _ovRenderResults(items, query, browseType) {
    const results = this.$('overview-results');
    if (!items.length) {
      results.innerHTML = `<div class="dm-no-results">No results found${query ? ` for "${this.esc(query)}"` : ''}.</div>`;
      return;
    }

    const countLabel = browseType
      ? `${items.length} ${browseType}`
      : `${items.length} result${items.length !== 1 ? 's' : ''}`;

    results.innerHTML = `<div class="dm-result-count">${countLabel}</div>` +
      items.map(item => {
        const worldLabel = this._ovWorldLabel(item.worldIds);
        return `
        <div class="dm-result-card" data-ov-id="${this.esc(item.id)}" data-ov-type="${this.esc(item.type)}">
          <div class="dm-result-header">
            <span class="dm-result-name">${this.esc(item.name)}</span>
            <span class="dm-result-type dm-type-${this.esc(item.type)}">${this.esc(item.type)}</span>
          </div>
          <div class="dm-result-meta">${this.esc(item.meta || '')}${worldLabel ? ` | ${worldLabel}` : ''}</div>
          ${item.description ? `<div class="dm-result-desc">${this.esc(item.description)}</div>` : ''}
        </div>`;
      }).join('');
  },

  _ovShowWelcome() {
    const results = this.$('overview-results');
    if (results) results.innerHTML = `
      <div class="dm-welcome">
        <h3>DM Console</h3>
        <p>Search for anything in the game world. Click a result to inspect, edit, or ask the AI to change it.</p>
        <div class="dm-quick-links">
          <button class="dm-quick-btn" data-ov-quick="players" type="button">All Players</button>
          <button class="dm-quick-btn" data-ov-quick="rooms" type="button">All Rooms</button>
          <button class="dm-quick-btn" data-ov-quick="spells" type="button">All Spells</button>
          <button class="dm-quick-btn" data-ov-quick="items" type="button">All Items</button>
          <button class="dm-quick-btn" data-ov-quick="classes" type="button">All Classes</button>
          <button class="dm-quick-btn" data-ov-quick="races" type="button">All Races</button>
          <button class="dm-quick-btn" data-ov-quick="monsters" type="button">All Monsters</button>
          <button class="dm-quick-btn" data-ov-quick="quests" type="button">All Quests</button>
        </div>
      </div>`;
  },

  ovSelectItem(item, type) {
    this._ovSelectedItem = item;
    this._ovSelectedType = type;
    this._ovJsonMode = false;

    const panel = this.$('overview-detail-panel');
    if (!panel) return;

    // Highlight selected card
    document.querySelectorAll('#overview-results .dm-result-card').forEach(c => c.classList.remove('selected'));
    const card = document.querySelector(`[data-ov-id="${item.id}"][data-ov-type="${type}"]`);
    if (card) card.classList.add('selected');

    const displayType = type.charAt(0).toUpperCase() + type.slice(1);
    const cardRows = this._dmBuildCardRows(item, type);
    const descHtml = item.description ? `<div class="dm-detail-desc">${this.esc(item.description)}</div>` : '';

    // Player-specific action buttons + inline quick-actions
    const playerActions = type === 'player' ? `
      <div class="dm-player-actions">
        <button class="btn btn-primary btn-sm" id="btn-ov-play" title="Play as this character (switches to user view)"><span class="btn-icon">&#9654;</span> Play</button>
        <button class="btn btn-primary btn-sm" id="btn-ov-impersonate" title="Open inline chat as this player"><span class="btn-icon">&#128483;</span> Impersonate</button>
        <button class="btn btn-secondary btn-sm" id="btn-ov-smoke" title="Run look/stats/inventory/help"><span class="btn-icon">&#9881;</span> Smoke Test</button>
        <button class="btn btn-secondary btn-sm" id="btn-ov-teleport-spawn" title="Teleport to spawn room"><span class="btn-icon">&#8634;</span> To Spawn</button>
        <button class="btn btn-secondary btn-sm" id="btn-ov-discord-msg" title="Send Discord message"${item.discordId ? '' : ' disabled'}><span class="btn-icon">&#9993;</span> Discord</button>
        <button class="btn btn-accent btn-sm" id="btn-ov-add-item" title="Grant item from registry"><span class="btn-icon">+</span> Item</button>
      </div>
      <div class="dm-impersonate-panel hidden" id="ov-impersonate-panel">
        <div class="dm-impersonate-header">Playing as <strong>${this.esc(item.name || item.id)}</strong> <button class="dm-icon-btn" id="btn-ov-impersonate-close" title="Close">&#x2715;</button></div>
        <div class="dm-impersonate-log" id="ov-impersonate-log"></div>
        <div class="dm-impersonate-input-row">
          <input type="text" id="ov-impersonate-input" placeholder="Type a command as ${this.esc(item.name || 'player')}..." autocomplete="off" />
          <button class="btn btn-primary btn-sm" id="btn-ov-impersonate-send">Send</button>
        </div>
        <div class="dm-impersonate-quick">
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="look">look</button>
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="inventory">inv</button>
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="journal">journal</button>
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="stats">stats</button>
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="hint">hint</button>
          <button class="btn btn-secondary btn-xs imp-qcmd" data-cmd="lorebook">lore</button>
        </div>
      </div>
      <div class="dm-item-picker hidden" id="ov-item-picker">
        <div class="dm-item-picker-row">
          <input type="text" id="ov-item-search" placeholder="Search items..." autocomplete="off" />
          <select id="ov-item-dest"><option value="inventory">Inventory</option><option value="equip">Equip</option></select>
        </div>
        <div class="dm-item-picker-list" id="ov-item-list"></div>
      </div>
      <details class="dm-inline-section">
        <summary class="dm-inline-header">Quick Actions</summary>
        <div class="dm-inline-actions">
          <div class="dm-inline-form">
            <label class="dm-inline-label">Run Command</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-cmd-input" placeholder="look, go north, attack goblin..." autocomplete="off" />
              <button class="btn btn-primary btn-sm" id="btn-ov-cmd" type="button">Run</button>
            </div>
          </div>
          <div class="dm-inline-form">
            <label class="dm-inline-label">Adjust Resources</label>
            <div class="dm-inline-row dm-resource-row">
              <div class="dm-res-field"><span>HP</span><input type="number" id="ov-res-hp" value="0" /></div>
              <div class="dm-res-field"><span>MP</span><input type="number" id="ov-res-mp" value="0" /></div>
              <div class="dm-res-field"><span>Gold</span><input type="number" id="ov-res-gold" value="0" /></div>
              <div class="dm-res-field"><span>XP</span><input type="number" id="ov-res-xp" value="0" /></div>
              <div class="dm-res-field"><span>Lv</span><input type="number" id="ov-res-level" value="0" /></div>
              <button class="btn btn-primary btn-sm" id="btn-ov-res" type="button">Apply</button>
            </div>
          </div>
          <div class="dm-inline-form">
            <label class="dm-inline-label">Teleport</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-tp-room" placeholder="Room ID (e.g. tavern, spawn)" autocomplete="off" />
              <button class="btn btn-primary btn-sm" id="btn-ov-tp" type="button">Go</button>
            </div>
          </div>
          <div class="dm-inline-form">
            <label class="dm-inline-label">Apply Status</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-status-name" placeholder="Status name" autocomplete="off" />
              <select id="ov-status-type"><option value="Buff">Buff</option><option value="Debuff">Debuff</option><option value="Poison">Poison</option><option value="Regen">Regen</option><option value="Stun">Stun</option></select>
              <input type="number" id="ov-status-turns" value="3" min="1" style="width:3.5rem" title="Turns" />
              <button class="btn btn-primary btn-sm" id="btn-ov-status" type="button">Apply</button>
            </div>
          </div>
          <div class="dm-inline-form">
            <label class="dm-inline-label">Grant Custom Item</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-grant-name" placeholder="Item name" autocomplete="off" />
              <select id="ov-grant-type"><option value="Misc">Misc</option><option value="Weapon">Weapon</option><option value="Armor">Armor</option><option value="Potion">Potion</option><option value="Key">Key</option><option value="QuestItem">Quest</option></select>
              <button class="btn btn-primary btn-sm" id="btn-ov-grant" type="button">Grant</button>
            </div>
          </div>
        </div>
      </details>` : '';

    // Room-specific inline actions
    const roomActions = type === 'room' ? `
      <details class="dm-inline-section">
        <summary class="dm-inline-header">Quick Actions</summary>
        <div class="dm-inline-actions">
          <div class="dm-inline-form">
            <label class="dm-inline-label">Add Item to Room</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-room-item-name" placeholder="Item name" autocomplete="off" />
              <select id="ov-room-item-type"><option value="Misc">Misc</option><option value="Weapon">Weapon</option><option value="Armor">Armor</option><option value="Potion">Potion</option><option value="Key">Key</option><option value="QuestItem">Quest</option></select>
              <button class="btn btn-primary btn-sm" id="btn-ov-room-item" type="button">Add</button>
            </div>
          </div>
          <div class="dm-inline-form">
            <label class="dm-inline-label">Add NPC to Room</label>
            <div class="dm-inline-row">
              <input type="text" id="ov-room-npc-name" placeholder="NPC name" autocomplete="off" />
              <label class="remember-toggle" style="margin:0"><input type="checkbox" id="ov-room-npc-hostile" /><span>Hostile</span></label>
              <button class="btn btn-primary btn-sm" id="btn-ov-room-npc" type="button">Add</button>
            </div>
          </div>
        </div>
      </details>` : '';

    panel.innerHTML = `
      <div class="dm-detail-header">
        <div class="dm-detail-header-left">
          <span class="dm-detail-title">${this.esc(item.name || item.id)}</span>
          <span class="dm-detail-type-badge dm-type-${type}">${displayType}</span>
        </div>
        <div class="dm-detail-actions-top">
          <button class="dm-icon-btn" id="btn-ov-toggle-json" title="Edit raw JSON">{ }</button>
          <button class="dm-icon-btn dm-icon-danger" id="btn-ov-delete" title="Delete">&#x2715;</button>
        </div>
      </div>
      ${playerActions}
      ${roomActions}
      ${descHtml}
      <div class="dm-detail-card">
        <table>${cardRows}</table>
      </div>
      <div class="dm-detail-json" id="ov-json-section">
        <textarea id="ov-json-textarea" spellcheck="false">${this.esc(JSON.stringify(item, null, 2))}</textarea>
        <button class="btn btn-primary btn-sm" id="btn-ov-save-json" type="button">Save JSON</button>
      </div>
      <div class="dm-detail-ai">
        <div class="dm-ai-row">
          <input type="text" id="ov-chat-input" placeholder="Ask AI to edit... e.g. 'give them a fire sword', 'set HP to 50'" autocomplete="off" />
          <button class="btn btn-primary btn-sm" id="btn-ov-chat-send" type="button">AI Edit</button>
          <button class="btn btn-primary btn-sm" id="btn-ov-save" type="button">Save</button>
        </div>
        <div class="dm-ai-messages" id="ov-chat-messages"></div>
      </div>
    `;

    // Wire events
    const chatInput = this.$('ov-chat-input');
    if (chatInput) chatInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') this._ovSendChat(); });
    this.$('btn-ov-chat-send')?.addEventListener('click', () => this._ovSendChat());
    this.$('btn-ov-save')?.addEventListener('click', () => this._ovSave());
    this.$('btn-ov-save-json')?.addEventListener('click', () => { this._ovJsonMode = true; this._ovSave(); });
    this.$('btn-ov-toggle-json')?.addEventListener('click', () => this._ovToggleJson());
    this.$('btn-ov-delete')?.addEventListener('click', () => this._ovDelete());
    this.$('btn-ov-play')?.addEventListener('click', () => {
      if (this._ovSelectedItem?.id) {
        document.dispatchEvent(new CustomEvent('overview-play-player', { detail: { playerId: this._ovSelectedItem.id } }));
      }
    });
    this.$('btn-ov-impersonate')?.addEventListener('click', () => this._ovToggleImpersonate());
    this.$('btn-ov-impersonate-close')?.addEventListener('click', () => this._ovToggleImpersonate(false));
    this.$('btn-ov-impersonate-send')?.addEventListener('click', () => this._ovImpersonateSend());
    this.$('ov-impersonate-input')?.addEventListener('keydown', (e) => { if (e.key === 'Enter') this._ovImpersonateSend(); });
    panel.querySelectorAll('.imp-qcmd').forEach((btn) => {
      btn.addEventListener('click', () => {
        const inp = this.$('ov-impersonate-input');
        if (inp) { inp.value = btn.dataset.cmd; this._ovImpersonateSend(); }
      });
    });
    this.$('btn-ov-smoke')?.addEventListener('click', () => {
      if (this._ovSelectedItem?.id) {
        document.dispatchEvent(new CustomEvent('overview-smoke-player', { detail: { playerId: this._ovSelectedItem.id } }));
      }
    });
    this.$('btn-ov-teleport-spawn')?.addEventListener('click', () => {
      if (this._ovSelectedItem?.id) {
        document.dispatchEvent(new CustomEvent('overview-teleport-spawn', { detail: { playerId: this._ovSelectedItem.id } }));
      }
    });
    this.$('btn-ov-discord-msg')?.addEventListener('click', () => {
      if (this._ovSelectedItem?.id) {
        const msg = prompt('Message to send via Discord:');
        if (msg?.trim()) {
          document.dispatchEvent(new CustomEvent('overview-discord-msg', { detail: { playerId: this._ovSelectedItem.id, message: msg.trim() } }));
        }
      }
    });
    this.$('btn-ov-add-item')?.addEventListener('click', () => this._ovToggleItemPicker());
    this.$('ov-item-search')?.addEventListener('input', (e) => this._ovFilterItems(e.target.value));
    panel.querySelectorAll('[data-ov-item-action]').forEach((button) => {
      button.addEventListener('click', () => {
        const itemId = button.dataset.ovItemId;
        const action = button.dataset.ovItemAction;
        if (!this._ovSelectedItem?.id || !itemId || !action) return;
        document.dispatchEvent(new CustomEvent('overview-item-action', {
          detail: { playerId: this._ovSelectedItem.id, itemId, action }
        }));
      });
    });

    // ── Inline player quick-actions ──
    const pid = this._ovSelectedItem?.id;
    if (type === 'player' && pid) {
      const cmdInput = this.$('ov-cmd-input');
      if (cmdInput) {
        cmdInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') this.$('btn-ov-cmd')?.click(); });
      }
      this.$('btn-ov-cmd')?.addEventListener('click', () => {
        const cmd = this.$('ov-cmd-input')?.value?.trim();
        if (cmd) document.dispatchEvent(new CustomEvent('overview-run-command', { detail: { playerId: pid, command: cmd } }));
      });
      this.$('btn-ov-res')?.addEventListener('click', () => {
        document.dispatchEvent(new CustomEvent('overview-adjust-resources', { detail: {
          playerId: pid,
          hpDelta: parseInt(this.$('ov-res-hp')?.value) || 0,
          mpDelta: parseInt(this.$('ov-res-mp')?.value) || 0,
          goldDelta: parseInt(this.$('ov-res-gold')?.value) || 0,
          xpDelta: parseInt(this.$('ov-res-xp')?.value) || 0,
          levelDelta: parseInt(this.$('ov-res-level')?.value) || 0
        }}));
      });
      this.$('btn-ov-tp')?.addEventListener('click', () => {
        const roomId = this.$('ov-tp-room')?.value?.trim();
        if (roomId) document.dispatchEvent(new CustomEvent('overview-teleport', { detail: { playerId: pid, roomId } }));
      });
      this.$('ov-tp-room')?.addEventListener('keydown', (e) => { if (e.key === 'Enter') this.$('btn-ov-tp')?.click(); });
      this.$('btn-ov-status')?.addEventListener('click', () => {
        const name = this.$('ov-status-name')?.value?.trim();
        if (name) document.dispatchEvent(new CustomEvent('overview-apply-status', { detail: {
          playerId: pid, name,
          type: this.$('ov-status-type')?.value || 'Buff',
          remainingTurns: parseInt(this.$('ov-status-turns')?.value) || 3
        }}));
      });
      this.$('btn-ov-grant')?.addEventListener('click', () => {
        const name = this.$('ov-grant-name')?.value?.trim();
        if (name) document.dispatchEvent(new CustomEvent('overview-grant-item', { detail: {
          playerId: pid, name,
          type: this.$('ov-grant-type')?.value || 'Misc'
        }}));
      });
    }

    // ── Inline room quick-actions ──
    const rid = this._ovSelectedItem?.id;
    if (type === 'room' && rid) {
      this.$('btn-ov-room-item')?.addEventListener('click', () => {
        const name = this.$('ov-room-item-name')?.value?.trim();
        if (name) document.dispatchEvent(new CustomEvent('overview-room-add-item', { detail: {
          roomId: rid, name, type: this.$('ov-room-item-type')?.value || 'Misc'
        }}));
      });
      this.$('btn-ov-room-npc')?.addEventListener('click', () => {
        const name = this.$('ov-room-npc-name')?.value?.trim();
        if (name) document.dispatchEvent(new CustomEvent('overview-room-add-npc', { detail: {
          roomId: rid, name, isHostile: this.$('ov-room-npc-hostile')?.checked || false
        }}));
      });
    }
  },

  _ovItemCache: null,

  async _ovToggleItemPicker() {
    const picker = this.$('ov-item-picker');
    if (!picker) return;
    const wasHidden = picker.classList.contains('hidden');
    picker.classList.toggle('hidden');
    if (wasHidden) {
      if (!this._ovItemCache) {
        const listEl = this.$('ov-item-list');
        if (listEl) listEl.innerHTML = '<div class="dm-item-picker-loading">Loading items...</div>';
        try {
          this._ovItemCache = await API.getRegistry('items');
        } catch (err) {
          if (listEl) listEl.innerHTML = '<div class="dm-item-picker-loading">Failed to load items.</div>';
          return;
        }
      }
      this.$('ov-item-search').value = '';
      this._ovRenderItemList(this._ovItemCache);
      this.$('ov-item-search')?.focus();
    }
  },

  _ovFilterItems(query) {
    if (!this._ovItemCache) return;
    const q = query.toLowerCase().trim();
    const filtered = q ? this._ovItemCache.filter(i =>
      i.name.toLowerCase().includes(q) || (i.type || '').toLowerCase().includes(q)
      || (i.tags || []).some(t => t.toLowerCase().includes(q))
    ) : this._ovItemCache;
    this._ovRenderItemList(filtered);
  },

  _ovRenderItemList(items) {
    const listEl = this.$('ov-item-list');
    if (!listEl) return;
    if (!items.length) { listEl.innerHTML = '<div class="dm-item-picker-loading">No items found.</div>'; return; }
    const typeIcon = (t) => {
      const map = { Weapon: '⚔', Armor: '🛡', Shield: '🛡', Helmet: '⛑', Potion: '🧪', Ring: '💍', Amulet: '📿', Scroll: '📜', Key: '🔑' };
      return map[t] || '•';
    };
    listEl.innerHTML = items.slice(0, 50).map(i => `
      <div class="dm-item-pick" data-item-id="${this.esc(i.id)}" title="${this.esc(i.description || '')}">
        <span class="dm-item-pick-icon">${typeIcon(i.type)}</span>
        <span class="dm-item-pick-name">${this.esc(i.name)}</span>
        <span class="dm-item-pick-meta">${this.esc(i.type || 'Misc')}${i.isTwoHanded ? ' (2H)' : ''}${i.damageDice ? ' ' + i.damageDice : ''}${i.armorValue ? ' AC+' + i.armorValue : ''}${i.value ? ' ' + i.value + 'g' : ''}</span>
      </div>
    `).join('');
    listEl.querySelectorAll('.dm-item-pick').forEach(el => {
      el.addEventListener('click', () => {
        const itemId = el.dataset.itemId;
        const template = this._ovItemCache?.find(i => i.id === itemId);
        if (!template || !this._ovSelectedItem?.id) return;
        const dest = this.$('ov-item-dest')?.value || 'inventory';
        document.dispatchEvent(new CustomEvent('overview-add-item', {
          detail: { playerId: this._ovSelectedItem.id, template, autoEquip: dest === 'equip' }
        }));
        this.$('ov-item-picker')?.classList.add('hidden');
      });
    });
  },

  _ovToPlayerItemEntry(item, slot, fallbackName) {
    const objectItem = item && typeof item === 'object' ? item : null;
    return {
      slot,
      itemId: objectItem?.id || '',
      name: objectItem?.name || objectItem?.title || objectItem?.id || (typeof item === 'string' ? item : fallbackName),
      quantity: Number.isFinite(objectItem?.quantity) && objectItem.quantity > 0 ? objectItem.quantity : 1
    };
  },

  _ovGetEquipmentEntries(equipment) {
    const entries = [];
    const push = (slot, item, fallbackName = slot) => {
      if (item == null) return;
      entries.push(this._ovToPlayerItemEntry(item, slot, fallbackName));
    };
    const pushMany = (slot, values) => {
      if (!Array.isArray(values)) return;
      values.forEach((value, index) => {
        const label = values.length > 1 ? `${slot} ${index + 1}` : slot;
        push(label, value, `${slot} ${index + 1}`);
      });
    };

    push('Main Hand', equipment?.mainHand);
    push('Off Hand', equipment?.offHand);
    push('Armor', equipment?.armor);
    push('Helmet', equipment?.helmet);
    push('Cloak', equipment?.cloak);
    push('Boots', equipment?.boots);
    push('Gloves', equipment?.gloves);
    pushMany('Ring', equipment?.rings);
    pushMany('Amulet', equipment?.amulets);
    pushMany('Bracelet', equipment?.bracelets);

    return entries.filter(entry => entry.name);
  },

  _ovGetInventoryEntries(items) {
    if (!Array.isArray(items)) return [];
    return items
      .map((item, index) => this._ovToPlayerItemEntry(item, '', `Item ${index + 1}`))
      .filter(entry => entry.name);
  },

  _ovRenderPlayerItemChips(entries, action, emptyText) {
    if (!entries.length) return `<span class="dm-card-placeholder">${this.esc(emptyText)}</span>`;
    const actionTitle = action === 'unequip' ? 'Unequip to inventory' : 'Remove from inventory';
    return `<div class="dm-inline-item-list">${entries.map((entry) => `
      <span class="dm-inline-item">
        ${entry.slot ? `<span class="dm-inline-slot">${this.esc(entry.slot)}</span>` : ''}
        <span class="dm-inline-item-label">${this.esc(entry.name)}</span>
        ${entry.quantity > 1 ? `<span class="dm-inline-item-qty">x${this.esc(String(entry.quantity))}</span>` : ''}
        ${entry.itemId ? `<button type="button" class="dm-inline-action" data-ov-item-action="${this.esc(action)}" data-ov-item-id="${this.esc(entry.itemId)}" title="${this.esc(actionTitle)}">&#x00d7;</button>` : ''}
      </span>
    `).join('')}</div>`;
  },

  _ovToggleImpersonate(show) {
    const panel = this.$('ov-impersonate-panel');
    if (!panel) return;
    const isVisible = show !== undefined ? show : panel.classList.contains('hidden');
    panel.classList.toggle('hidden', !isVisible);
    if (isVisible) {
      this.$('ov-impersonate-input')?.focus();
    }
  },

  async _ovImpersonateSend() {
    const input = this.$('ov-impersonate-input');
    const log = this.$('ov-impersonate-log');
    if (!input || !log || !this._ovSelectedItem?.id) return;
    const cmd = input.value.trim();
    if (!cmd) return;
    input.value = '';

    log.innerHTML += `<div class="imp-msg player">&gt; ${this.esc(cmd)}</div>`;
    log.scrollTop = log.scrollHeight;

    try {
      const result = await API.sendCommand(this._ovSelectedItem.id, cmd);
      const narration = result.narration || result.mechanicalSummary || '(no response)';
      const mechHtml = result.mechanicalSummary ? `<div class="imp-msg mech">${this.esc(result.mechanicalSummary)}</div>` : '';
      const narrHtml = result.narration ? `<div class="imp-msg narr">${this.esc(result.narration)}</div>` : '';
      log.innerHTML += mechHtml + narrHtml;
    } catch (err) {
      log.innerHTML += `<div class="imp-msg error">Error: ${this.esc(err.message)}</div>`;
    }
    log.scrollTop = log.scrollHeight;
  },

  _ovToggleJson() {
    const section = this.$('ov-json-section');
    const btn = this.$('btn-ov-toggle-json');
    if (!section) return;
    this._ovJsonMode = !this._ovJsonMode;
    section.classList.toggle('visible', this._ovJsonMode);
    if (btn) btn.textContent = this._ovJsonMode ? 'Card View' : '{ }';
    if (this._ovJsonMode && this._ovSelectedItem) {
      const ta = this.$('ov-json-textarea');
      if (ta) ta.value = JSON.stringify(this._ovSelectedItem, null, 2);
    }
  },

  async _ovSendChat() {
    const input = this.$('ov-chat-input');
    const messages = this.$('ov-chat-messages');
    if (!input || !input.value.trim() || !this._ovSelectedItem) return;

    const userMsg = input.value.trim();
    input.value = '';
    messages.innerHTML += `<div class="dm-ai-msg user">${this.esc(userMsg)}</div>`;
    messages.innerHTML += `<div class="dm-ai-msg ai loading" id="ov-chat-loading">Thinking...</div>`;
    messages.scrollTop = messages.scrollHeight;
    messages.classList.remove('hidden');

    try {
      const type = this._ovSelectedType;
      const existingJson = JSON.stringify(this._ovSelectedItem);
      const result = await API.generateContent(type, userMsg, existingJson);

      const loadingEl = this.$('ov-chat-loading');
      if (loadingEl) loadingEl.remove();

      if (result.json) {
        try {
          const updated = JSON.parse(result.json);
          this._ovSelectedItem = updated;
          const cardSection = document.querySelector('#overview-detail-panel .dm-detail-card');
          if (cardSection) cardSection.innerHTML = `<table>${this._dmBuildCardRows(updated, type)}</table>`;
          const ta = this.$('ov-json-textarea');
          if (ta) ta.value = JSON.stringify(updated, null, 2);
          messages.innerHTML += `<div class="dm-ai-msg ai">Updated! Review changes above, then Save.</div>`;
        } catch {
          messages.innerHTML += `<div class="dm-ai-msg ai">Got a response but couldn't parse it. Try again?</div>`;
        }
      }
      messages.scrollTop = messages.scrollHeight;
    } catch (err) {
      const loadingEl = this.$('ov-chat-loading');
      if (loadingEl) loadingEl.remove();
      messages.innerHTML += `<div class="dm-ai-msg system">Error: ${this.esc(err.message)}</div>`;
      messages.scrollTop = messages.scrollHeight;
    }
  },

  async _ovSave() {
    if (!this._ovSelectedItem || !this._ovSelectedType) return;

    if (this._ovJsonMode) {
      const ta = this.$('ov-json-textarea');
      if (ta) {
        try {
          this._ovSelectedItem = JSON.parse(ta.value);
        } catch (err) {
          alert('Invalid JSON: ' + err.message);
          return;
        }
      }
    }

    const type = this._ovSelectedType;
    const registryTypesMap = { spell: 'spells', item: 'items', class: 'classes', race: 'races', monster: 'monsters', quest: 'quests', lore_entry: 'lore_entries', narrator_preset: 'narrator_presets' };
    try {
      if (registryTypesMap[type]) {
        await API.upsertRegistryEntry(registryTypesMap[type], this._ovSelectedItem);
      } else if (type === 'player') {
        await API.updatePlayer(this._ovSelectedItem.id, this._ovSelectedItem);
      } else if (type === 'room') {
        await API.updateRoom(this._ovSelectedItem.id, this._ovSelectedItem);
      }
      const messages = this.$('ov-chat-messages');
      if (messages) {
        messages.innerHTML += `<div class="dm-ai-msg system">Saved.</div>`;
        messages.classList.remove('hidden');
        messages.scrollTop = messages.scrollHeight;
      }
    } catch (err) {
      alert('Save failed: ' + err.message);
    }
  },

  async _ovDelete() {
    if (!this._ovSelectedItem || !this._ovSelectedType) return;
    if (!confirm(`Delete "${this._ovSelectedItem.name || this._ovSelectedItem.id}"?`)) return;
    const type = this._ovSelectedType;
    try {
      const deleteTypeMap = { spell: 'spells', item: 'items', class: 'classes', race: 'races', monster: 'monsters', quest: 'quests', lore_entry: 'lore_entries', narrator_preset: 'narrator_presets' };
      if (deleteTypeMap[type]) {
        await API.deleteRegistryEntry(deleteTypeMap[type], this._ovSelectedItem.id);
      } else if (type === 'player') {
        await API.deletePlayer(this._ovSelectedItem.id);
      } else if (type === 'room') {
        await API.deleteRoom(this._ovSelectedItem.id);
      }
      const panel = this.$('overview-detail-panel');
      if (panel) panel.innerHTML = '<div class="dm-detail-empty"><p>Item deleted.</p></div>';
      this._ovSelectedItem = null;
    } catch (err) {
      alert('Delete failed: ' + err.message);
    }
  },

  async _ovFetchAndSelect(id, type) {
    try {
      let item;
      const registryTypes = { spell: 'spells', item: 'items', class: 'classes', race: 'races', monster: 'monsters', quest: 'quests', lore_entry: 'lore_entries', narrator_preset: 'narrator_presets' };
      if (registryTypes[type]) {
        item = await API.getRegistryEntry(registryTypes[type], id);
      } else if (type === 'player') {
        item = await API.getPlayer(id);
      } else if (type === 'room') {
        item = await API.getRoom(id);
      } else if (type === 'npc') {
        const rooms = await API.getRooms();
        for (const rm of rooms) {
          const npc = (rm.npcs || []).find(n => n.id === id);
          if (npc) { item = npc; item._roomId = rm.id; break; }
        }
      }
      if (item) this.ovSelectItem(item, type);
    } catch (err) {
      console.error('Overview: failed to fetch item', err);
    }
  },

  wireOverviewBrowser() {
    const searchInput = this.$('overview-search-input');
    const resultsEl = this.$('overview-results');
    let debounceTimer = null;

    const triggerSearch = () => {
      const q = searchInput?.value?.trim() || '';
      if (!q) { this._ovShowWelcome(); return; }
      if (q.length < 2) return;
      this.ovSearch(q);
    };

    if (searchInput) {
      searchInput.addEventListener('input', () => {
        clearTimeout(debounceTimer);
        const q = searchInput.value.trim();
        if (!q) { this._ovShowWelcome(); return; }
        debounceTimer = setTimeout(triggerSearch, 250);
      });
      searchInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { clearTimeout(debounceTimer); triggerSearch(); }
      });
    }

    // Re-search when world or type filter changes
    const worldFilter = this.$('overview-world-filter');
    const typeFilter = this.$('overview-type-filter');
    if (worldFilter) worldFilter.addEventListener('change', triggerSearch);
    if (typeFilter) typeFilter.addEventListener('change', triggerSearch);

    if (resultsEl) {
      resultsEl.addEventListener('click', async (e) => {
        // Quick browse buttons
        const quickBtn = e.target.closest('[data-ov-quick]');
        if (quickBtn) {
          this.ovBrowse(quickBtn.dataset.ovQuick);
          return;
        }

        // Result card click — load full object and show detail
        const card = e.target.closest('[data-ov-id]');
        if (card) {
          const id = card.dataset.ovId;
          const type = card.dataset.ovType;
          await this._ovFetchAndSelect(id, type);
        }
      });
    }
  },

  renderAdminPlayers(players, currentPlayerId, session) {
    // No longer renders the entity browser — overview uses search now.
    // Keep for compatibility (still populates select options, portal, etc.)
    this._adminPlayers = players;
    this._adminCurrentPlayerId = currentPlayerId;
  },

  renderRoomCatalogue(rooms) {
    // No longer renders the entity browser — overview uses search now.
  },

  // ── World Management rendering ──

  renderWorldList(worlds, selectedWorldId) {
    const container = this.$('world-list');
    if (!container) return;

    if (!worlds || !worlds.length) {
      container.innerHTML = '<div class="empty-state">No worlds found.</div>';
      return;
    }

    container.innerHTML = worlds.map(w => {
      const isSelected = w.id === selectedWorldId;
      const isDiscordDefault = (w.tags || []).includes('discord-default');
      const badges = [];
      if (isDiscordDefault) badges.push('<span class="world-badge default">⚔ Discord Default</span>');
      badges.push(w.isActive
        ? '<span class="world-badge active">Active</span>'
        : '<span class="world-badge inactive">Inactive</span>');
      if (w.playerCount > 0) badges.push(`<span class="world-badge players">${w.playerCount} player${w.playerCount !== 1 ? 's' : ''}</span>`);
      if (w.portalCount > 0) badges.push(`<span class="world-badge portals">${w.portalCount} portal${w.portalCount !== 1 ? 's' : ''}</span>`);
      if (!isDiscordDefault) badges.push(`<button class="btn btn-secondary btn-sm world-set-default-btn" data-set-default-world="${this.esc(w.id)}" type="button" style="font-size:10px;padding:1px 8px;">Set Default</button>`);

      return `<div class="world-card${isSelected ? ' selected' : ''}" data-world-id="${this.esc(w.id)}">
        <div class="world-card-info">
          <div class="world-card-name">${this.esc(w.name)}</div>
          <div class="world-card-meta">${this.esc(w.id)} | ${w.statCount ?? 0} stats | Created ${new Date(w.createdAt).toLocaleDateString()}</div>
        </div>
        <div class="world-card-badges">${badges.join('')}</div>
      </div>`;
    }).join('');
  },

  renderWorldDetail(world, players) {
    const panel = this.$('world-detail-panel');
    const body = this.$('world-detail-body');
    if (!panel || !body) return;

    if (!world) {
      panel.classList.add('hidden');
      return;
    }

    panel.classList.remove('hidden');
    this.$('world-detail-name').textContent = world.name;

    const playerCount = players ? players.length : 0;
    const portalCount = (world.portals || []).length;
    const statCount = Object.keys(world.rules?.stats || {}).length;
    const tags = world.tags || [];

    body.innerHTML = `
      <div class="world-detail-stats">
        <div class="world-stat-card"><div class="world-stat-value">${playerCount}</div><div class="world-stat-label">Players</div></div>
        <div class="world-stat-card"><div class="world-stat-value">${portalCount}</div><div class="world-stat-label">Portals</div></div>
        <div class="world-stat-card"><div class="world-stat-value">${statCount}</div><div class="world-stat-label">Stats</div></div>
        <div class="world-stat-card"><div class="world-stat-value">${world.isActive ? 'Yes' : 'No'}</div><div class="world-stat-label">Active</div></div>
      </div>
      <div class="world-detail-section">
        <h4>Description</h4>
        <p style="font-size:12px;margin:0;">${this.esc(world.description || 'No description.')}</p>
      </div>
      <div class="world-detail-section">
        <h4>Spawn Room</h4>
        <p style="font-size:12px;margin:0;"><code>${this.esc(world.spawnRoomId || 'spawn')}</code></p>
      </div>
      ${tags.length ? `<div class="world-detail-section">
        <h4>Tags</h4>
        <div class="world-detail-tags">${tags.map(t => `<span class="world-tag">${this.esc(t)}</span>`).join('')}</div>
      </div>` : ''}
      ${playerCount > 0 ? `<div class="world-detail-section">
        <h4 class="collapsible-heading" data-toggle="world-player-list" style="cursor:pointer;user-select:none;">Players in World <span style="font-size:10px;color:var(--dim);font-weight:400;">(${playerCount}) ▾</span></h4>
        <div id="world-player-list" class="portal-player-list" style="max-height:180px;overflow-y:auto;">${players.map(p => `
          <div class="registry-row">
            <div class="registry-meta">
              <div class="registry-name">${this.esc(p.name)}</div>
              <div class="registry-subtext">Lv.${p.level} ${this.esc(p.race || '')} ${this.esc(p.class || '')} | ${this.esc(p.currentRoomId || '?')}</div>
            </div>
          </div>`).join('')}
        </div>
      </div>` : ''}
      <div class="world-detail-section">
        <h4>Narrator Voice</h4>
        <select id="world-default-narrator" style="width:100%;padding:4px 6px;font-size:12px;background:var(--bg-secondary);color:var(--text);border:1px solid var(--border);border-radius:4px;">
          <option value="">System default</option>
        </select>
      </div>
      <div class="world-detail-section">
        <h4>Character Creation Intro</h4>
        <textarea id="world-intro-text" rows="6" style="width:100%;padding:6px;font-size:11px;background:var(--bg-secondary);color:var(--text);border:1px solid var(--border);border-radius:4px;resize:vertical;font-family:inherit;" placeholder="Leave blank for generic intro. Supports Discord markdown.">${this.esc(world.characterCreationIntro || '')}</textarea>
        <div style="display:flex;gap:0.5rem;margin-top:0.35rem;">
          <button class="btn btn-secondary btn-sm" id="world-intro-generate" type="button">AI Generate</button>
          <button class="btn btn-primary btn-sm" id="world-intro-save" type="button">Save Settings</button>
        </div>
        <div id="world-intro-status" style="font-size:11px;margin-top:0.25rem;color:var(--dim);"></div>
      </div>
      <div class="world-detail-section">
        <h4>Stat Definitions <button class="btn btn-secondary btn-sm" id="world-stat-add" type="button" style="margin-left:0.5rem;font-size:10px;padding:1px 8px;">+ Add</button></h4>
        <div id="world-stat-list" style="display:grid;gap:0.25rem;">${statCount > 0 ? Object.entries(world.rules.stats).map(([key, s]) => `
          <div class="registry-row" style="padding:0.3rem 0.5rem;display:flex;align-items:center;justify-content:space-between;" data-stat-key="${this.esc(key)}">
            <div class="registry-meta" style="flex:1;">
              <div class="registry-name">${this.esc(s.display || key)} <span style="font-weight:400;color:var(--dim);">(${this.esc(key)})</span></div>
              <div class="registry-subtext">${this.esc(s.category || '?')} | Range ${s.min}-${s.max} | Base ${s.base}${(s.semanticTags || []).length ? ' | Tags: ' + s.semanticTags.join(', ') : ''}</div>
            </div>
            <div style="display:flex;gap:0.25rem;flex-shrink:0;">
              <button class="btn btn-secondary btn-sm" data-stat-edit="${this.esc(key)}" type="button" style="font-size:10px;padding:1px 6px;">Edit</button>
              <button class="admin-row-delete" data-stat-delete="${this.esc(key)}" type="button" style="font-size:10px;padding:1px 6px;">Del</button>
            </div>
          </div>`).join('') : '<div class="empty-state" style="font-size:11px;">No stats defined. Click + Add to create one.</div>'}
        </div>
      </div>
    `;
  },

  renderPortalList(portals, worlds) {
    const panel = this.$('portal-manager-panel');
    const container = this.$('portal-list');
    if (!panel || !container) return;

    if (!portals) {
      panel.classList.add('hidden');
      return;
    }

    panel.classList.remove('hidden');
    const worldMap = {};
    (worlds || []).forEach(w => { worldMap[w.id] = w.name; });

    if (!portals.length) {
      container.innerHTML = '<div class="empty-state">No portals configured for this world.</div>';
      return;
    }

    container.innerHTML = portals.map(p => {
      const destName = worldMap[p.destinationWorldId] || p.destinationWorldId;
      const badges = [];
      if (p.isAdminOnly) badges.push('<span class="world-badge inactive">Admin Only</span>');
      if (p.minLevel) badges.push(`<span class="world-badge players">Lv.${p.minLevel}+</span>`);

      return `<div class="portal-card" data-portal-id="${this.esc(p.id)}">
        <div class="portal-card-info">
          <div class="portal-card-route">${this.esc(p.sourceRoomId)} → ${this.esc(destName)}${p.destinationRoomId ? ' (' + this.esc(p.destinationRoomId) + ')' : ''}</div>
          <div class="portal-card-meta">${p.description ? this.esc(p.description) : 'No description'} ${badges.join(' ')}</div>
        </div>
        <div class="portal-card-actions">
          <button class="btn btn-secondary btn-sm" data-portal-edit="${this.esc(p.id)}" type="button">Edit</button>
          <button class="admin-row-delete" data-portal-delete="${this.esc(p.id)}" type="button">Del</button>
        </div>
      </div>`;
    }).join('');
  },

  populateWorldSelects(worlds, excludeWorldId) {
    const selects = [
      this.$('transfer-world-select'),
      this.$('portal-form-dest-world')
    ];
    selects.forEach(sel => {
      if (!sel) return;
      const current = sel.value;
      sel.innerHTML = '<option value="">Select world...</option>' +
        (worlds || [])
          .filter(w => w.id !== excludeWorldId)
          .map(w => `<option value="${this.esc(w.id)}">${this.esc(w.name)}${w.isActive ? '' : ' (inactive)'}</option>`)
          .join('');
      if (current) sel.value = current;
    });
  },

  populateTransferPlayerSelect(players) {
    const sel = this.$('transfer-player-select');
    if (!sel) return;
    const current = sel.value;
    sel.innerHTML = '<option value="">Select player...</option>' +
      (players || []).map(p => `<option value="${this.esc(p.id)}">${this.esc(p.name)} (${this.esc(p.id)})</option>`).join('');
    if (current) sel.value = current;
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

  // Stat modifier baseline — fetched from server config. null = hide modifiers.
  _statModifierBaseline: undefined,

  async loadGameConfig() {
    try {
      const cfg = await API.getGameConfig();
      this._statModifierBaseline = cfg.statModifierBaseline ?? null;
    } catch { this._statModifierBaseline = null; }
  },

  getStatEntries(player) {
    const baseline = this._statModifierBaseline;
    const preferred = KNOWN_ABILITY_KEYS
      .filter((key) => this.isScalar(player[key]))
      .map((key) => ({
        label: key.toUpperCase(),
        value: player[key],
        modifier: baseline != null && typeof player[key] === 'number'
          ? Math.trunc((player[key] - baseline) / 2)
          : null
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

  formatNarration(text) {
    let safe = this.esc(text);
    safe = safe.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    return safe;
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

  _mechanicalSubsumedByNarration(normalizedMechanical, normalizedNarration) {
    // Check if the mechanical summary's key sentences are already covered by the narration.
    // Split mechanical into sentences and check if most are contained in narration.
    const sentences = normalizedMechanical.split(/[.!?]+/).map((s) => s.trim()).filter((s) => s.length > 15);
    if (sentences.length === 0) return false;
    const covered = sentences.filter((sentence) => normalizedNarration.includes(sentence));
    return covered.length >= sentences.length * 0.6;
  },

  _stripRoomMetadata(text) {
    if (!text) {
      return '';
    }

    const room = this._roomContext;
    const roomName = room?.name ? this._normalizeRoomLine(room.name) : '';
    const roomDescription = room?.description ? this._normalizeRoomLine(room.description) : '';
    const roomNpcNames = new Set((room?.npcs || []).map((npc) => this._normalizeRoomLine(npc.name)).filter(Boolean));
    const roomItemNames = new Set((room?.items || []).map((item) => this._normalizeRoomLine(item.name)).filter(Boolean));
    const roomExitNames = new Set(Object.keys(room?.exits || {}).map((exit) => this._normalizeRoomLine(exit)).filter(Boolean));
    const lines = String(text).split('\n');
    const metadataIndexes = lines
      .map((line, index) => ({ line: this._normalizeRoomLine(line), index }))
      .filter(({ line }) => /^(Exits?|You see|Items?|NPCs?|Creatures?|Objects?|Nearby)\s*:/i.test(line))
      .map(({ index }) => index);
    const looksLikeRoomBlock = metadataIndexes.length >= 2 && metadataIndexes[0] <= 2;
    const filtered = [];
    let metadataSection = '';

    const normalizeListLine = (value) => this._normalizeRoomLine(
      value
        .replace(/^\s*(?:[-*•!$]+|\d+[.)])\s*/, '')
        .replace(/\s+\(x\d+\)$/i, '')
    );
    const isMetadataListLine = (value, pool) => {
      const normalizedValue = normalizeListLine(value);
      if (!normalizedValue) {
        return false;
      }

      if (pool.has(normalizedValue)) {
        return true;
      }

      const commaParts = normalizedValue.split(',').map((part) => this._normalizeRoomLine(part)).filter(Boolean);
      return commaParts.length > 0 && commaParts.every((part) => pool.has(part));
    };

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

      if (/^(NPCs?|Creatures?|Objects?|Items?)\s*:/i.test(normalized)) {
        metadataSection = 'entities';
        continue;
      }

      if (/^(Exits?|You see|Nearby)\s*:/i.test(normalized)) {
        metadataSection = 'exits';
        continue;
      }

      if (metadataSection === 'entities') {
        if (isMetadataListLine(trimmed, roomNpcNames) || isMetadataListLine(trimmed, roomItemNames)) {
          continue;
        }
        metadataSection = '';
      }

      if (metadataSection === 'exits') {
        if (isMetadataListLine(trimmed, roomExitNames)) {
          continue;
        }
        metadataSection = '';
      }

      if (roomName && normalized === roomName) {
        continue;
      }

      if (roomDescription && normalized === roomDescription) {
        continue;
      }

      if (isMetadataListLine(trimmed, roomNpcNames) || isMetadataListLine(trimmed, roomItemNames)) {
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

      // Strip standalone entity marker lines (engine-generated NPC/item list lines from room dumps)
      // Matches: "$ NpcName" (shopkeeper), "! NpcName" (hostile) — engine-specific markers
      if (/^[$!]\s+\S/.test(trimmed)) {
        continue;
      }

      // Strip short entity list lines: "- NpcName", "* ItemName (x2)" from room dumps
      // Only short lines (< 50 chars) starting with - or * followed by a capitalized word
      if (/^[-*]\s+[A-Z]/.test(trimmed) && trimmed.length < 50) {
        continue;
      }

      // Strip exit summary lines from room dumps
      if (/^\*\*Exits?:\*\*/i.test(trimmed)) {
        continue;
      }

      filtered.push(line);
    }

    // Strip trailing inline entity lists (e.g., "...gates. - NpcA - NpcB * ItemA * ItemB")
    let result = filtered.join('\n').trim();
    result = result.replace(/(?:\s[-*$!]\s[A-Z][A-Za-z']+(?:\s[A-Za-z']+)*){2,}\s*$/, '');
    return result.trim();
  },

  _normalizeRoomLine(text) {
    return String(text || '')
      .trim()
      .replace(/\*\*(.*?)\*\*/g, '$1')
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

  _dmBuildCardRows(item, type) {
    const row = (label, value, cls) => value != null && value !== ''
      ? `<tr><td class="dm-card-label">${this.esc(label)}</td><td class="${cls || ''}">${this.esc(String(value))}</td></tr>` : '';
    const rawRow = (label, html, cls) => html != null && html !== ''
      ? `<tr><td class="dm-card-label">${this.esc(label)}</td><td class="${cls || ''}">${html}</td></tr>` : '';
    const statRow = (label, value, color) => value != null
      ? `<td><span class="dm-stat-label">${this.esc(label)}</span><span class="dm-stat-val" style="color:${color || 'var(--text)'}">${this.esc(String(value))}</span></td>` : '';

    switch (type) {
      case 'spell':
        return row('School', item.school) + row('Mana Cost', item.manaCost, 'dm-val-mp') + row('Power Level', item.powerLevel, 'dm-val-accent')
          + row('Damage', item.damageDice, 'dm-val-hp') + row('Healing', item.healDice, 'dm-val-heal') + row('Range', item.range)
          + row('Required Level', item.requiredLevel) + row('Classes', (item.requiredClasses || []).join(', ') || 'All')
          + row('Duration', item.duration) + row('Status Effect', item.statusEffect)
          + row('Tags', (item.tags || []).join(', '));
      case 'item':
        return row('Type', item.type) + row('Rarity', item.rarity, 'dm-val-accent') + row('Value', `${item.value}g`, 'dm-val-gold')
          + row('Damage', item.damageDice, 'dm-val-hp') + row('Armor', item.armorValue) + row('Effect', item.effect)
          + row('Equippable', item.isEquippable ? 'Yes' : 'No') + row('Consumable', item.isConsumable ? 'Yes' : 'No')
          + row('Tags', (item.tags || []).join(', '));
      case 'class':
        return row('Hit Die', item.hitDie, 'dm-val-accent') + row('Primary Stat', item.primaryStat) + row('Caster', item.canCastSpells ? 'Yes' : 'No')
          + row('MP Bonus', item.baseMpBonus, 'dm-val-mp') + row('Spells', (item.spellList || []).length + ' available')
          + row('Tags', (item.tags || []).join(', '));
      case 'race':
        return row('Traits', (item.traits || []).join(', '))
          + row('Stat Bonuses', Object.entries(item.statBonuses || {}).map(([k,v]) => `${k.toUpperCase()} +${v}`).join(', '), 'dm-val-accent')
          + row('Tags', (item.tags || []).join(', '));
      case 'room':
        return row('ID', item.id) + row('Exits', Object.entries(item.exits || {}).map(([d,t]) => `${d} -> ${t}`).join(', '))
          + row('NPCs', (item.npcs || []).map(n => n.name).join(', '))
          + row('Items', (item.items || []).map(i => i.name).join(', '))
          + row('Tags', (item.environmentTags || []).join(', '));
      case 'player': {
        const equipmentEntries = this._ovGetEquipmentEntries(item.equipment || {});
        const inventoryEntries = this._ovGetInventoryEntries(item.inventory || []);
        const equipmentSummary = equipmentEntries.length
          ? equipmentEntries.map(entry => `${entry.slot}: ${entry.name}`).join(' | ')
          : 'None';
        const inventorySummary = `${inventoryEntries.length} item${inventoryEntries.length === 1 ? '' : 's'}`;
        const base = row('Race', item.race) + row('Class', item.class)
          + row('Level', item.level, 'dm-val-accent') + row('HP', `${item.hp}/${item.maxHp}`, 'dm-val-hp') + row('MP', `${item.mp}/${item.maxMp}`, 'dm-val-mp')
          + row('Gold', item.gold, 'dm-val-gold') + row('XP', item.xp, 'dm-val-xp') + row('Room', item.currentRoomId)
          + row('World', item.activeWorldId) + row('Equipment', equipmentSummary)
          + row('Inventory', inventorySummary) + row('Discord', item.discordId || 'not linked')
          + rawRow('Equipped Items', this._ovRenderPlayerItemChips(equipmentEntries, 'unequip', 'No equipment equipped.'))
          + rawRow('Inventory Items', this._ovRenderPlayerItemChips(inventoryEntries, 'remove', 'Inventory empty.'));
        const stats = `<tr><td class="dm-card-label">Stats</td><td class="dm-stat-grid">` +
          `<table class="dm-stats-inline"><tr>${statRow('STR', item.str)}${statRow('DEX', item.dex)}${statRow('CON', item.con)}</tr>` +
          `<tr>${statRow('INT', item.int)}${statRow('WIS', item.wis)}${statRow('CHA', item.cha)}</tr></table></td></tr>`;
        return base + stats;
      }
      case 'npc':
        return row('Faction', item.faction) + row('Level', item.level, 'dm-val-accent') + row('HP', `${item.hp || '?'}/${item.maxHp || '?'}`, 'dm-val-hp')
          + row('Hostile', item.isHostile ? 'Yes' : 'No', item.isHostile ? 'dm-val-hp' : '') + row('Shopkeeper', item.isShopkeeper ? 'Yes' : 'No')
          + row('Personality', item.personality);
      case 'narrator_preset':
        return row('Archetype', item.archetype, 'dm-val-accent')
          + row('Selectable', item.isSelectable === false ? 'No' : 'Yes')
          + row('Sort Order', item.sortOrder)
          + rawRow('Personality', item.personalityPrompt ? `<div style="max-height:100px;overflow-y:auto;font-size:10px;white-space:pre-wrap;line-height:1.4;color:var(--dim);">${this.esc(item.personalityPrompt)}</div>` : '')
          + rawRow('Greeting', item.greetingText ? `<div style="max-height:80px;overflow-y:auto;font-size:10px;white-space:pre-wrap;line-height:1.4;color:var(--dim);">${this.esc(item.greetingText)}</div>` : '')
          + row('Lore Style', item.loreDeliveryStyle)
          + row('On Failure', item.failureReactionStyle)
          + row('On Success', item.successReactionStyle)
          + row('Tags', (item.tags || []).join(', '));
      case 'lore_entry':
        return row('Scope', item.scope, 'dm-val-accent')
          + row('Discovery', item.discoveryTrigger || (item.isStarterLore ? 'Starter Lore' : ''))
          + row('Cascade', item.cascade ? 'Yes' : 'No')
          + row('Parent', item.parentId)
          + row('Tags', (item.tags || []).join(', '));
      case 'quest':
        return row('Level', `${item.minLevel || 1}-${item.maxLevel || '?'}`, 'dm-val-accent')
          + row('Quest Giver', item.questGiverId)
          + row('Stages', (item.stages || []).length)
          + row('Repeatable', item.isRepeatable ? 'Yes' : 'No')
          + row('Prerequisites', (item.prerequisites || []).join(', '))
          + row('Tags', (item.tags || []).join(', '));
      case 'monster':
        return row('Level', `${item.minLevel || 1}-${item.maxLevel || '?'}`, 'dm-val-accent')
          + row('HP', item.hp, 'dm-val-hp') + row('Damage', item.damageDice, 'dm-val-hp')
          + row('Rarity', item.rarity) + row('Boss', item.isBoss ? 'BOSS' : 'No')
          + row('Tags', (item.tags || []).join(', '));
      default:
        return row('ID', item.id);
    }
  },


  // ═══════════════════════════════════════════════════════════
  //  CONTENT REGISTRY TAB
  // ═══════════════════════════════════════════════════════════

  _registryData: [],
  _registryEditingId: null,
  _registryFilterText: '',

  populateRegistryWorldFilter(worlds) {
    const sel = this.$('registry-world-select');
    if (!sel) return;
    const current = sel.value;
    sel.innerHTML = '<option value="">All Worlds</option>';
    for (const w of worlds) {
      this._ovWorldMap[w.id] = w.name; // share world name map with registry renderer
      const opt = document.createElement('option');
      opt.value = w.id;
      opt.textContent = w.name;
      sel.appendChild(opt);
    }
    if (current) sel.value = current;
  },

  async loadRegistry() {
    const type = this.$('registry-type-select')?.value || 'spells';
    const worldId = this.$('registry-world-select')?.value || '';
    const countEl = this.$('registry-count');
    const list = this.$('registry-list');
    if (!list) return;

    // Toggle edit/delete/new/generate buttons visibility for live-entity types
    const isLiveType = type === 'players' || type === 'rooms';
    const newBtn = this.$('btn-new-registry-entry');
    const questBtn = this.$('btn-generate-quest');
    if (newBtn) newBtn.style.display = isLiveType ? 'none' : '';
    if (questBtn) questBtn.style.display = isLiveType ? 'none' : '';

    try {
      // Players and rooms are live game entities, not content registry
      if (type === 'players') {
        this._registryData = await API.getPlayers();
      } else if (type === 'rooms') {
        this._registryData = await API.getRooms();
      } else {
        this._registryData = await API.getRegistry(type);
      }
      // Client-side world filter: entries have worldIds array or activeWorldId
      let filtered = this._registryData;
      if (worldId && type === 'players') {
        filtered = filtered.filter(e => (e.activeWorldId || e.homeWorldId || '') === worldId);
      } else
      if (worldId) {
        filtered = filtered.filter(e => {
          const wids = e.worldIds || e.WorldIds || [];
          return !wids.length || wids.some(id => id.toLowerCase() === worldId.toLowerCase());
        });
      }
      if (countEl) countEl.textContent = `${filtered.length} entries`;
      this._registryFilterText = '';
      const filterInput = this.$('registry-filter-input');
      if (filterInput) filterInput.value = '';
      this.renderRegistryList(type, filtered);
    } catch (err) {
      list.innerHTML = `<div class="empty-state">Failed to load: ${this.esc(err.message)}</div>`;
    }
  },

  _registryApplyFilter() {
    const type = this.$('registry-type-select')?.value || 'spells';
    const worldId = this.$('registry-world-select')?.value || '';
    const countEl = this.$('registry-count');
    const q = (this.$('registry-filter-input')?.value || '').trim().toLowerCase();
    this._registryFilterText = q;

    let filtered = this._registryData;
    if (worldId && type === 'players') {
      filtered = filtered.filter(e => (e.activeWorldId || e.homeWorldId || '') === worldId);
    } else if (worldId) {
      filtered = filtered.filter(e => {
        const wids = e.worldIds || e.WorldIds || [];
        return !wids.length || wids.some(id => id.toLowerCase() === worldId.toLowerCase());
      });
    }
    if (q) {
      filtered = filtered.filter(e =>
        (e.name || '').toLowerCase().includes(q) ||
        (e.id || '').toLowerCase().includes(q) ||
        (e.description || '').toLowerCase().includes(q) ||
        (e.race || '').toLowerCase().includes(q) ||
        (e.class || '').toLowerCase().includes(q) ||
        (e.currentRoomId || '').toLowerCase().includes(q)
      );
    }
    if (countEl) countEl.textContent = `${filtered.length} entries`;
    this.renderRegistryList(type, filtered);
  },

  renderRegistryList(type, entries) {
    const list = this.$('registry-list');
    if (!entries.length) {
      list.innerHTML = '<div class="empty-state">No entries in this registry.</div>';
      return;
    }

    const isLiveType = type === 'players' || type === 'rooms';
    list.innerHTML = entries.map(entry => {
      const meta = this._getEntryMeta(type, entry);
      const tags = (entry.tags || []).slice(0, 5);
      const worldIds = entry.worldIds || entry.WorldIds || [];
      const worldLabels = worldIds.map(id => this._ovWorldMap[id] || id).filter(Boolean);
      return `
        <div class="registry-entry" data-registry-id="${this.esc(entry.id)}">
          <div>
            <div class="registry-entry-name">${this.esc(entry.name || entry.id)}${worldLabels.length ? worldLabels.map(w => ` <span class="registry-world-tag">${this.esc(w)}</span>`).join('') : ''}</div>
            <div class="registry-entry-meta">${this.esc(meta)}</div>
            ${tags.length ? `<div class="registry-entry-tags">${tags.map(t => `<span class="registry-entry-tag">${this.esc(t)}</span>`).join('')}</div>` : ''}
          </div>
          ${isLiveType ? '' : `<div class="registry-entry-actions">
            <button class="btn btn-primary btn-sm" data-reg-action="edit" data-reg-id="${this.esc(entry.id)}" type="button">Edit</button>
            <button class="btn btn-danger btn-sm" data-reg-action="delete" data-reg-id="${this.esc(entry.id)}" type="button">Del</button>
          </div>`}
        </div>
      `;
    }).join('');
  },

  _getEntryMeta(type, entry) {
    switch (type) {
      case 'players':
        return `Lv.${entry.level || 1} ${entry.race || '?'} ${entry.class || '?'} | ${entry.currentRoomId || '?'} | HP ${entry.hp ?? '?'}/${entry.maxHp ?? '?'} | ${entry.gold ?? 0}g | World: ${entry.activeWorldId || '?'}`;
      case 'rooms':
        return `${Object.keys(entry.exits || {}).length} exits | ${(entry.npcs || []).length} NPCs | ${(entry.fixtures || []).length} fixtures${(entry.worldIds || []).length ? ' | ' + (entry.worldIds || []).join(', ') : ''}`;
      case 'spells':
        return `${entry.school || '?'} | Power ${entry.powerLevel || '?'} | ${entry.manaCost || 0} MP | Lv.${entry.requiredLevel || 1}${entry.damageDice ? ` | ${entry.damageDice}` : ''}${entry.healDice ? ` | Heal ${entry.healDice}` : ''}`;
      case 'items':
        return `${entry.type || 'Misc'} | ${entry.rarity || 'common'} | ${entry.value || 0}g${entry.damageDice ? ` | ${entry.damageDice}` : ''}${entry.armorValue ? ` | AC+${entry.armorValue}` : ''}`;
      case 'classes':
        return `${entry.hitDie || '?'} | ${entry.primaryStat || '?'}${entry.canCastSpells ? ' | Caster' : ' | Martial'} | ${(entry.spellList || []).length} spells`;
      case 'races':
        return `${(entry.traits || []).join(', ')}`;
      case 'monsters':
        return `Lv.${entry.minLevel ?? '?'}-${entry.maxLevel ?? '?'} | HP ${entry.baseHp ?? '?'} | ${entry.damageDice || '?'} | ${entry.rarity || 'common'}${entry.isBoss ? ' | BOSS' : ''}`;
      case 'quests':
        return `Lv.${entry.minLevel || 1} | ${(entry.stages || []).length} stages | Giver: ${entry.giverId || '?'}${entry.isOneTime === false ? ' | Repeatable' : ''}`;
      case 'lore_entries':
        return `${entry.loreScope || 'custom'} | ${entry.isStarterLore ? 'Starter' : (entry.discoveryTrigger || 'talk')}${entry.cascadeDown ? ' | Cascades' : ''}${entry.parentLoreId ? ` | Parent: ${entry.parentLoreId}` : ''}`;
      case 'narrator_presets':
        return `${entry.archetype || '?'}${entry.isSelectable ? '' : ' | Admin-only'} | Order: ${entry.sortOrder ?? 0}`;
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
    panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
      const pluralToSingular = { spells: 'spell', items: 'item', classes: 'class', races: 'race', monsters: 'monster', quests: 'quest', lore_entries: 'lore_entry', narrator_presets: 'narrator_preset' };
      const singularType = pluralToSingular[type] || type.replace(/s$/, '');
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
    const worldSelect = this.$('registry-world-select');
    const filterInput = this.$('registry-filter-input');
    const refreshBtn = this.$('btn-refresh-registry');
    const newBtn = this.$('btn-new-registry-entry');
    const saveBtn = this.$('btn-registry-save');
    const cancelBtn = this.$('btn-registry-cancel');
    const deleteBtn = this.$('btn-registry-delete');
    const sendBtn = this.$('btn-registry-chat-send');
    const chatInput = this.$('registry-chat-input');
    const listEl = this.$('registry-list');

    if (typeSelect) typeSelect.addEventListener('change', () => this.loadRegistry());
    if (worldSelect) worldSelect.addEventListener('change', () => this.loadRegistry());
    if (refreshBtn) refreshBtn.addEventListener('click', () => this.loadRegistry());
    if (newBtn) newBtn.addEventListener('click', () => {
      const pluralToSingular = { spells: 'spell', items: 'item', classes: 'class', races: 'race', monsters: 'monster', quests: 'quest', lore_entries: 'lore_entry', narrator_presets: 'narrator_preset' };
      const singular = pluralToSingular[typeSelect?.value] || 'spell';
      this.openRegistryEditor(null, singular);
    });
    if (saveBtn) saveBtn.addEventListener('click', () => this.saveRegistryEntry());
    if (cancelBtn) cancelBtn.addEventListener('click', () => this.closeRegistryEditor());
    if (deleteBtn) deleteBtn.addEventListener('click', () => this.deleteRegistryEntry(this._registryEditingId));
    if (sendBtn) sendBtn.addEventListener('click', () => this.sendRegistryChat());

    // Quest generation dialog
    const questGenBtn = this.$('btn-generate-quest');
    const questGenDialog = this.$('quest-gen-dialog');
    const questGenGo = this.$('btn-quest-gen-go');
    const questGenCancel = this.$('btn-quest-gen-cancel');
    if (questGenBtn && questGenDialog) {
      questGenBtn.addEventListener('click', () => {
        questGenDialog.classList.toggle('hidden');
        if (!questGenDialog.classList.contains('hidden')) this._populateQuestGenSelects();
      });
    }
    if (questGenCancel && questGenDialog) {
      questGenCancel.addEventListener('click', () => questGenDialog.classList.add('hidden'));
    }
    if (questGenGo) {
      questGenGo.addEventListener('click', () => this._runQuestGeneration());
    }
    if (chatInput) {
      chatInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') this.sendRegistryChat();
      });
    }

    // Live filter-by-name input
    let filterTimer = null;
    if (filterInput) {
      filterInput.addEventListener('input', () => {
        clearTimeout(filterTimer);
        filterTimer = setTimeout(() => this._registryApplyFilter(), 150);
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
  },

  // ── Quest Generation ─────────────────────────────────────
  async _populateQuestGenSelects() {
    // Populate world select
    const worldSel = this.$('quest-gen-world');
    if (worldSel && worldSel.options.length <= 1) {
      try {
        const worlds = await API.getWorlds();
        worlds.forEach(w => {
          const opt = document.createElement('option');
          opt.value = w.id;
          opt.textContent = w.name;
          worldSel.appendChild(opt);
        });
      } catch { /* ignore */ }
    }
    // Populate lore select
    const loreSel = this.$('quest-gen-lore');
    if (loreSel && loreSel.options.length === 0) {
      try {
        const loreEntries = await API.getRegistryEntries('lore_entries');
        (loreEntries || []).forEach(l => {
          const opt = document.createElement('option');
          opt.value = l.id;
          opt.textContent = `[${l.loreScope || '?'}] ${l.name}`;
          loreSel.appendChild(opt);
        });
      } catch { /* ignore */ }
    }
  },

  async _runQuestGeneration() {
    const brief = this.$('quest-gen-brief')?.value?.trim();
    if (!brief) { alert('Please enter a brief description.'); return; }

    const worldId = this.$('quest-gen-world')?.value || null;
    const loreSel = this.$('quest-gen-lore');
    const loreEntryIds = loreSel ? Array.from(loreSel.selectedOptions).map(o => o.value) : [];
    const minLevel = parseInt(this.$('quest-gen-min-level')?.value) || 1;
    const maxLevel = parseInt(this.$('quest-gen-max-level')?.value) || 5;

    const status = this.$('quest-gen-status');
    if (status) status.textContent = 'Generating quest... (this may take a moment)';
    const goBtn = this.$('btn-quest-gen-go');
    if (goBtn) goBtn.disabled = true;

    try {
      const result = await API.generateQuest(brief, worldId, loreEntryIds, minLevel, maxLevel);
      if (result?.json) {
        // Open in registry editor as a new quest
        let questData;
        try {
          questData = typeof result.json === 'string' ? JSON.parse(result.json) : result.json;
        } catch { questData = result.json; }
        this.openRegistryEditor(questData, 'quest');
        this.$('quest-gen-dialog')?.classList.add('hidden');
        if (status) status.textContent = '';
      } else {
        if (status) status.textContent = 'Generation returned no result.';
      }
    } catch (err) {
      if (status) status.textContent = `Error: ${err.message || err}`;
    } finally {
      if (goBtn) goBtn.disabled = false;
    }
  },

  // ─── Event Log ──────────────────────────────────────────
  _eventTypeIcons: {
    PlayerMoved: '🚶', CombatStarted: '⚔️', PlayerDied: '💀',
    PlayerTalked: '💬', QuestUpdated: '📜', RoomUpdated: '🏠',
    StoryAdvanced: '📖', PlayerCreated: '✨', PlayerRevived: '❤️',
    CombatEnded: '🏁', NpcSpawned: '👤', NpcDied: '☠️',
    SystemMessage: '⚙️'
  },

  clearEventLog() {
    const list = this.$('event-log-list');
    if (list) list.innerHTML = '<div class="empty-state">Waiting for game events...</div>';
  },

  renderEventLog(events, filter) {
    const list = this.$('event-log-list');
    if (!list) return;
    list.innerHTML = '';
    const filtered = filter ? events.filter(e => e.typeName === filter) : events;
    if (filtered.length === 0) {
      list.innerHTML = '<div class="empty-state">No matching events.</div>';
      return;
    }
    for (const entry of filtered) {
      list.appendChild(this._buildEventLogNode(entry));
    }
  },

  appendEventLogEntry(entry, filter) {
    if (filter && entry.typeName !== filter) return;
    const list = this.$('event-log-list');
    if (!list) return;
    const empty = list.querySelector('.empty-state');
    if (empty) empty.remove();
    const node = this._buildEventLogNode(entry);
    node.classList.add('fade-in');
    list.prepend(node);
    // Cap DOM at 200 entries
    while (list.children.length > 200) list.lastChild.remove();
  },

  _buildEventLogNode(entry) {
    const div = document.createElement('div');
    div.className = 'event-log-entry';
    div.dataset.eventType = entry.typeName;

    const ts = new Date(entry.timestamp);
    const timeStr = ts.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const icon = this._eventTypeIcons[entry.typeName] || '📌';

    div.innerHTML =
      `<span class="event-log-time">${timeStr}</span>` +
      `<span class="event-log-icon">${icon}</span>` +
      `<span class="event-log-player" title="${entry.playerId}">${entry.playerId || '—'}</span>` +
      `<span class="event-log-summary" title="${this._escapeHtml(entry.summary)}">${this._escapeHtml(entry.summary)}</span>`;
    return div;
  },

  _escapeHtml(text) {
    if (!text) return '';
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }
};
