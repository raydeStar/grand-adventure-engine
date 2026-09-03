// webmcp.js — lifecycle-bound, same-origin WebMCP tools for the authenticated DM Console.
(function () {
  'use strict';

  const OUTPUT_BUDGET = 1500;
  const OUTPUT_CONTRACTS = Object.freeze({
    get_selected_player_context: Object.freeze({ budgetChars: OUTPUT_BUDGET, classification: 'untrusted_game_content', readOnly: true, containsUntrustedOutput: true, consequential: false, expectedOutputContract: 'playerContextResult' }),
    search_campaign_world: Object.freeze({ budgetChars: OUTPUT_BUDGET, classification: 'untrusted_game_content', readOnly: true, containsUntrustedOutput: true, consequential: false, expectedOutputContract: 'campaignSearchResult' }),
    inspect_campaign_entity: Object.freeze({ budgetChars: OUTPUT_BUDGET, classification: 'untrusted_game_content', readOnly: true, containsUntrustedOutput: true, consequential: false, expectedOutputContract: 'campaignEntityResult' }),
    send_player_message: Object.freeze({ budgetChars: OUTPUT_BUDGET, classification: 'trusted_receipt', readOnly: false, containsUntrustedOutput: false, consequential: true, expectedOutputContract: 'playerMessageResult' }),
    propose_mechanical_change: Object.freeze({ budgetChars: OUTPUT_BUDGET, classification: 'trusted_receipt', readOnly: false, containsUntrustedOutput: false, consequential: true, expectedOutputContract: 'mechanicalProposalResult' })
  });
  const SUCCESS_CODES = Object.freeze({
    get_selected_player_context: 'PLAYER_CONTEXT_RETRIEVED',
    search_campaign_world: 'CAMPAIGN_SEARCH_COMPLETED',
    inspect_campaign_entity: 'CAMPAIGN_ENTITY_RETRIEVED',
    send_player_message: 'PLAYER_MESSAGE_DELIVERED',
    propose_mechanical_change: 'MECHANICAL_PROPOSAL_CREATED'
  });
  const SUCCESS_MESSAGES = Object.freeze({
    get_selected_player_context: 'Selected player context retrieved.',
    search_campaign_world: 'Campaign search completed.',
    inspect_campaign_entity: 'Campaign entity retrieved.',
    send_player_message: 'Selected-player message request completed.',
    propose_mechanical_change: 'Mechanical proposal created; campaign state remains unchanged pending review.'
  });
  const ERROR_MESSAGES = Object.freeze({
    INVALID_INPUT: 'The tool input failed validation.',
    AUTH_REQUIRED: 'An authenticated session is required.',
    FORBIDDEN: 'The current user is not authorized to perform this action.',
    NO_PLAYER_SELECTED: 'No player is selected. Even prophecy needs a subject.',
    NOT_FOUND: 'No authorized entity matched that identifier.',
    CONFLICT: 'The requested state transition conflicts with current application state.',
    REQUEST_REJECTED: 'The request was rejected.',
    NETWORK_ERROR: 'The application service could not be reached.',
    UNEXPECTED_ERROR: 'The operation could not be completed.'
  });
  let registrationController = null;
  let registrationPromise = null;
  let registeredNames = [];

  function deepFreeze(value) {
    if (!value || typeof value !== 'object' || Object.isFrozen(value)) return value;
    Object.freeze(value);
    for (const child of Object.values(value)) deepFreeze(child);
    return value;
  }

  function invalid(message) {
    const error = new Error(message);
    error.code = 'INVALID_INPUT';
    return error;
  }

  function validateValue(value, schema, path) {
    if (schema.type === 'object') {
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw invalid(`${path} must be an object.`);
      const properties = schema.properties || {};
      for (const name of (schema.required || [])) {
        if (value[name] === undefined || value[name] === null || value[name] === '') throw invalid(`${path}.${name} is required.`);
      }
      if (schema.additionalProperties === false) {
        const unknown = Object.keys(value).filter((name) => !Object.hasOwn(properties, name));
        if (unknown.length) throw invalid(`${path} contains unsupported field '${unknown[0]}'.`);
      }
      for (const [name, child] of Object.entries(value)) {
        if (properties[name] && child !== undefined) validateValue(child, properties[name], `${path}.${name}`);
      }
      return;
    }
    if (schema.type === 'string') {
      if (typeof value !== 'string') throw invalid(`${path} must be a string.`);
      const length = value.trim().length;
      if (schema.minLength != null && length < schema.minLength) throw invalid(`${path} is too short.`);
      if (schema.maxLength != null && length > schema.maxLength) throw invalid(`${path} is too long.`);
      if (schema.enum && !schema.enum.includes(value)) throw invalid(`${path} has an unsupported value.`);
      return;
    }
    if (schema.type === 'integer') {
      if (!Number.isInteger(value)) throw invalid(`${path} must be an integer.`);
      if (schema.minimum != null && value < schema.minimum) throw invalid(`${path} is below the minimum.`);
      if (schema.maximum != null && value > schema.maximum) throw invalid(`${path} exceeds the maximum.`);
      return;
    }
    if (schema.type === 'array') {
      if (!Array.isArray(value)) throw invalid(`${path} must be an array.`);
      if (schema.maxItems != null && value.length > schema.maxItems) throw invalid(`${path} has too many entries.`);
      if (schema.uniqueItems && new Set(value).size !== value.length) throw invalid(`${path} must contain unique entries.`);
      value.forEach((child, index) => validateValue(child, schema.items, `${path}[${index}]`));
    }
  }

  function projectContext(data) {
    return {
      player: data.player ? {
        id: data.player.id,
        name: data.player.name,
        level: data.player.level,
        hp: data.player.hp,
        maxHp: data.player.maxHp,
        mp: data.player.mp,
        maxMp: data.player.maxMp,
        gold: data.player.gold,
        currentRoomId: data.player.currentRoomId,
        activeWorldId: data.player.activeWorldId,
        commandHold: data.player.commandHold || null
      } : null,
      room: data.room ? {
        id: data.room.id,
        name: data.room.name,
        description: String(data.room.description || '').slice(0, 180),
        exits: (data.room.exits || []).slice(0, 4),
        npcs: (data.room.npcs || []).slice(0, 4).map((item) => ({ id: item.id, name: item.name, count: item.count })),
        items: (data.room.items || []).slice(0, 4).map((item) => ({ id: item.id, name: item.name, count: item.count }))
      } : null,
      interaction: data.interaction,
      activeQuests: (data.activeQuests || []).slice(0, 3).map((quest) => ({ questId: quest.questId, status: quest.status, summary: String(quest.summary || '').slice(0, 100) })),
      statusEffects: (data.statusEffects || []).slice(0, 3).map((effect) => ({ id: effect.id, name: effect.name, type: effect.type, remainingTurns: effect.remainingTurns })),
      recentStory: (data.recentStory || []).slice(0, 2).map((entry) => ({ id: entry.id, rawInput: String(entry.rawInput || '').slice(0, 60), narration: String(entry.narration || entry.mechanicalSummary || '').slice(0, 120) })),
      capabilities: data.capabilities,
      limitations: (data.limitations || []).slice(0, 4)
    };
  }

  function projectResult(name, data) {
    if (name === 'get_selected_player_context') return projectContext(data);
    if (name === 'search_campaign_world') return {
      query: data.query,
      returned: data.returned,
      results: (data.results || []).slice(0, 6).map((item) => ({ entityId: item.id, entityType: item.type, name: item.name, summary: String(item.summary || '').slice(0, 160) }))
    };
    if (name === 'inspect_campaign_entity') {
      const { type, id, name: entityName, description, worldIds, ...details } = data;
      return {
        entityId: id,
        entityType: type,
        name: entityName,
        description: String(description || details.summary || '').slice(0, 420),
        worldIds: (worldIds || []).slice(0, 3),
        details
      };
    }
    if (name === 'send_player_message') return {
      actionId: data.actionId,
      status: data.status,
      delivery: data.delivery,
      playerId: data.player?.id,
      receiptId: data.receiptId || null,
      playerFlowPersisted: data.delivery === 'player_flow' && data.status === 'completed',
      discordMirrored: false
    };
    if (name === 'propose_mechanical_change') return {
      proposalId: data.actionId,
      status: data.status === 'pending' ? 'pending_review' : data.status,
      kind: data.kind,
      playerId: data.player?.id
    };
    return data;
  }

  function stableCode(error) {
    if (['INVALID_INPUT', 'AUTH_REQUIRED', 'FORBIDDEN', 'NO_PLAYER_SELECTED', 'NOT_FOUND', 'CONFLICT', 'REQUEST_REJECTED'].includes(error?.code)) return error.code;
    if (error?.status === 401) return 'AUTH_REQUIRED';
    if (error?.status === 403) return 'FORBIDDEN';
    if (error?.status === 404) return 'NOT_FOUND';
    if (error?.status === 409) return 'CONFLICT';
    if (error?.status >= 400 && error?.status < 500) return 'REQUEST_REJECTED';
    if (error instanceof TypeError) return 'NETWORK_ERROR';
    return 'UNEXPECTED_ERROR';
  }

  function shrinkData(name, data) {
    if (name === 'get_selected_player_context') return {
      player: data?.player ? { id: data.player.id, name: data.player.name, hp: data.player.hp, maxHp: data.player.maxHp, mp: data.player.mp, maxMp: data.player.maxMp, gold: data.player.gold, currentRoomId: data.player.currentRoomId, activeWorldId: data.player.activeWorldId, commandHold: data.player.commandHold || null } : null,
      room: data?.room ? { id: data.room.id, name: data.room.name, exits: (data.room.exits || []).slice(0, 4), npcs: (data.room.npcs || []).slice(0, 4).map((item) => ({ id: item.id, name: item.name })) } : null,
      interaction: data?.interaction,
      activeQuests: (data?.activeQuests || []).slice(0, 4).map((quest) => ({ questId: quest.questId, status: quest.status })),
      statusEffects: (data?.statusEffects || []).slice(0, 6),
      recentStory: (data?.recentStory || []).slice(0, 1).map((entry) => ({ id: entry.id, narration: String(entry.narration || '').slice(0, 80) }))
    };
    if (name === 'search_campaign_world') return { query: data?.query, returned: data?.returned, results: (data?.results || []).slice(0, 6).map((item) => ({ entityId: item.entityId, entityType: item.entityType, name: item.name })) };
    if (name === 'inspect_campaign_entity') return { entityId: data?.entityId, entityType: data?.entityType, name: data?.name, description: String(data?.description || '').slice(0, 240) };
    return data;
  }

  function boundedEnvelope(name, ok, code, message, data, retryable = false) {
    const contract = OUTPUT_CONTRACTS[name];
    let envelope = {
      ok,
      status: ok ? 'success' : 'error',
      code,
      message: String(message || '').slice(0, 240),
      summary: String(message || '').slice(0, 240),
      retryable,
      data: data ?? null,
      meta: { classification: contract.classification, truncated: false, budgetChars: contract.budgetChars, outputChars: 0 }
    };
    envelope.meta.outputChars = JSON.stringify(envelope).length;
    let serialized = JSON.stringify(envelope);
    if (serialized.length > contract.budgetChars) {
      envelope.data = shrinkData(name, envelope.data);
      envelope.meta.truncated = true;
      envelope.meta.outputChars = JSON.stringify(envelope).length;
      serialized = JSON.stringify(envelope);
    }
    if (serialized.length > contract.budgetChars) {
      envelope = {
        ...envelope,
        data: { availableIn: 'visible Co-DM panel', reason: 'output_budget' },
        meta: { ...envelope.meta, truncated: true }
      };
      envelope.meta.outputChars = JSON.stringify(envelope).length;
      serialized = JSON.stringify(envelope);
    }
    envelope.meta.outputChars = serialized.length;
    return envelope;
  }

  async function executeTool(definition, operation, input, options = {}) {
    window.GaeCoDm.setWebMcpDiagnostics({ mostRecentToolCall: definition.name });
    try {
      const value = input == null ? {} : input;
      validateValue(value, definition.inputSchema, 'input');
      const data = await operation(value, { signal: options?.signal });
      const pendingDiscord = definition.name === 'send_player_message' && data?.status === 'pending_review';
      const code = pendingDiscord ? 'DISCORD_DELIVERY_REVIEW_CREATED' : SUCCESS_CODES[definition.name];
      const message = pendingDiscord ? 'Discord delivery is pending visible human review.' : SUCCESS_MESSAGES[definition.name];
      return boundedEnvelope(definition.name, true, code, message, projectResult(definition.name, data));
    } catch (error) {
      if (error?.name === 'AbortError') throw error;
      const code = stableCode(error);
      window.GaeCoDm.recordActivity(`WebMCP ${definition.name} failed (${code}).`, 'failure');
      return boundedEnvelope(definition.name, false, code, ERROR_MESSAGES[code] || ERROR_MESSAGES.UNEXPECTED_ERROR, null, code === 'NETWORK_ERROR');
    }
  }

  const entityCategory = Object.freeze({
    type: 'string',
    enum: ['character', 'location', 'npc', 'item', 'spell', 'quest', 'lore'],
    description: 'Optional category within the selected player campaign.'
  });

  const definitions = [
    {
      name: 'get_selected_player_context',
      title: 'Read selected player context',
      description: 'Read the authoritative scene for the player visibly selected in the authenticated Co-DM panel. Does not change selection or game state.',
      inputSchema: { type: 'object', properties: {}, required: [], additionalProperties: false },
      annotations: { readOnlyHint: true, untrustedContentHint: true },
      operation: (input, options) => window.GaeCoDm.getContext(input, options)
    },
    {
      name: 'search_campaign_world',
      title: 'Search campaign world',
      description: 'Search bounded campaign entities in the selected player current world and render the same results shown to the human DM.',
      inputSchema: {
        type: 'object',
        properties: {
          query: { type: 'string', minLength: 1, maxLength: 160, description: 'Plain search text; returned content is untrusted game data.' },
          entityTypes: { type: 'array', maxItems: 6, uniqueItems: true, items: entityCategory, description: 'Optional bounded category filter.' },
          limit: { type: 'integer', minimum: 1, maximum: 8, description: 'Maximum compact results; defaults to 6.' }
        },
        required: ['query'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: true, untrustedContentHint: true },
      operation: (input, options) => window.GaeCoDm.searchWorld(input, options)
    },
    {
      name: 'inspect_campaign_entity',
      title: 'Inspect campaign entity',
      description: 'Inspect one exact entity ID from the selected player campaign and open it in the existing human DM detail panel.',
      inputSchema: {
        type: 'object',
        properties: { entityId: { type: 'string', minLength: 1, maxLength: 120, description: 'Exact ID returned by search or visible context.' } },
        required: ['entityId'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: true, untrustedContentHint: true },
      operation: (input, options) => window.GaeCoDm.inspectEntity(input, options)
    },
    {
      name: 'send_player_message',
      title: 'Send selected player message',
      description: 'Send one message to the visibly selected player. Player Flow delivery is immediate; Discord delivery creates a visible human-review card.',
      inputSchema: {
        type: 'object',
        properties: {
          message: { type: 'string', minLength: 1, maxLength: 800, description: 'Exact message for the selected player.' },
          delivery: { type: 'string', enum: ['player_flow', 'player_flow_and_discord'], description: 'Explicit destination. Discord always requires human review.' }
        },
        required: ['message', 'delivery'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: false, untrustedContentHint: false },
      operation: (input, options) => window.GaeCoDm.sendMessage(input, options)
    },
    {
      name: 'propose_mechanical_change',
      title: 'Propose mechanical change',
      description: 'Stage one bounded, reviewable DM intervention for the selected player. Supports holds, registered damage or healing spells, and optional final narration. The server calculates mechanics; nothing changes until the human DM approves the visible proposal.',
      inputSchema: {
        type: 'object',
        properties: {
          kind: { type: 'string', enum: ['grant_item', 'apply_status', 'adjust_resources', 'teleport', 'pause_player', 'resume_player', 'invoke_registered_spell'], description: 'Supported proposal kind.' },
          title: { type: 'string', minLength: 1, maxLength: 160, description: 'Short review-card title.' },
          rationale: { type: 'string', minLength: 1, maxLength: 800, description: 'Why this change follows from inspected evidence.' },
          evidenceIds: { type: 'array', maxItems: 8, uniqueItems: true, items: { type: 'string', minLength: 1, maxLength: 140 }, description: 'IDs supporting the proposal.' },
          message: { type: 'string', minLength: 1, maxLength: 800, description: 'Optional final player-facing narration delivered only after the mechanic succeeds; required for invoke_registered_spell.' },
          itemId: { type: 'string', minLength: 1, maxLength: 120 },
          quantity: { type: 'integer', minimum: 1, maximum: 20 },
          statusName: { type: 'string', minLength: 1, maxLength: 120 },
          statusDescription: { type: 'string', maxLength: 500 },
          durationTurns: { type: 'integer', minimum: 1, maximum: 50 },
          hpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          mpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          goldDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          xpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          destinationRoomId: { type: 'string', minLength: 1, maxLength: 120 },
          spellId: { type: 'string', minLength: 1, maxLength: 120, description: 'Exact registered spell ID returned by campaign search.' },
          targetEntityId: { type: 'string', minLength: 1, maxLength: 120, description: 'Exact current-scene target ID. Healing may target only the selected player.' },
          holdReason: { type: 'string', minLength: 1, maxLength: 300, description: 'Player-visible reason retained while pause_player is active.' }
        },
        required: ['kind', 'title', 'rationale'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: false, untrustedContentHint: false },
      operation: (input, options) => window.GaeCoDm.createProposal(input, options)
    }
  ].map((definition) => {
    const tool = {
      name: definition.name,
      title: definition.title,
      description: definition.description,
      inputSchema: definition.inputSchema,
      annotations: definition.annotations
    };
    tool.execute = (input, options) => executeTool(tool, definition.operation, input, options);
    return deepFreeze(tool);
  });

  const registry = {
    async register(options = {}) {
      const context = document.modelContext;
      const supported = typeof context?.registerTool === 'function';
      window.GaeCoDm.setWebMcpDiagnostics({ supported });
      if (!supported || !window.isSecureContext || window.top !== window.self || options.authenticated !== true || options.isAdmin !== true) {
        this.dispose();
        return [];
      }
      if (registrationPromise) return registrationPromise;
      if (registrationController && registeredNames.length === definitions.length) return [...registeredNames];

      registrationController = new AbortController();
      const signal = registrationController.signal;
      registeredNames = [];
      window.GaeCoDm.setWebMcpDiagnostics({ registrationAttempted: true, registrationError: null, registeredTools: [] });
      registrationPromise = (async () => {
        try {
          for (const tool of definitions) {
            await context.registerTool(tool, { signal });
            registeredNames.push(tool.name);
            window.GaeCoDm.setWebMcpDiagnostics({ registeredTools: [...registeredNames] });
          }
          return [...registeredNames];
        } catch (error) {
          registrationController?.abort();
          registrationController = null;
          registeredNames = [];
          const message = String(error?.message || 'WebMCP registration failed.').slice(0, 240);
          window.GaeCoDm.setWebMcpDiagnostics({ registrationError: message, registeredTools: [] });
          console.warn('WebMCP registration stumbled; the ordinary dashboard remains unharmed.', error);
          return [];
        } finally {
          registrationPromise = null;
        }
      })();
      return registrationPromise;
    },

    dispose() {
      registrationController?.abort();
      registrationController = null;
      registrationPromise = null;
      registeredNames = [];
      window.GaeCoDm?.setWebMcpDiagnostics({ registeredTools: [], registrationAttempted: false, mostRecentToolCall: null });
    },

    getDefinitions() {
      return [...definitions];
    },

    getOutputContracts() {
      return OUTPUT_CONTRACTS;
    }
  };

  window.GaeWebMcp = Object.freeze(registry);
  window.GaeCoDm.setWebMcpDiagnostics({ supported: typeof document.modelContext?.registerTool === 'function' && window.isSecureContext });
})();
