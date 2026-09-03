// co-dm.js — one shared, bounded application service for humans and WebMCP callers.
(function () {
  'use strict';

  const STORAGE = {
    player: 'gae.coDm.selectedPlayer',
    activity: 'gae.coDm.activity',
    proposals: 'gae.coDm.proposals'
  };
  const MAX_ACTIVITY = 50;
  const MAX_PROPOSALS = 30;
  const ENTITY_TYPES = new Set(['player', 'room', 'npc', 'item', 'spell', 'class', 'race', 'quest', 'monster', 'narrator_preset', 'lore_entry']);
  const PROPOSAL_KINDS = new Set(['grant_item', 'apply_status', 'adjust_resources', 'teleport']);
  const STATUS_TYPES = ['buff', 'debuff', 'poison', 'regen', 'stun', 'blind', 'charm'];

  const state = {
    bound: false,
    authenticated: false,
    players: [],
    selectedPlayerId: readStoredText(STORAGE.player),
    context: null,
    selectedEntity: null,
    activity: readStoredArray(STORAGE.activity, MAX_ACTIVITY),
    proposals: readStoredArray(STORAGE.proposals, MAX_PROPOSALS),
    diagnostics: {
      supported: typeof document.modelContext?.registerTool === 'function',
      registrationAttempted: false,
      registeredTools: [],
      mostRecentToolCall: null,
      mostRecentVisibleMutation: null,
      registrationError: null
    }
  };

  function readStoredArray(key, limit) {
    try {
      const parsed = JSON.parse(localStorage.getItem(key) || '[]');
      return Array.isArray(parsed) ? parsed.slice(-limit) : [];
    } catch {
      return [];
    }
  }

  function readStoredText(key) {
    try {
      return localStorage.getItem(key) || '';
    } catch {
      return '';
    }
  }

  function persist(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch (error) {
      console.warn('Co-DM storage declined the invitation; the evening shall proceed in memory.', error);
    }
  }

  function persistText(key, value) {
    try {
      if (value) localStorage.setItem(key, value);
      else localStorage.removeItem(key);
    } catch (error) {
      console.warn('Co-DM could not remember that preference; memory is a fickle familiar.', error);
    }
  }

  function esc(value) {
    return UI?.esc ? UI.esc(String(value ?? '')) : String(value ?? '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function bounded(value, max = 500) {
    const text = String(value ?? '').trim();
    return text.length <= max ? text : `${text.slice(0, Math.max(0, max - 1))}…`;
  }

  function integer(value, fallback = 0) {
    const number = Number(value);
    return Number.isInteger(number) ? number : fallback;
  }

  function uuid(prefix) {
    const id = typeof crypto?.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    return `${prefix}-${id}`;
  }

  function clone(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
  }

  function activeQuest(progress) {
    const status = String(progress?.status ?? '').toLowerCase();
    return status === 'active' || status === 'readytoturnin' || status === 'ready_to_turn_in';
  }

  function compactStatus(effect) {
    const numericType = integer(effect?.type, -1);
    const type = typeof effect?.type === 'number'
      ? (STATUS_TYPES[numericType] || 'unknown')
      : bounded(effect?.type || 'unknown', 40).toLowerCase();
    const statModifiers = Object.fromEntries(Object.entries(effect?.statModifiers || {}).slice(0, 12).map(([stat, value]) => [
      bounded(stat, 40),
      Number.isFinite(Number(value)) ? Number(value) : 0
    ]));
    return {
      id: bounded(effect?.id, 120),
      name: bounded(effect?.name || 'Unnamed effect', 120),
      description: bounded(effect?.description, 350),
      type,
      remainingTurns: Math.max(0, integer(effect?.remainingTurns)),
      statModifiers,
      damagePerTurn: effect?.damagePerTurn == null ? null : integer(effect.damagePerTurn),
      healPerTurn: effect?.healPerTurn == null ? null : integer(effect.healPerTurn)
    };
  }

  function summarizeEntities(entries, max = 12) {
    const grouped = new Map();
    for (const entry of (entries || []).slice(0, 40)) {
      const name = bounded(entry?.name || entry?.id || 'Unknown', 90);
      const key = name.toLowerCase();
      const existing = grouped.get(key) || { id: bounded(entry?.id || '', 120), name, count: 0 };
      existing.count += Math.max(1, integer(entry?.quantity, 1));
      grouped.set(key, existing);
    }
    return [...grouped.values()].slice(0, max);
  }

  function compactStory(entry) {
    return {
      id: bounded(entry?.id || '', 120),
      actionId: bounded(entry?.actionId || '', 120),
      rawInput: bounded(entry?.rawInput || '', 240),
      mechanicalSummary: bounded(entry?.mechanicalSummary || '', 500),
      narration: bounded(entry?.narration || '', 900),
      roomId: bounded(entry?.roomId || '', 120),
      worldId: bounded(entry?.worldId || '', 120),
      timestamp: entry?.timestamp || null
    };
  }

  function compactSearchResult(item) {
    const worldIds = (item?.worldIds || []).slice(0, 5).map((id) => bounded(id, 120));
    return {
      type: bounded(item?.type || '', 40),
      id: bounded(item?.id || '', 120),
      name: bounded(item?.name || item?.title || item?.id || '', 160),
      summary: bounded(item?.description || item?.meta || '', 420),
      worldId: worldIds[0] || null,
      selection: {
        type: bounded(item?.type || '', 40),
        id: bounded(item?.id || '', 120),
        worldId: worldIds[0] || null
      }
    };
  }

  function compactEntity(item, type) {
    if (!item) return null;
    const common = {
      type,
      id: bounded(item.id || '', 120),
      name: bounded(item.name || item.title || item.id || '', 160),
      description: bounded(item.description || item.personality || '', 700),
      worldIds: (item.worldIds || []).slice(0, 5).map((id) => bounded(id, 120))
    };
    if (type === 'player') {
      return { ...common, race: bounded(item.race, 80), class: bounded(item.class, 80), level: item.level,
        hp: item.hp, maxHp: item.maxHp, mp: item.mp, maxMp: item.maxMp, gold: item.gold,
        currentRoomId: bounded(item.currentRoomId, 120), activeWorldId: bounded(item.activeWorldId, 120) };
    }
    if (type === 'room') {
      return { ...common, exits: Object.entries(item.exits || {}).slice(0, 12).map(([direction, roomId]) => ({ direction, roomId })),
        npcs: summarizeEntities(item.npcs), items: summarizeEntities(item.items), environmentTags: (item.environmentTags || []).slice(0, 12) };
    }
    if (type === 'npc') {
      return { ...common, roomId: bounded(item._roomId || '', 120), faction: bounded(item.faction || '', 100), level: item.level,
        disposition: bounded(item.disposition || '', 100), knowledgeScopes: (item.knowledgeScopes || []).slice(0, 12).map((v) => bounded(v, 100)) };
    }
    if (type === 'quest') {
      return { ...common, giverId: bounded(item.giverId || '', 120), minLevel: item.minLevel,
        stages: (item.stages || []).slice(0, 8).map((stage) => ({ id: bounded(stage.id, 120), name: bounded(stage.name, 160), description: bounded(stage.description, 350) })) };
    }
    if (type === 'lore_entry') {
      return { ...common, loreScope: bounded(item.loreScope || '', 120), discoveryTrigger: bounded(item.discoveryTrigger || '', 160),
        linkedEntityIds: (item.linkedEntityIds || []).slice(0, 12).map((id) => bounded(id, 120)), tags: (item.tags || []).slice(0, 12).map((tag) => bounded(tag, 80)) };
    }
    return { ...common, summary: bounded(item.meta || item.effect || item.archetype || item.rarity || '', 400), tags: (item.tags || []).slice(0, 12) };
  }

  function ensurePlayerId(playerId) {
    const value = bounded(playerId, 120);
    if (!value) throw new Error('playerId is required; the Co-DM never broadcasts by accident.');
    return value;
  }

  function renderPlayerOptions() {
    const select = document.getElementById('co-dm-player-select');
    if (!select) return;
    const selected = state.selectedPlayerId;
    select.innerHTML = '<option value="">Choose a player</option>' + state.players.map((player) =>
      `<option value="${esc(player.id)}">${esc(player.name || player.id)} (${esc(player.id)})</option>`
    ).join('');
    select.value = state.players.some((player) => player.id === selected) ? selected : '';
  }

  function renderScene() {
    const host = document.getElementById('co-dm-scene');
    if (!host) return;
    const context = state.context;
    if (!context) {
      host.innerHTML = `<div class="empty-state">${state.selectedPlayerId ? 'Context has not been refreshed.' : 'Choose one player to inspect authoritative game state.'}</div>`;
      return;
    }
    const player = context.player;
    const room = context.room;
    const entityList = (items, empty) => items.length
      ? items.map((item) => `<li><code>${esc(item.id || 'no-id')}</code> ${esc(item.name)}${item.count > 1 ? ` ×${item.count}` : ''}</li>`).join('')
      : `<li class="muted">${esc(empty)}</li>`;
    const exits = room?.exits?.length
      ? room.exits.map((exit) => `<li>${esc(exit.direction)} → <code>${esc(exit.roomId)}</code></li>`).join('')
      : '<li class="muted">No exits reported.</li>';
    const quests = context.activeQuests.length
      ? context.activeQuests.map((quest) => `<li><code>${esc(quest.questId)}</code> ${esc(quest.summary || quest.currentStageId || quest.status)}</li>`).join('')
      : '<li class="muted">No active quests.</li>';
    const statuses = (context.statusEffects || []).length
      ? context.statusEffects.map((effect) => `<li><code>${esc(effect.name)}</code> ${esc(effect.type)} · ${esc(effect.remainingTurns)} turn(s)${effect.description ? ` — ${esc(effect.description)}` : ''}</li>`).join('')
      : '<li class="muted">No active status effects.</li>';
    const story = context.recentStory.length
      ? context.recentStory.map((entry) => `<li><strong>${esc(entry.rawInput || 'DM / world')}</strong><span>${esc(entry.narration || entry.mechanicalSummary || 'No visible text.')}</span></li>`).join('')
      : '<li class="muted">No recent story entries.</li>';

    host.innerHTML = `
      <div class="co-dm-scene-heading">
        <strong>${esc(player.name)}</strong>
        <code>${esc(player.id)}</code>
        <span>Lv.${esc(player.level)} ${esc(player.race)} ${esc(player.class)}</span>
      </div>
      <div class="co-dm-stat-row">
        <span>HP ${esc(player.hp)}/${esc(player.maxHp)}</span><span>MP ${esc(player.mp)}/${esc(player.maxMp)}</span>
        <span>${esc(player.gold)} gold</span><span>World <code>${esc(player.activeWorldId)}</code></span>
      </div>
      <h4>${esc(room?.name || 'Room unavailable')} <code>${esc(room?.id || player.currentRoomId)}</code></h4>
      <p>${esc(room?.description || 'No room description is available.')}</p>
      <div class="co-dm-scene-columns">
        <div><strong>Exits</strong><ul>${exits}</ul></div>
        <div><strong>NPCs</strong><ul>${entityList(room?.npcs || [], 'No NPCs reported.')}</ul></div>
        <div><strong>Items</strong><ul>${entityList(room?.items || [], 'No visible items reported.')}</ul></div>
      </div>
      <div class="co-dm-interaction"><strong>Interaction:</strong> ${esc(context.interaction.mode || 'explore')}${context.interaction.target ? ` with ${esc(context.interaction.target)}` : ''}</div>
      <details${(context.statusEffects || []).length ? ' open' : ''}><summary>Status effects (${(context.statusEffects || []).length})</summary><ul>${statuses}</ul></details>
      <details><summary>Active quests (${context.activeQuests.length})</summary><ul>${quests}</ul></details>
      <details open><summary>Recent story (${context.recentStory.length})</summary><ol class="co-dm-story">${story}</ol></details>
      ${context.limitations.length ? `<div class="co-dm-limitations"><strong>Unavailable:</strong> ${esc(context.limitations.join(' '))}</div>` : ''}`;
  }

  function renderActivity() {
    const host = document.getElementById('co-dm-activity');
    if (!host) return;
    host.innerHTML = state.activity.length ? state.activity.map((entry) => `
      <div class="co-dm-receipt co-dm-receipt-${esc(entry.tone || 'info')}">
        <time datetime="${esc(entry.timestamp)}">${esc(new Date(entry.timestamp).toLocaleTimeString())}</time>
        <span>${esc(entry.message)}</span>
      </div>`).join('') : '<div class="empty-state">No agent receipts yet.</div>';
    host.scrollTop = host.scrollHeight;
  }

  function renderProposals() {
    const host = document.getElementById('co-dm-proposals');
    if (!host) return;
    host.innerHTML = state.proposals.length ? [...state.proposals].reverse().map((proposal) => `
      <article class="co-dm-proposal status-${esc(proposal.status)}" id="co-dm-proposal-${esc(proposal.id)}">
        <div class="co-dm-card-heading"><strong>${esc(proposal.title)}</strong><span class="co-dm-status-badge">${esc(proposal.status)}</span></div>
        <div class="co-dm-proposal-meta">${esc(proposal.kind)} · ${esc(proposal.playerName || proposal.playerId)} · proposed by ${esc(proposal.proposedBy)}</div>
        <p>${esc(proposal.summary)}</p>
        <p><strong>Rationale:</strong> ${esc(proposal.rationale)}</p>
        ${proposal.evidenceIds.length ? `<p><strong>Evidence:</strong> ${proposal.evidenceIds.map((id) => `<code>${esc(id)}</code>`).join(' ')}</p>` : ''}
        <details><summary>Exact mechanical payload</summary><pre>${esc(JSON.stringify(proposal.mechanicalRequest || proposal.payload, null, 2))}</pre></details>
        ${proposal.result ? `<div class="co-dm-proposal-result">${esc(proposal.result)}</div>` : ''}
        <div class="co-dm-proposal-actions">
          <button class="btn btn-primary btn-xs" data-co-dm-approve="${esc(proposal.id)}" type="button"${proposal.status !== 'pending' ? ' disabled' : ''}>Approve</button>
          <button class="btn btn-secondary btn-xs" data-co-dm-reject="${esc(proposal.id)}" type="button"${proposal.status !== 'pending' ? ' disabled' : ''}>Reject</button>
        </div>
      </article>`).join('') : '<div class="empty-state">No intervention proposals.</div>';
  }

  function renderDiagnostics() {
    const host = document.getElementById('co-dm-status');
    if (!host) return;
    const d = state.diagnostics;
    const row = (label, value) => `<div class="co-dm-diagnostic"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
    host.innerHTML = row('WebMCP supported', d.supported ? 'yes' : 'no')
      + row('Registration attempted', d.registrationAttempted ? 'yes' : 'no')
      + row('Registered tools', d.registeredTools.length ? d.registeredTools.join(', ') : 'none')
      + row('Most recent tool call', d.mostRecentToolCall || 'none')
      + row('Most recent visible mutation', d.mostRecentVisibleMutation || 'none')
      + (d.registrationError ? `<div class="co-dm-registration-error">${esc(d.registrationError)}</div>` : '');
  }

  function renderAll() {
    renderPlayerOptions();
    renderScene();
    renderActivity();
    renderProposals();
    renderDiagnostics();
  }

  function bindUi() {
    if (state.bound) return;
    state.bound = true;
    document.getElementById('co-dm-player-select')?.addEventListener('change', (event) => {
      const playerId = event.target.value;
      if (!playerId) {
        state.selectedPlayerId = '';
        state.context = null;
        persistText(STORAGE.player, '');
        renderScene();
        return;
      }
      void service.selectPlayer(playerId, { record: true });
    });
    document.getElementById('btn-co-dm-refresh')?.addEventListener('click', () => {
      void service.refreshContext({ record: true }).catch((error) => service.recordActivity(`Refresh failed: ${error.message}`, 'failure'));
    });
    document.getElementById('btn-co-dm-clear-activity')?.addEventListener('click', () => {
      state.activity = [];
      persist(STORAGE.activity, state.activity);
      renderActivity();
    });
    document.getElementById('co-dm-message-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const input = document.getElementById('co-dm-message');
      const message = input?.value || '';
      void service.sendMessage({ playerId: state.selectedPlayerId, message }).then(() => {
        if (input) input.value = '';
      }).catch((error) => service.recordActivity(`DM message failed: ${error.message}`, 'failure'));
    });
    document.getElementById('btn-co-dm-copy-prompt')?.addEventListener('click', async () => {
      const prompt = document.getElementById('co-dm-suggested-prompt')?.textContent || '';
      try {
        await navigator.clipboard.writeText(prompt);
        service.recordActivity('Human copied the suggested Co-DM prompt.', 'info');
      } catch {
        service.recordActivity('Clipboard access was unavailable; the prompt remains visible.', 'failure');
      }
    });
    document.getElementById('co-dm-proposals')?.addEventListener('click', (event) => {
      const approve = event.target.closest('[data-co-dm-approve]');
      const reject = event.target.closest('[data-co-dm-reject]');
      if (approve) void service.approveProposal(approve.dataset.coDmApprove);
      if (reject) service.rejectProposal(reject.dataset.coDmReject);
    });
  }

  function validateProposal(input) {
    const playerId = ensurePlayerId(input?.playerId);
    const kind = bounded(input?.kind, 40);
    const title = bounded(input?.title, 160);
    const rationale = bounded(input?.rationale, 800);
    if (!PROPOSAL_KINDS.has(kind)) throw new Error(`Unsupported intervention kind '${kind}'.`);
    if (!title) throw new Error('title is required.');
    if (!rationale) throw new Error('rationale is required.');
    const evidenceIds = Array.isArray(input?.evidenceIds)
      ? input.evidenceIds.slice(0, 10).map((id) => bounded(id, 140)).filter(Boolean)
      : [];
    const has = (name) => input?.[name] !== undefined && input?.[name] !== null && input?.[name] !== '';
    const rejectFields = (fields) => {
      const unsupported = fields.filter(has);
      if (unsupported.length) throw new Error(`${kind} does not accept: ${unsupported.join(', ')}.`);
    };

    let payload;
    let summary;
    if (kind === 'grant_item') {
      rejectFields(['statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId']);
      const itemId = bounded(input.itemId, 120);
      const quantity = integer(input.quantity, 1);
      if (!itemId) throw new Error('itemId is required for grant_item.');
      if (quantity < 1 || quantity > 20) throw new Error('quantity must be between 1 and 20.');
      payload = { itemId, quantity };
      summary = `Grant ${quantity} × registered item ${itemId}.`;
    } else if (kind === 'apply_status') {
      rejectFields(['itemId', 'quantity', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId']);
      const statusName = bounded(input.statusName, 120);
      const statusDescription = bounded(input.statusDescription, 500);
      const durationTurns = integer(input.durationTurns, 3);
      if (!statusName) throw new Error('statusName is required for apply_status.');
      if (durationTurns < 1 || durationTurns > 50) throw new Error('durationTurns must be between 1 and 50.');
      payload = { statusName, statusDescription, durationTurns };
      summary = `Apply status “${statusName}” for ${durationTurns} turn(s).`;
    } else if (kind === 'adjust_resources') {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'destinationRoomId']);
      payload = {
        hpDelta: integer(input.hpDelta), mpDelta: integer(input.mpDelta),
        goldDelta: integer(input.goldDelta), xpDelta: integer(input.xpDelta)
      };
      if (!Object.values(payload).some((value) => value !== 0)) throw new Error('At least one resource delta must be non-zero.');
      if (Object.values(payload).some((value) => Math.abs(value) > 10000)) throw new Error('Resource deltas are capped at 10000 per proposal.');
      summary = `Adjust resources: HP ${payload.hpDelta}, MP ${payload.mpDelta}, gold ${payload.goldDelta}, XP ${payload.xpDelta}.`;
    } else {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta']);
      const destinationRoomId = bounded(input.destinationRoomId, 120);
      if (!destinationRoomId) throw new Error('destinationRoomId is required for teleport.');
      payload = { destinationRoomId };
      summary = `Teleport to existing room ${destinationRoomId}.`;
    }
    return { playerId, kind, title, rationale, evidenceIds, payload, summary };
  }

  async function buildMutationRequest(proposal) {
    if (proposal.kind === 'grant_item') {
      const template = await API.getRegistryEntry('items', proposal.payload.itemId);
      if (!template) throw new Error(`Registered item '${proposal.payload.itemId}' was not found.`);
      return {
        endpoint: 'grant-item',
        body: {
          playerId: proposal.playerId,
          name: bounded(template.name, 160),
          type: bounded(template.type || 'Misc', 40),
          quantity: proposal.payload.quantity,
          value: integer(template.value),
          description: bounded(template.description || '', 500),
          damageDice: template.damageDice || null,
          damageStat: template.damageStat || null,
          armorValue: integer(template.armorValue),
          isEquippable: template.isEquippable,
          isConsumable: template.isConsumable,
          isTwoHanded: !!template.isTwoHanded,
          effect: bounded(template.effect || '', 300) || null,
          statBonuses: clone(template.statBonuses || {}),
          autoEquip: false
        }
      };
    }
    if (proposal.kind === 'apply_status') {
      return {
        endpoint: 'status',
        body: {
          playerId: proposal.playerId,
          name: proposal.payload.statusName,
          description: proposal.payload.statusDescription,
          type: 'Debuff',
          remainingTurns: proposal.payload.durationTurns,
          replaceExisting: true
        }
      };
    }
    if (proposal.kind === 'adjust_resources') {
      return { endpoint: 'resources', body: { playerId: proposal.playerId, ...proposal.payload } };
    }
    return {
      endpoint: 'teleport',
      body: {
        playerId: proposal.playerId,
        roomId: proposal.payload.destinationRoomId,
        createRoomIfMissing: false,
        connectFromCurrentRoom: false
      }
    };
  }

  async function fetchEntity(type, id, worldId) {
    const registryTypes = { spell: 'spells', item: 'items', class: 'classes', race: 'races', monster: 'monsters', quest: 'quests', lore_entry: 'lore_entries', narrator_preset: 'narrator_presets' };
    const belongsToWorld = (item) => !worldId || (item?.worldIds || []).some((candidate) => candidate.toLowerCase() === worldId.toLowerCase());
    if (registryTypes[type]) {
      const item = await API.getRegistryEntry(registryTypes[type], id);
      return item && belongsToWorld(item) ? item : null;
    }
    if (type === 'player') {
      const player = await API.getPlayer(id);
      return player && (!worldId || player.activeWorldId?.toLowerCase() === worldId.toLowerCase()) ? player : null;
    }
    if (type === 'room') {
      const room = await API.getRoom(id);
      return room && belongsToWorld(room) ? room : null;
    }
    if (type === 'npc') {
      const rooms = await API.getRooms();
      for (const room of rooms) {
        if (worldId && !(room.worldIds || []).some((candidate) => candidate.toLowerCase() === worldId.toLowerCase())) continue;
        const npc = (room.npcs || []).find((candidate) => candidate.id === id);
        if (npc) return { ...npc, _roomId: room.id };
      }
    }
    return null;
  }

  const service = {
    bootstrap() {
      bindUi();
      renderAll();
      return this.getDiagnostics();
    },

    async initialize(options = {}) {
      state.authenticated = options.authenticated !== false;
      bindUi();
      if (!state.authenticated) {
        state.players = [];
        state.context = null;
        renderAll();
        return null;
      }
      state.players = await API.getPlayers();
      if (state.selectedPlayerId && !state.players.some((player) => player.id === state.selectedPlayerId)) {
        state.selectedPlayerId = '';
        state.context = null;
        persistText(STORAGE.player, '');
      }
      renderPlayerOptions();
      if (state.selectedPlayerId) await this.refreshContext({ record: false });
      else renderScene();
      return state.context;
    },

    updatePlayers(players) {
      state.players = Array.isArray(players) ? players.slice() : [];
      if (state.selectedPlayerId && !state.players.some((player) => player.id === state.selectedPlayerId)) {
        state.selectedPlayerId = '';
        state.context = null;
        persistText(STORAGE.player, '');
      }
      renderPlayerOptions();
      renderScene();
    },

    handleGameEvent(event = {}) {
      if (!state.selectedPlayerId) return;
      if (event.playerId && event.playerId !== state.selectedPlayerId) return;
      window.clearTimeout(state.eventRefreshTimer);
      state.eventRefreshTimer = window.setTimeout(() => {
        void this.refreshContext({ record: false }).catch((error) => {
          console.warn('Co-DM event refresh failed; the manual refresh lever still functions.', error);
        });
      }, 150);
    },

    async selectPlayer(playerId, options = {}) {
      const id = ensurePlayerId(playerId);
      const player = await API.getPlayer(id);
      if (!player) throw new Error(`Player '${id}' was not found.`);
      state.selectedPlayerId = id;
      persistText(STORAGE.player, id);
      if (!state.players.some((entry) => entry.id === id)) state.players.push(player);
      renderPlayerOptions();
      const context = await this.refreshContext({ storyLimit: options.storyLimit, record: false });
      if (options.record !== false) this.recordActivity(`Selected ${player.name || id} for Co-DM inspection.`, 'info');
      return context;
    },

    async refreshContext(options = {}) {
      const playerId = ensurePlayerId(options.playerId || state.selectedPlayerId);
      const storyLimit = Math.max(1, Math.min(12, integer(options.storyLimit, 8)));
      if (playerId !== state.selectedPlayerId) {
        state.selectedPlayerId = playerId;
        persistText(STORAGE.player, playerId);
      }
      const player = await API.getPlayer(playerId);
      if (!player) throw new Error(`Player '${playerId}' was not found.`);
      const [room, story, health] = await Promise.all([
        API.getRoom(player.currentRoomId, playerId),
        API.getStory(playerId, storyLimit, player.activeWorldId),
        API.getHealth().catch(() => null)
      ]);
      const interaction = player.interaction || {};
      const modeNames = ['explore', 'conversation', 'combat', 'trading', 'stealth', 'event', 'blindadventure', 'cyoa'];
      const interactionMode = typeof interaction.mode === 'number' ? (modeNames[interaction.mode] || 'unknown') : bounded(interaction.mode || 'explore', 40).toLowerCase();
      const narratorCheck = health?.['health/narrator'];
      const limitations = [];
      if (!room) limitations.push('The current room payload was unavailable.');
      if (!narratorCheck) limitations.push('Narrator availability telemetry was unavailable.');
      limitations.push('Discord connection telemetry is not exposed by the dashboard API.');
      if (interactionMode === 'combat') limitations.push('Detailed combat turn state is not exposed by the current dashboard API.');

      state.context = {
        mode: 'co-dm',
        selectedPlayerId: player.id,
        player: {
          id: bounded(player.id, 120), name: bounded(player.name, 160), race: bounded(player.race, 100), class: bounded(player.class, 100),
          level: player.level, hp: player.hp, maxHp: player.maxHp, mp: player.mp, maxMp: player.maxMp, gold: player.gold,
          currentRoomId: bounded(player.currentRoomId, 120), activeWorldId: bounded(player.activeWorldId, 120)
        },
        room: room ? {
          id: bounded(player.currentRoomId, 120), instanceId: bounded(room.id, 160), name: bounded(room.name, 160), description: bounded(room.description, 900),
          exits: Object.entries(room.exits || {}).slice(0, 12).map(([direction, roomId]) => ({ direction: bounded(direction, 50), roomId: bounded(roomId, 120) })),
          npcs: summarizeEntities(room.npcs), items: summarizeEntities(room.items)
        } : null,
        interaction: {
          mode: interactionMode,
          target: bounded(interaction.target || '', 160) || null,
          turnCount: integer(interaction.turnCount),
          canLeave: interaction.canLeave !== false
        },
        activeQuests: (player.questLog || []).filter(activeQuest).slice(0, 12).map((quest) => ({
          questId: bounded(quest.questId, 120), status: bounded(quest.status, 60), currentStageId: bounded(quest.currentStageId, 120),
          summary: bounded(quest.narratorDescription || '', 350),
          objectives: (quest.objectives || []).slice(0, 12).map((objective) => ({ objectiveId: bounded(objective.objectiveId, 120), currentCount: objective.currentCount, isComplete: !!objective.isComplete }))
        })),
        statusEffects: (player.statusEffects || []).slice(0, 12).map(compactStatus),
        recentStory: (story || []).slice(0, storyLimit).map(compactStory),
        selectedEntity: clone(state.selectedEntity),
        capabilities: {
          discordConnected: null,
          narratorAvailable: narratorCheck ? !!narratorCheck.ok : null,
          signalRConnected: typeof GameHub?.isRealtimeAvailable === 'function' ? GameHub.isRealtimeAvailable() : false
        },
        limitations
      };
      renderPlayerOptions();
      renderScene();
      if (options.record !== false) this.recordActivity(`Inspected ${player.name}'s current scene.`, 'info');
      return clone(state.context);
    },

    async getContext(input = {}) {
      if (input.playerId) await this.selectPlayer(input.playerId, { record: false, storyLimit: input.storyLimit });
      const context = await this.refreshContext({ storyLimit: input.storyLimit, record: false });
      this.recordActivity(`Agent inspected ${context.player.name}'s current scene.`, 'info');
      return context;
    },

    async searchWorld(input = {}) {
      const query = bounded(input.query, 200);
      const type = input.type ? bounded(input.type, 40) : '';
      const worldId = input.worldId ? bounded(input.worldId, 120) : '';
      const limit = Math.max(1, Math.min(20, integer(input.limit, 10)));
      if (!query) throw new Error('query must be nonblank.');
      if (type && !ENTITY_TYPES.has(type)) throw new Error(`Unsupported entity type '${type}'.`);
      const data = await API.dmSearch(query, type || undefined, worldId || undefined);
      const raw = (data.results || []).slice(0, limit);
      const searchInput = document.getElementById('overview-search-input');
      const typeFilter = document.getElementById('overview-type-filter');
      const worldFilter = document.getElementById('overview-world-filter');
      if (searchInput) searchInput.value = query;
      if (typeFilter) typeFilter.value = type;
      if (worldFilter && [...worldFilter.options].some((option) => option.value === worldId)) worldFilter.value = worldId;
      UI._ovRenderResults(raw, query);
      this.recordActivity(`Agent searched ${type || 'the world'} for “${query}” and found ${raw.length} result(s).`, 'info');
      return { query, type: type || null, worldId: worldId || null, total: data.total ?? raw.length, returned: raw.length, results: raw.map(compactSearchResult) };
    },

    async inspectEntity(input = {}) {
      const type = bounded(input.type, 40);
      const id = bounded(input.id, 120);
      const worldId = bounded(input.worldId || '', 120);
      if (!ENTITY_TYPES.has(type)) throw new Error(`Unsupported entity type '${type}'.`);
      if (!id) throw new Error('id is required.');
      const item = await fetchEntity(type, id, worldId);
      if (!item) throw new Error(`${type} '${id}' was not found.`);
      UI.ovSelectItem(item, type);
      state.selectedEntity = compactEntity(item, type);
      if (state.context) state.context.selectedEntity = clone(state.selectedEntity);
      this.recordActivity(`Agent inspected ${type} ${item.name || id}.`, 'info');
      return clone(state.selectedEntity);
    },

    async sendMessage(input = {}) {
      const playerId = ensurePlayerId(input.playerId);
      const message = bounded(input.message, 801);
      if (!message) throw new Error('message must be nonblank.');
      if (message.length > 800) throw new Error('message must be at most 800 characters.');
      const result = await API.sendMessage({ playerId, message });
      if (playerId !== state.selectedPlayerId) await this.selectPlayer(playerId, { record: false });
      const context = await this.refreshContext({ record: false });
      state.diagnostics.mostRecentVisibleMutation = `DM message to ${context.player.name}`;
      renderDiagnostics();
      this.recordActivity(`Agent sent a visible DM message to ${context.player.name}.`, 'success');
      return {
        player: { id: context.player.id, name: context.player.name },
        message: bounded(message, 800),
        server: { sent: result.sent, discordMirrored: result.discordMirrored === true, summary: bounded(result.summary || '', 300) },
        storyReceipt: context.recentStory[0] || null
      };
    },

    async createProposal(input = {}) {
      const valid = validateProposal(input);
      const player = await API.getPlayer(valid.playerId);
      if (!player) throw new Error(`Player '${valid.playerId}' was not found.`);
      const proposal = {
        id: uuid('proposal'),
        playerId: valid.playerId,
        playerName: bounded(player.name || valid.playerId, 160),
        kind: valid.kind,
        title: valid.title,
        summary: valid.summary,
        rationale: valid.rationale,
        evidenceIds: valid.evidenceIds,
        payload: valid.payload,
        proposedBy: 'browser agent',
        status: 'pending',
        result: null,
        createdAt: new Date().toISOString()
      };
      proposal.mechanicalRequest = await buildMutationRequest(proposal);
      state.proposals.push(proposal);
      state.proposals = state.proposals.slice(-MAX_PROPOSALS);
      persist(STORAGE.proposals, state.proposals);
      renderProposals();
      this.recordActivity(`Agent proposed ${valid.summary} Human review is required.`, 'info');
      requestAnimationFrame(() => document.getElementById(`co-dm-proposal-${proposal.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
      return clone(proposal);
    },

    listProposals() {
      return clone(state.proposals);
    },

    async approveProposal(proposalId) {
      const proposal = state.proposals.find((entry) => entry.id === proposalId);
      if (!proposal) throw new Error(`Proposal '${proposalId}' was not found.`);
      if (proposal.status !== 'pending') throw new Error(`Proposal '${proposalId}' is already ${proposal.status}.`);
      const riskyResources = proposal.kind === 'adjust_resources' && Object.values(proposal.payload).some((value) => Math.abs(value) > 100);
      if ((proposal.kind === 'teleport' || riskyResources) && !window.confirm(`Approve ${proposal.summary}\n\nThe existing game API will modify persistent state.`)) return clone(proposal);
      proposal.result = 'Approval in progress…';
      renderProposals();
      try {
        const mechanicalRequest = proposal.mechanicalRequest || await buildMutationRequest(proposal);
        proposal.mechanicalRequest = mechanicalRequest;
        let result;
        if (proposal.kind === 'grant_item') {
          result = await API.grantItem(mechanicalRequest.body);
        } else if (proposal.kind === 'apply_status') {
          result = await API.applyStatus(mechanicalRequest.body);
        } else if (proposal.kind === 'adjust_resources') {
          result = await API.adjustResources(mechanicalRequest.body);
        } else if (proposal.kind === 'teleport') {
          result = await API.teleportPlayer(mechanicalRequest.body);
        }
        proposal.status = 'approved';
        proposal.result = bounded(result?.summary || 'Existing game API accepted the intervention.', 500);
        state.diagnostics.mostRecentVisibleMutation = `${proposal.kind} for ${proposal.playerName}`;
        this.recordActivity(`Human approved “${proposal.title}”. ${proposal.result}`, 'success');
        if (proposal.playerId === state.selectedPlayerId) await this.refreshContext({ record: false });
      } catch (error) {
        proposal.status = 'failed';
        proposal.result = bounded(error.message || 'Approval failed.', 500);
        this.recordActivity(`Approval failed for “${proposal.title}”: ${proposal.result}`, 'failure');
      }
      persist(STORAGE.proposals, state.proposals);
      renderProposals();
      renderDiagnostics();
      return clone(proposal);
    },

    rejectProposal(proposalId) {
      const proposal = state.proposals.find((entry) => entry.id === proposalId);
      if (!proposal) throw new Error(`Proposal '${proposalId}' was not found.`);
      if (proposal.status !== 'pending') throw new Error(`Proposal '${proposalId}' is already ${proposal.status}.`);
      proposal.status = 'rejected';
      proposal.result = 'Rejected by the human DM. No game mutation API was called.';
      persist(STORAGE.proposals, state.proposals);
      renderProposals();
      this.recordActivity(`Human rejected “${proposal.title}”; game state was not changed.`, 'info');
      return clone(proposal);
    },

    recordActivity(message, tone = 'info') {
      const receipt = { id: uuid('activity'), timestamp: new Date().toISOString(), message: bounded(message, 360), tone };
      state.activity.push(receipt);
      state.activity = state.activity.slice(-MAX_ACTIVITY);
      persist(STORAGE.activity, state.activity);
      renderActivity();
      return clone(receipt);
    },

    setWebMcpDiagnostics(update = {}) {
      state.diagnostics = { ...state.diagnostics, ...update };
      if (Array.isArray(update.registeredTools)) state.diagnostics.registeredTools = [...new Set(update.registeredTools)].slice(0, 6);
      renderDiagnostics();
      return this.getDiagnostics();
    },

    getDiagnostics() {
      return clone(state.diagnostics);
    },

    reset() {
      state.authenticated = false;
      state.players = [];
      state.context = null;
      renderPlayerOptions();
      renderScene();
    }
  };

  window.GaeCoDm = service;
  service.bootstrap();
})();
