// co-dm.js — one shared, bounded application service for humans and WebMCP callers.
(function () {
  'use strict';

  const STORAGE = {
    player: 'gae.coDm.selectedPlayer',
    activity: 'gae.coDm.activity',
    proposals: 'gae.coDm.proposals',
    approvalTokens: 'gae.coDm.approvalTokens'
  };
  const MAX_ACTIVITY = 50;
  const MAX_PROPOSALS = 30;
  const ENTITY_TYPES = new Set(['player', 'room', 'npc', 'item', 'spell', 'class', 'race', 'quest', 'monster', 'narrator_preset', 'lore_entry']);
  const ENTITY_CATEGORY_MAP = Object.freeze({ character: 'player', location: 'room', npc: 'npc', item: 'item', spell: 'spell', quest: 'quest', lore: 'lore_entry' });
  const PROPOSAL_KINDS = new Set(['grant_item', 'apply_status', 'adjust_resources', 'teleport', 'pause_player', 'resume_player', 'invoke_registered_spell']);
  const MESSAGE_DELIVERIES = new Set(['player_flow', 'player_flow_and_discord']);
  const STATUS_TYPES = ['buff', 'debuff', 'poison', 'regen', 'stun', 'blind', 'charm'];

  const state = {
    bound: false,
    authenticated: false,
    players: [],
    selectedPlayerId: '',
    context: null,
    liveFeed: [],
    sheetPlayer: null,
    selectedEntity: null,
    entityIndex: new Map(),
    approvalInFlight: null,
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

  function readApprovalTokens() {
    try {
      const value = JSON.parse(sessionStorage.getItem(STORAGE.approvalTokens) || '{}');
      return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
    } catch {
      return {};
    }
  }

  function persistProposals() {
    persist(STORAGE.proposals, state.proposals.map(({ approvalToken: _secret, ...proposal }) => proposal));
    try {
      const tokens = Object.fromEntries(state.proposals.filter((proposal) => proposal.approvalToken).map((proposal) => [proposal.requestId, proposal.approvalToken]));
      sessionStorage.setItem(STORAGE.approvalTokens, JSON.stringify(tokens));
    } catch (error) {
      console.warn('Co-DM approval secrets could not be retained for this tab; fresh proposals still work.', error);
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

  // Story text arrives as narrator markdown (bold, code fences, HP bars). Cards show prose only.
  function plainText(value, max = 220) {
    const text = String(value ?? '')
      .replace(/```[\s\S]*?```/g, ' ')
      .replace(/`([^`]*)`/g, '$1')
      .replace(/\*\*([^*]*)\*\*/g, '$1')
      .replace(/\[[#=-]{4,}\]/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
    return bounded(text, max);
  }

  function integer(value, fallback = 0) {
    const number = Number(value);
    return Number.isInteger(number) ? number : fallback;
  }

  function domainError(code, message) {
    const error = new Error(message);
    error.code = code;
    return error;
  }

  function uuid(prefix) {
    const id = typeof crypto?.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    return `${prefix}-${id}`;
  }

  function approvalToken() {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('');
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
      playerId: bounded(entry?.playerId || '', 120),
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

  function selectedPlayerId() {
    if (!state.authenticated) throw domainError('AUTH_REQUIRED', 'An authenticated admin session is required.');
    if (!state.selectedPlayerId) throw domainError('NO_PLAYER_SELECTED', 'Select one player visibly in the Co-DM panel first.');
    return ensurePlayerId(state.selectedPlayerId);
  }

  function renderPlayerOptions() {
    const select = document.getElementById('co-dm-player-select');
    if (!select) return;
    const selected = state.selectedPlayerId;
    select.innerHTML = '<option value="">All Players — Live Feed</option>' + state.players.map((player) =>
      `<option value="${esc(player.id)}">${esc(player.name || player.id)} (${esc(player.id)})</option>`
    ).join('');
    select.value = state.players.some((player) => player.id === selected) ? selected : '';
  }

  function renderLiveFeed(host) {
    const playerById = new Map(state.players.map((player) => [player.id, player]));
    const latestByPlayer = new Map();
    for (const entry of state.liveFeed) {
      if (!latestByPlayer.has(entry.playerId)) latestByPlayer.set(entry.playerId, entry);
    }
    const playerCards = state.players.length
      ? state.players.map((player) => {
        const latest = latestByPlayer.get(player.id);
        const hold = player.commandHold;
        return `<article class="co-dm-player-card${hold ? ' is-held' : ''}" data-co-dm-focus="${esc(player.id)}" role="button" tabindex="0" title="Open ${esc(player.name || player.id)}: scene, stats, recent actions, and messaging">
          <div class="co-dm-card-heading"><strong>${esc(player.name || player.id)}</strong><span class="co-dm-presence ${hold ? 'held' : 'live'}">${hold ? 'held' : 'live'}</span></div>
          <code>${esc(player.id)}</code>
          <div class="co-dm-player-vitals"><span>HP ${esc(player.hp)}/${esc(player.maxHp)}</span><span>MP ${esc(player.mp)}/${esc(player.maxMp)}</span><span>Lv.${esc(player.level)}</span></div>
          <div class="co-dm-player-location">${esc(player.currentRoomId || 'unknown room')} · ${esc(player.activeWorldId || 'default world')}</div>
          <p>${esc(plainText(latest?.narration || latest?.mechanicalSummary || (hold?.reason ? `DM review: ${hold.reason}` : 'No recent activity yet.')))}</p>
          <span class="co-dm-card-cta">Open scene →</span>
        </article>`;
      }).join('')
      : '<div class="empty-state">No players are currently available.</div>';
    const timeline = state.liveFeed.length
      ? state.liveFeed.map((entry) => {
        const player = playerById.get(entry.playerId);
        const time = entry.timestamp ? new Date(entry.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : 'now';
        return `<article class="co-dm-live-entry">
          <time datetime="${esc(entry.timestamp || '')}">${esc(time)}</time>
          <button data-co-dm-focus="${esc(entry.playerId)}" type="button">${esc(player?.name || entry.playerId)}</button>
          <div><strong>${esc(entry.rawInput || 'DM / world')}</strong><p>${esc(plainText(entry.narration || entry.mechanicalSummary || 'State changed without visible narration.', 320))}</p></div>
        </article>`;
      }).join('')
      : '<div class="empty-state">No player activity yet. New commands and DM messages will appear here live.</div>';

    host.innerHTML = `<div class="co-dm-live-heading"><div><strong>ALL PLAYERS LIVE</strong><span>Chronological campaign view</span></div><span>${state.players.length} player${state.players.length === 1 ? '' : 's'}</span></div>
      <div class="co-dm-player-grid">${playerCards}</div>
      <div class="co-dm-live-timeline"><h4>Latest activity</h4>${timeline}</div>`;
  }

  let lastArmedPlayerId = '';
  function syncPlayerScopedControls(hasFocus) {
    const label = document.querySelector('#co-dm-message-form > label');
    const focused = state.players.find((entry) => entry.id === state.selectedPlayerId);
    const focusedName = focused?.name || state.selectedPlayerId;
    if (label) label.textContent = hasFocus ? `Message ${focusedName} as the Dungeon Master` : 'Open a player card above to send a message';
    const form = document.getElementById('co-dm-message-form');
    if (form && hasFocus && state.selectedPlayerId !== lastArmedPlayerId) {
      form.classList.add('is-armed');
      window.clearTimeout(form.__armTimer);
      form.__armTimer = window.setTimeout(() => form.classList.remove('is-armed'), 1400);
    }
    lastArmedPlayerId = hasFocus ? state.selectedPlayerId : '';
    document.querySelectorAll('#co-dm-message, #co-dm-message-delivery, #co-dm-message-form button')
      .forEach((control) => { control.disabled = !hasFocus; });
  }

  const SWITCHER_LIMIT = 12;
  function renderSwitcher(activeId) {
    const ordered = [...state.players].sort((left, right) => (left.id === activeId ? -1 : right.id === activeId ? 1 : 0));
    const shown = ordered.slice(0, SWITCHER_LIMIT);
    const hidden = ordered.length - shown.length;
    return `<div class="co-dm-switcher" aria-label="Quick player switch">
      <button class="co-dm-chip co-dm-chip-back" data-co-dm-unfocus type="button" title="Back to the all-player live feed">← All players</button>
      ${shown.map((entry) => `<button class="co-dm-chip${entry.id === activeId ? ' active' : ''}" data-co-dm-focus="${esc(entry.id)}" type="button" title="${esc(entry.id)}"${entry.id === activeId ? ' aria-current="true"' : ''}>${esc(entry.name || entry.id)}</button>`).join('')}
      ${hidden > 0 ? `<span class="co-dm-chip co-dm-chip-more">+${hidden} more in the dropdown</span>` : ''}
    </div>`;
  }

  let lastRenderedPlayerId = null;
  function renderScene() {
    const host = document.getElementById('co-dm-scene');
    if (!host) return;
    // Switching players (or back to everyone) starts the card at its heading, not at whatever
    // offset the previous view had been scrolled to.
    if (lastRenderedPlayerId !== state.selectedPlayerId) host.scrollTop = 0;
    lastRenderedPlayerId = state.selectedPlayerId;
    syncPlayerScopedControls(!!state.selectedPlayerId);
    const title = document.getElementById('co-dm-scene-title');
    if (!state.selectedPlayerId) {
      if (title) title.textContent = 'All Player Activity';
      renderLiveFeed(host);
      return;
    }
    if (title) title.textContent = 'Current Scene';
    const context = state.context;
    if (!context) {
      host.innerHTML = `<div class="empty-state">${state.selectedPlayerId ? 'Context has not been refreshed.' : 'Pick a player above to see their live scene, or keep All Players for the campaign feed.'}</div>`;
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
    const hold = player.commandHold;
    const holdControl = hold
      ? `<div class="co-dm-hold-state is-held"><div><strong>PLAYER HELD</strong><span>${esc(hold.reason)}</span></div><button class="btn btn-primary btn-xs" data-co-dm-resume type="button">Propose Resume</button></div>`
      : '<div class="co-dm-hold-state"><div><strong>PLAYER LIVE</strong><span>Consequential commands are currently enabled.</span></div><button class="btn btn-secondary btn-xs" data-co-dm-hold type="button">Propose Hold</button></div>';

    const switcher = renderSwitcher(player.id);
    host.innerHTML = `
      ${switcher}
      <div class="co-dm-scene-heading">
        <strong>${esc(player.name)}</strong>
        <code>${esc(player.id)}</code>
        <span>Lv.${esc(player.level)} ${esc(player.race)} ${esc(player.class)}</span>
      </div>
      <div class="co-dm-stat-row">
        <span>HP ${esc(player.hp)}/${esc(player.maxHp)}</span><span>MP ${esc(player.mp)}/${esc(player.maxMp)}</span>
        <span>${esc(player.gold)} gold</span><span>World <code>${esc(player.activeWorldId)}</code></span>
      </div>
      ${holdControl}
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
      </div>`).join('') : '<div class="empty-state">Nothing yet. Every tool call your browser agent makes lands here as a receipt.</div>';
    host.scrollTop = host.scrollHeight;
  }

  function renderProposals() {
    const host = document.getElementById('co-dm-proposals');
    if (!host) return;
    host.innerHTML = state.proposals.length ? [...state.proposals].reverse().map((proposal) => `
      <article class="co-dm-proposal status-${esc(proposal.status)}" id="co-dm-proposal-${esc(proposal.id)}">
        <div class="co-dm-card-heading"><strong>${esc(proposal.title)}</strong><span class="co-dm-status-badge">${esc(proposal.status)}</span></div>
        <div class="co-dm-proposal-actions">
          <button class="btn btn-primary btn-xs" data-co-dm-approve="${esc(proposal.id)}" type="button"${proposal.status !== 'pending' ? ' disabled' : ''}>${proposal.kind === 'player_flow_and_discord' ? 'Confirm delivery' : 'Approve'}</button>
          <button class="btn btn-secondary btn-xs" data-co-dm-reject="${esc(proposal.id)}" type="button"${proposal.status !== 'pending' ? ' disabled' : ''}>${proposal.kind === 'player_flow_and_discord' ? 'Cancel' : 'Reject'}</button>
        </div>
        <div class="co-dm-proposal-meta">${esc(proposal.kind)} · ${esc(proposal.playerName || proposal.playerId)} · proposed by ${esc(proposal.proposedBy)}</div>
        <p>${esc(proposal.summary)}</p>
        <p><strong>Rationale:</strong> ${esc(proposal.rationale)}</p>
        ${proposal.kind === 'player_flow_and_discord' ? `<p><strong>Destination:</strong> Player Flow and configured Discord thread for <code>${esc(proposal.playerId)}</code></p>` : ''}
        ${proposal.payload?.message ? `<p class="co-dm-preview"><strong>Player preview:</strong> “${esc(proposal.payload.message)}”</p>` : ''}
        ${(proposal.evidenceIds || []).length ? `<p><strong>Evidence:</strong> ${proposal.evidenceIds.map((id) => `<code>${esc(id)}</code>`).join(' ')}</p>` : ''}
        <details><summary>Exact ${proposal.actionType === 'message_delivery' ? 'delivery' : 'mechanical'} payload</summary><pre>${esc(JSON.stringify(proposal.payload, null, 2))}</pre></details>
        ${proposal.result ? `<div class="co-dm-proposal-result">${esc(proposal.result)}</div>` : ''}
      </article>`).join('') : '<div class="empty-state">Empty. When the agent suggests a message or a mechanical change, it waits here for your approval.</div>';
  }

  function bar(label, value, max, cssClass) {
    const safeMax = Math.max(1, integer(max, 1));
    const current = Math.max(0, integer(value));
    const pct = Math.max(0, Math.min(100, Math.round((current / safeMax) * 100)));
    return `<div class="co-dm-bar ${cssClass}"><span class="co-dm-bar-label">${esc(label)}</span><span class="co-dm-bar-track"><span class="co-dm-bar-fill" style="width:${pct}%"></span></span><span class="co-dm-bar-num">${esc(current)}/${esc(safeMax)}</span></div>`;
  }

  function renderWebMcpFooter() {
    const d = state.diagnostics;
    const tools = d.registeredTools.length;
    const summary = d.supported
      ? (tools ? `WebMCP · ${tools} tool${tools === 1 ? '' : 's'} ready` : 'WebMCP · supported, no tools registered')
      : 'WebMCP · not available in this browser';
    const row = (label, value) => `<div class="co-dm-diagnostic"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
    return `<details class="co-dm-sheet-diag"${d.supported ? '' : ' open'}>
      <summary>${esc(summary)}</summary>
      ${row('WebMCP supported', d.supported ? 'yes' : 'no')}
      ${row('Registration attempted', d.registrationAttempted ? 'yes' : 'no')}
      ${row('Registered tools', tools ? d.registeredTools.join(', ') : 'none')}
      ${row('Most recent tool call', d.mostRecentToolCall || 'none')}
      ${row('Most recent visible mutation', d.mostRecentVisibleMutation || 'none')}
      ${d.registrationError ? `<div class="co-dm-registration-error">${esc(d.registrationError)}</div>` : ''}
    </details>`;
  }

  function renderDiagnostics() {
    const host = document.getElementById('co-dm-status');
    if (!host) return;
    const p = state.selectedPlayerId ? state.sheetPlayer : null;
    if (!p) {
      host.innerHTML = '<div class="empty-state">Open a player card to see their stat sheet here: vitals, attributes, gold, gear, and quests.</div>' + renderWebMcpFooter();
      return;
    }
    const attributes = [['STR', p.str], ['DEX', p.dex], ['CON', p.con], ['INT', p.int], ['WIS', p.wis], ['CHA', p.cha], ['LCK', p.luck]]
      .map(([label, value]) => `<div class="co-dm-attr"><span>${label}</span><strong>${esc(integer(value, 10))}</strong></div>`).join('');
    const equipped = Object.values(p.equipment || {}).filter((slot) => slot && typeof slot === 'object').length;
    const activeQuests = (p.questLog || []).filter(activeQuest).length;
    const interaction = p.interaction || {};
    const modeNames = ['explore', 'conversation', 'combat', 'trading', 'stealth', 'event', 'blindadventure', 'cyoa'];
    const mode = typeof interaction.mode === 'number' ? (modeNames[interaction.mode] || 'unknown') : bounded(interaction.mode || 'explore', 40).toLowerCase();
    const fact = (label, value) => `<div class="co-dm-fact"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
    host.innerHTML = `
      <div class="co-dm-sheet-heading">
        <strong>${esc(p.name || p.id)}</strong>
        <span>Lv.${esc(p.level)} ${esc(p.race || '')} ${esc(p.class || '')}</span>
        <span class="co-dm-presence ${p.commandHold ? 'held' : 'live'}">${p.commandHold ? 'held' : 'live'}</span>
      </div>
      <div class="co-dm-bars">
        ${bar('HP', p.hp, p.maxHp, 'co-dm-bar-hp')}
        ${bar('MP', p.mp, p.maxMp, 'co-dm-bar-mp')}
      </div>
      <div class="co-dm-attrs">${attributes}</div>
      <div class="co-dm-facts">
        ${fact('Gold', `${integer(p.gold)} ◈`)}
        ${fact('XP', integer(p.xp))}
        ${fact('Room', p.currentRoomId || 'unknown')}
        ${fact('World', p.activeWorldId || 'default')}
        ${fact('Mode', interaction.target ? `${mode} · ${bounded(interaction.target, 40)}` : mode)}
        ${fact('Carrying', `${(p.inventory || []).length} item${(p.inventory || []).length === 1 ? '' : 's'} · ${equipped} equipped`)}
        ${fact('Quests', `${activeQuests} active`)}
        ${fact('Effects', `${(p.statusEffects || []).length} active`)}
        ${fact('Spells', `${(p.spellbook || []).length} known`)}
      </div>
      ${renderWebMcpFooter()}`;
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
        void service.showAllPlayers({ record: true }).catch((error) => service.recordActivity(`Live feed refresh failed: ${error.message}`, 'failure'));
        return;
      }
      void service.selectPlayer(playerId, { record: true });
    });
    document.getElementById('btn-co-dm-refresh')?.addEventListener('click', () => {
      const refresh = state.selectedPlayerId ? service.refreshContext({ record: true }) : service.refreshLiveFeed({ record: true });
      void refresh.catch((error) => service.recordActivity(`Refresh failed: ${error.message}`, 'failure'));
    });
    document.getElementById('btn-co-dm-clear-activity')?.addEventListener('click', () => {
      state.activity = [];
      persist(STORAGE.activity, state.activity);
      renderActivity();
    });
    document.getElementById('co-dm-message-form')?.addEventListener('submit', async (event) => {
      event.preventDefault();
      const input = document.getElementById('co-dm-message');
      const message = input?.value || '';
      const delivery = document.getElementById('co-dm-message-delivery')?.value || 'player_flow';
      const result = document.getElementById('co-dm-message-result');
      const button = event.currentTarget?.querySelector('button[type="submit"]');
      const originalLabel = button?.textContent || 'Send or Stage Message';

      if (button) {
        button.disabled = true;
        button.textContent = delivery === 'player_flow' ? 'Sending…' : 'Staging…';
      }
      if (result) {
        result.textContent = delivery === 'player_flow' ? 'Delivering to Player Flow…' : 'Creating a review card…';
        result.className = 'inline-message info';
      }

      try {
        const receipt = await service.sendMessage({ message, delivery });
        if (input) input.value = '';
        if (result) {
          result.textContent = receipt.status === 'pending_review'
            ? 'Message staged. Review it in the DM Intervention Queue.'
            : `Delivered to ${receipt.player?.name || 'the selected player'} and saved in Player Flow.`;
          result.className = 'inline-message success';
        }
        document.dispatchEvent(new CustomEvent('co-dm-message-delivered', {
          detail: { playerId: receipt.player?.id || state.selectedPlayerId, status: receipt.status }
        }));
      } catch (error) {
        service.recordActivity(`DM message failed: ${error.message}`, 'failure');
        if (result) {
          result.textContent = `Message failed: ${error.message}`;
          result.className = 'inline-message error';
        }
      } finally {
        if (button) {
          button.textContent = originalLabel;
          button.disabled = !state.selectedPlayerId;
        }
      }
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
      if (approve) {
        const pending = service.approveProposal(approve.dataset.coDmApprove).catch((error) => {
          service.recordActivity(`Approval failed before completion: ${error.message}`, 'failure');
          return null;
        });
        state.approvalInFlight = pending;
        void pending.finally(() => {
          if (state.approvalInFlight === pending) state.approvalInFlight = null;
        });
      }
      if (reject) void service.rejectProposal(reject.dataset.coDmReject);
    });
    document.getElementById('co-dm-scene')?.addEventListener('keydown', (event) => {
      if (event.key !== 'Enter' && event.key !== ' ') return;
      const card = event.target.closest('[data-co-dm-focus][role="button"]');
      if (!card) return;
      event.preventDefault();
      card.click();
    });
    document.getElementById('co-dm-scene')?.addEventListener('click', (event) => {
      if (event.target.closest('[data-co-dm-unfocus]')) {
        void service.showAllPlayers({ record: true })
          .catch((error) => service.recordActivity(`Live feed refresh failed: ${error.message}`, 'failure'));
        return;
      }
      const focus = event.target.closest('[data-co-dm-focus]');
      if (focus) {
        void service.selectPlayer(focus.dataset.coDmFocus, { record: true })
          .catch((error) => service.recordActivity(`Player focus failed: ${error.message}`, 'failure'));
        return;
      }
      const hold = event.target.closest('[data-co-dm-hold]');
      const resume = event.target.closest('[data-co-dm-resume]');
      if (!hold && !resume) return;
      const isResume = !!resume;
      void service.createProposal({
        kind: isResume ? 'resume_player' : 'pause_player',
        title: isResume ? 'Resume player commands' : 'Hold for DM review',
        rationale: isResume ? 'The Dungeon Master has resolved the intervention.' : 'Pause consequential commands while the Dungeon Master reviews this scene.',
        holdReason: isResume ? undefined : 'The Dungeon Master is reviewing a consequential moment.'
      }).catch((error) => service.recordActivity(`Hold proposal failed: ${error.message}`, 'failure'));
    });
  }

  function validateProposal(input) {
    const playerId = selectedPlayerId();
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

    const message = bounded(input?.message, 801);
    if (message.length > 800) throw new Error('message must be at most 800 characters.');
    let payload;
    let summary;
    if (kind === 'grant_item') {
      rejectFields(['statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId', 'spellId', 'targetEntityId', 'holdReason']);
      const itemId = bounded(input.itemId, 120);
      const quantity = integer(input.quantity, 1);
      if (!itemId) throw new Error('itemId is required for grant_item.');
      if (quantity < 1 || quantity > 20) throw new Error('quantity must be between 1 and 20.');
      payload = { itemId, quantity, ...(message ? { message } : {}) };
      summary = `Grant ${quantity} × registered item ${itemId}.`;
    } else if (kind === 'apply_status') {
      rejectFields(['itemId', 'quantity', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId', 'spellId', 'targetEntityId', 'holdReason']);
      const statusName = bounded(input.statusName, 120);
      const statusDescription = bounded(input.statusDescription, 500);
      const durationTurns = integer(input.durationTurns, 3);
      if (!statusName) throw new Error('statusName is required for apply_status.');
      if (durationTurns < 1 || durationTurns > 50) throw new Error('durationTurns must be between 1 and 50.');
      payload = { statusName, statusDescription, durationTurns, ...(message ? { message } : {}) };
      summary = `Apply status “${statusName}” for ${durationTurns} turn(s).`;
    } else if (kind === 'adjust_resources') {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'destinationRoomId', 'spellId', 'targetEntityId', 'holdReason']);
      payload = {
        hpDelta: integer(input.hpDelta), mpDelta: integer(input.mpDelta),
        goldDelta: integer(input.goldDelta), xpDelta: integer(input.xpDelta), ...(message ? { message } : {})
      };
      if (!Object.values(payload).some((value) => value !== 0)) throw new Error('At least one resource delta must be non-zero.');
      if (Object.values(payload).some((value) => Math.abs(value) > 10000)) throw new Error('Resource deltas are capped at 10000 per proposal.');
      summary = `Adjust resources: HP ${payload.hpDelta}, MP ${payload.mpDelta}, gold ${payload.goldDelta}, XP ${payload.xpDelta}.`;
    } else if (kind === 'teleport') {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'spellId', 'targetEntityId', 'holdReason']);
      const destinationRoomId = bounded(input.destinationRoomId, 120);
      if (!destinationRoomId) throw new Error('destinationRoomId is required for teleport.');
      payload = { destinationRoomId, ...(message ? { message } : {}) };
      summary = `Teleport to existing room ${destinationRoomId}.`;
    } else if (kind === 'pause_player') {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId', 'spellId', 'targetEntityId', 'message']);
      const holdReason = bounded(input.holdReason || 'The Dungeon Master is reviewing this scene.', 301);
      if (holdReason.length > 300) throw new Error('holdReason must be at most 300 characters.');
      payload = { holdReason };
      summary = 'Hold future consequential player commands for DM review.';
    } else if (kind === 'resume_player') {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId', 'spellId', 'targetEntityId', 'holdReason', 'message']);
      payload = {};
      summary = 'Release the DM review hold and resume normal play.';
    } else {
      rejectFields(['itemId', 'quantity', 'statusName', 'statusDescription', 'durationTurns', 'hpDelta', 'mpDelta', 'goldDelta', 'xpDelta', 'destinationRoomId', 'holdReason']);
      const spellId = bounded(input.spellId, 120);
      const targetEntityId = bounded(input.targetEntityId, 120);
      if (!spellId || !targetEntityId) throw new Error('spellId and targetEntityId are required for invoke_registered_spell.');
      if (!message) throw new Error('message is required so the player can preview the spell narration.');
      payload = { spellId, targetEntityId, message };
      summary = `Invoke registered spell ${spellId} on ${targetEntityId}; the server will calculate its effect.`;
    }
    return { playerId, kind, title, rationale, evidenceIds, payload, summary };
  }

  async function fetchEntity(type, id, worldId, signal) {
    const registryTypes = { spell: 'spells', item: 'items', class: 'classes', race: 'races', monster: 'monsters', quest: 'quests', lore_entry: 'lore_entries', narrator_preset: 'narrator_presets' };
    const belongsToWorld = (item) => !worldId || (item?.worldIds || []).some((candidate) => candidate.toLowerCase() === worldId.toLowerCase());
    if (registryTypes[type]) {
      const item = await API.getRegistryEntry(registryTypes[type], id, { signal });
      return item && belongsToWorld(item) ? item : null;
    }
    if (type === 'player') {
      const player = await API.getPlayer(id, { signal });
      return player && (!worldId || player.activeWorldId?.toLowerCase() === worldId.toLowerCase()) ? player : null;
    }
    if (type === 'room') {
      const room = await API.getRoom(id, undefined, { signal });
      return room && belongsToWorld(room) ? room : null;
    }
    if (type === 'npc') {
      const rooms = await API.getRooms({ signal });
      for (const room of rooms) {
        if (worldId && !(room.worldIds || []).some((candidate) => candidate.toLowerCase() === worldId.toLowerCase())) continue;
        const npc = (room.npcs || []).find((candidate) => candidate.id === id);
        if (npc) return { ...npc, _roomId: room.id };
      }
    }
    return null;
  }

  function mergeServerActions(actions) {
    const sessionSecrets = readApprovalTokens();
    const localSecrets = new Map(state.proposals.map((proposal) => [proposal.requestId || proposal.id, proposal.approvalToken || sessionSecrets[proposal.requestId]]));
    state.proposals = (Array.isArray(actions) ? actions : []).filter((action) => action.kind !== 'player_flow').slice(0, MAX_PROPOSALS).reverse().map((action) => ({
      ...action,
      playerName: state.players.find((player) => player.id === action.playerId)?.name || action.playerId,
      approvalToken: localSecrets.get(action.requestId) || null
    }));
    persistProposals();
    renderProposals();
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
      const [players, actions, story] = await Promise.all([
        API.getPlayers(),
        API.getCoDmActions().catch(() => []),
        API.getStory(null, 50).catch(() => [])
      ]);
      state.players = players;
      state.liveFeed = (story || []).map(compactStory)
        .filter((entry) => entry.playerId)
        .sort((left, right) => new Date(right.timestamp || 0) - new Date(left.timestamp || 0));
      if (state.selectedPlayerId && !state.players.some((player) => player.id === state.selectedPlayerId)) {
        state.selectedPlayerId = '';
        state.context = null;
        persistText(STORAGE.player, '');
      }
      renderPlayerOptions();
      mergeServerActions(actions);
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
      renderDiagnostics();
    },

    handleGameEvent(event = {}) {
      if (!state.selectedPlayerId) {
        window.clearTimeout(state.eventRefreshTimer);
        state.eventRefreshTimer = window.setTimeout(() => {
          void this.refreshLiveFeed({ record: false }).catch((error) => {
            console.warn('Co-DM live feed refresh failed; the manual refresh lever still functions.', error);
          });
        }, 150);
        return;
      }
      if (event.playerId && event.playerId !== state.selectedPlayerId) return;
      window.clearTimeout(state.eventRefreshTimer);
      state.eventRefreshTimer = window.setTimeout(() => {
        void this.refreshContext({ record: false }).catch((error) => {
          console.warn('Co-DM event refresh failed; the manual refresh lever still functions.', error);
        });
      }, 150);
    },

    async showAllPlayers(options = {}) {
      state.selectedPlayerId = '';
      state.context = null;
      state.sheetPlayer = null;
      persistText(STORAGE.player, '');
      return this.refreshLiveFeed(options);
    },

    async refreshLiveFeed(options = {}) {
      if (!state.authenticated) throw domainError('AUTH_REQUIRED', 'An authenticated admin session is required.');
      const [players, story] = await Promise.all([
        API.getPlayers({ signal: options.signal }),
        API.getStory(null, 50, undefined, { signal: options.signal })
      ]);
      state.players = Array.isArray(players) ? players : [];
      state.liveFeed = (story || []).map(compactStory)
        .filter((entry) => entry.playerId)
        .sort((left, right) => new Date(right.timestamp || 0) - new Date(left.timestamp || 0));
      state.context = null;
      renderPlayerOptions();
      renderScene();
      renderDiagnostics();
      if (options.record !== false) this.recordActivity('Refreshed the all-player live feed.', 'info');
      return { players: clone(state.players), recentStory: clone(state.liveFeed) };
    },

    async selectPlayer(playerId, options = {}) {
      const id = ensurePlayerId(playerId);
      const previousPlayerId = state.selectedPlayerId;
      const previousContext = state.context;

      // Claim the visible selection before network work begins. A browser agent may call a
      // tool immediately after the change event, particularly against a higher-latency host.
      state.selectedPlayerId = id;
      state.context = null;
      persistText(STORAGE.player, id);
      renderPlayerOptions();
      renderScene();

      try {
        const player = await API.getPlayer(id);
        if (!player) throw new Error(`Player '${id}' was not found.`);
        if (!state.players.some((entry) => entry.id === id)) state.players.push(player);
        const context = await this.refreshContext({ storyLimit: options.storyLimit, record: false });
        if (options.record !== false) this.recordActivity(`Selected ${player.name || id} for Co-DM inspection.`, 'info');
        return context;
      } catch (error) {
        if (state.selectedPlayerId === id) {
          state.selectedPlayerId = previousPlayerId;
          state.context = previousContext;
          persistText(STORAGE.player, previousPlayerId);
          renderPlayerOptions();
          renderScene();
        }
        throw error;
      }
    },

    async refreshContext(options = {}) {
      const playerId = selectedPlayerId();
      const storyLimit = Math.max(1, Math.min(12, integer(options.storyLimit, 8)));
      const signal = options.signal;
      const player = await API.getPlayer(playerId, { signal });
      if (!player) throw new Error(`Player '${playerId}' was not found.`);
      state.sheetPlayer = player;
      const [room, story, health] = await Promise.all([
        API.getRoom(player.currentRoomId, playerId, { signal }),
        API.getStory(playerId, storyLimit, player.activeWorldId, { signal }),
        API.getHealth({ signal }).catch((error) => error?.name === 'AbortError' ? Promise.reject(error) : null)
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
          currentRoomId: bounded(player.currentRoomId, 120), activeWorldId: bounded(player.activeWorldId, 120),
          commandHold: player.commandHold ? {
            reason: bounded(player.commandHold.reason, 300), heldBy: bounded(player.commandHold.heldBy, 120),
            heldAt: player.commandHold.heldAt || null, sourceActionId: bounded(player.commandHold.sourceActionId, 120)
          } : null
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
      renderDiagnostics();
      if (options.record !== false) this.recordActivity(`Inspected ${player.name}'s current scene.`, 'info');
      return clone(state.context);
    },

    async getContext(input = {}, options = {}) {
      if (Object.keys(input).length) throw new Error('Context input must be empty; player scope comes from the visible selection.');
      if (state.approvalInFlight) await state.approvalInFlight;
      const context = await this.refreshContext({ storyLimit: 8, record: false, signal: options.signal });
      this.recordActivity(`Agent inspected ${context.player.name}'s current scene.`, 'info');
      return context;
    },

    async searchWorld(input = {}, options = {}) {
      const unknown = Object.keys(input).filter((name) => !['query', 'entityTypes', 'limit'].includes(name));
      if (unknown.length) throw new Error(`Unsupported search field '${unknown[0]}'.`);
      const query = bounded(input.query, 160);
      const categories = Array.isArray(input.entityTypes) ? [...new Set(input.entityTypes)] : [];
      const types = categories.map((category) => ENTITY_CATEGORY_MAP[category]);
      const limit = Math.max(1, Math.min(8, integer(input.limit, 6)));
      if (!query) throw new Error('query must be nonblank.');
      if (types.some((type) => !type) || types.length > 6) throw new Error('entityTypes contains an unsupported category.');
      if (!state.context) await this.refreshContext({ record: false, signal: options.signal });
      const worldId = state.context?.player?.activeWorldId;
      const responses = types.length
        ? await Promise.all(types.map((type) => API.dmSearch(query, type, worldId, { signal: options.signal })))
        : [await API.dmSearch(query, undefined, worldId, { signal: options.signal })];
      const raw = responses.flatMap((data) => data.results || [])
        .filter((item) => item.type !== 'player' || item.id === state.selectedPlayerId)
        .slice(0, limit);
      state.entityIndex.clear();
      for (const item of raw) state.entityIndex.set(String(item.id).toLowerCase(), item.type);
      const searchInput = document.getElementById('overview-search-input');
      const typeFilter = document.getElementById('overview-type-filter');
      const worldFilter = document.getElementById('overview-world-filter');
      if (searchInput) searchInput.value = query;
      if (typeFilter) typeFilter.value = types.length === 1 ? types[0] : '';
      if (worldFilter && [...worldFilter.options].some((option) => option.value === worldId)) worldFilter.value = worldId;
      UI._ovRenderResults(raw, query);
      this.recordActivity(`Agent searched the selected campaign for “${query}” and found ${raw.length} result(s).`, 'info');
      return { query, worldId, returned: raw.length, results: raw.map(compactSearchResult) };
    },

    async inspectEntity(input = {}, options = {}) {
      const unknown = Object.keys(input).filter((name) => name !== 'entityId');
      if (unknown.length) throw new Error(`Unsupported inspection field '${unknown[0]}'.`);
      const id = bounded(input.entityId, 120);
      if (!id) throw new Error('entityId is required.');
      if (!state.context) await this.refreshContext({ record: false, signal: options.signal });
      const worldId = bounded(state.context?.player?.activeWorldId || '', 120);
      let type = state.entityIndex.get(id.toLowerCase());
      if (!type && state.context?.player?.id?.toLowerCase() === id.toLowerCase()) type = 'player';
      if (!type && state.context?.room?.id?.toLowerCase() === id.toLowerCase()) type = 'room';
      if (!type && (state.context?.room?.npcs || []).some((entry) => entry.id?.toLowerCase() === id.toLowerCase())) type = 'npc';
      if (!type && (state.context?.room?.items || []).some((entry) => entry.id?.toLowerCase() === id.toLowerCase())) type = 'item';
      if (!type) {
        const search = await API.dmSearch(id, undefined, worldId, { signal: options.signal });
        const exact = (search.results || []).filter((entry) => String(entry.id).toLowerCase() === id.toLowerCase())
          .filter((entry) => entry.type !== 'player' || entry.id === state.selectedPlayerId);
        const exactTypes = [...new Set(exact.map((entry) => entry.type))];
        if (exactTypes.length > 1) throw domainError('CONFLICT', `entityId '${id}' is ambiguous in the selected campaign.`);
        type = exactTypes[0];
      }
      if (!ENTITY_TYPES.has(type)) throw domainError('NOT_FOUND', `Entity '${id}' was not found in the selected campaign.`);
      const item = await fetchEntity(type, id, worldId, options.signal);
      if (!item) throw domainError('NOT_FOUND', `${type} '${id}' was not found.`);
      UI.ovSelectItem(item, type);
      state.selectedEntity = compactEntity(item, type);
      if (state.context) state.context.selectedEntity = clone(state.selectedEntity);
      this.recordActivity(`Agent inspected ${type} ${item.name || id}.`, 'info');
      return clone(state.selectedEntity);
    },

    async sendMessage(input = {}, options = {}) {
      const unknown = Object.keys(input).filter((name) => !['message', 'delivery'].includes(name));
      if (unknown.length) throw new Error(`Unsupported message field '${unknown[0]}'.`);
      const playerId = selectedPlayerId();
      const message = bounded(input.message, 801);
      const delivery = bounded(input.delivery, 40);
      if (!message) throw new Error('message must be nonblank.');
      if (message.length > 800) throw new Error('message must be at most 800 characters.');
      if (!MESSAGE_DELIVERIES.has(delivery)) throw new Error('delivery must be player_flow or player_flow_and_discord.');

      if (delivery === 'player_flow_and_discord') {
        const token = approvalToken();
        const action = await API.createCoDmProposal({
          requestId: uuid('request'),
          approvalToken: token,
          playerId,
          kind: delivery,
          title: 'Review external player message',
          rationale: 'Discord is an external delivery channel and requires explicit human confirmation.',
          evidenceIds: [],
          message
        }, { signal: options.signal });
        action.playerName = state.players.find((player) => player.id === playerId)?.name || playerId;
        action.approvalToken = token;
        state.proposals.push(action);
        state.proposals = state.proposals.slice(-MAX_PROPOSALS);
        persistProposals();
        renderProposals();
        this.recordActivity(`Agent staged an external message to ${action.playerName}; human confirmation is required.`, 'info');
        return { summary: 'External delivery is pending human review.', status: 'pending_review', actionId: action.id, delivery, player: { id: playerId, name: action.playerName } };
      }

      const result = await API.sendCoDmPlayerFlowMessage({ requestId: uuid('request'), playerId, message, delivery }, { signal: options.signal });
      const context = await this.refreshContext({ record: false, signal: options.signal });
      state.diagnostics.mostRecentVisibleMutation = `DM message to ${context.player.name}`;
      renderDiagnostics();
      this.recordActivity(`Agent sent a visible DM message to ${context.player.name}.`, 'success');
      return {
        summary: 'Player Flow message delivered to one selected player.',
        status: result.status,
        actionId: result.id,
        delivery,
        player: { id: context.player.id, name: context.player.name },
        receiptId: context.recentStory[0]?.id || null
      };
    },

    async createProposal(input = {}, options = {}) {
      const valid = validateProposal(input);
      const player = await API.getPlayer(valid.playerId, { signal: options.signal });
      if (!player) throw new Error(`Player '${valid.playerId}' was not found.`);
      const token = approvalToken();
      const proposal = await API.createCoDmProposal({
        requestId: uuid('request'),
        approvalToken: token,
        playerId: valid.playerId,
        kind: valid.kind,
        title: valid.title,
        rationale: valid.rationale,
        evidenceIds: valid.evidenceIds,
        ...valid.payload
      }, { signal: options.signal });
      proposal.playerName = bounded(player.name || valid.playerId, 160);
      proposal.approvalToken = token;
      state.proposals.push(proposal);
      state.proposals = state.proposals.slice(-MAX_PROPOSALS);
      persistProposals();
      renderProposals();
      this.recordActivity(`Agent proposed ${valid.summary} Human review is required.`, 'info');
      requestAnimationFrame(() => document.getElementById(`co-dm-proposal-${proposal.id}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
      return { summary: 'Mechanical change is pending human review.', status: proposal.status, actionId: proposal.id, kind: proposal.kind, player: { id: valid.playerId, name: proposal.playerName } };
    },

    listProposals() {
      return clone(state.proposals);
    },

    async approveProposal(proposalId) {
      const proposal = state.proposals.find((entry) => entry.id === proposalId);
      if (!proposal) throw new Error(`Proposal '${proposalId}' was not found.`);
      if (proposal.status !== 'pending') throw new Error(`Proposal '${proposalId}' is already ${proposal.status}.`);
      if (!proposal.approvalToken) throw new Error('This proposal cannot be approved after its local approval secret was lost. Reject it and create a fresh proposal.');
      proposal.status = 'processing';
      proposal.result = 'Approval in progress…';
      renderProposals();
      try {
        const result = await API.approveCoDmAction(proposal.id, proposal.approvalToken);
        Object.assign(proposal, result);
        proposal.result = bounded(result?.result || 'Existing game API accepted the reviewed action.', 500);
        state.diagnostics.mostRecentVisibleMutation = `${proposal.kind} for ${proposal.playerName}`;
        this.recordActivity(`Human approved “${proposal.title}”. ${proposal.result}`, 'success');
        if (proposal.playerId === state.selectedPlayerId) await this.refreshContext({ record: false });
      } catch (error) {
        proposal.status = 'failed';
        proposal.result = bounded(error.message || 'Approval failed.', 500);
        this.recordActivity(`Approval failed for “${proposal.title}”: ${proposal.result}`, 'failure');
      }
      persistProposals();
      renderProposals();
      renderDiagnostics();
      return clone(proposal);
    },

    async rejectProposal(proposalId) {
      const proposal = state.proposals.find((entry) => entry.id === proposalId);
      if (!proposal) throw new Error(`Proposal '${proposalId}' was not found.`);
      if (proposal.status !== 'pending') throw new Error(`Proposal '${proposalId}' is already ${proposal.status}.`);
      if (!proposal.approvalToken) throw new Error('This proposal cannot be rejected after its local approval secret was lost.');
      const result = await API.rejectCoDmAction(proposal.id, proposal.approvalToken);
      Object.assign(proposal, result);
      proposal.result = result.result;
      persistProposals();
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
      renderDiagnostics();
    }
  };

  window.GaeCoDm = service;
  service.bootstrap();
})();
