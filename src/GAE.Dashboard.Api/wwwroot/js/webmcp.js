// webmcp.js — narrow top-level site tools for the signed-in DM Console.
(async function () {
  'use strict';

  const context = document.modelContext;
  const canRegister = typeof context?.registerTool === 'function';
  window.GaeCoDm.setWebMcpDiagnostics({ supported: canRegister });
  if (!canRegister) return;

  const registered = document.__gaeWebMcpTools || new Set();
  document.__gaeWebMcpTools = registered;
  window.GaeCoDm.setWebMcpDiagnostics({ registrationAttempted: true });

  const entityType = {
    type: 'string',
    enum: ['player', 'room', 'npc', 'item', 'spell', 'class', 'race', 'quest', 'monster', 'narrator_preset', 'lore_entry']
  };

  async function execute(name, operation, input) {
    window.GaeCoDm.setWebMcpDiagnostics({ mostRecentToolCall: name });
    try {
      return { ok: true, data: await operation(input || {}) };
    } catch (error) {
      const message = String(error?.message || error || 'The tool call failed.').slice(0, 500);
      window.GaeCoDm.recordActivity(`WebMCP ${name} failed: ${message}`, 'failure');
      return { ok: false, error: message };
    }
  }

  const tools = [
    {
      name: 'get_dm_context',
      description: 'Reads authoritative Grand Adventure Engine state for exactly one player and does not modify the game. Omit playerId to use the player visibly selected in the Co-DM panel.',
      inputSchema: {
        type: 'object',
        properties: {
          playerId: { type: 'string', minLength: 1, maxLength: 120 },
          storyLimit: { type: 'integer', minimum: 1, maximum: 12, default: 8 }
        },
        additionalProperties: false
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      execute: (input) => execute('get_dm_context', window.GaeCoDm.getContext.bind(window.GaeCoDm), input)
    },
    {
      name: 'search_world',
      description: 'Searches the actual Grand Adventure Engine world and visibly renders compact results in the signed-in DM Console. It is read-only.',
      inputSchema: {
        type: 'object',
        properties: {
          query: { type: 'string', minLength: 1, maxLength: 200 },
          type: entityType,
          worldId: { type: 'string', minLength: 1, maxLength: 120 },
          limit: { type: 'integer', minimum: 1, maximum: 20, default: 10 }
        },
        required: ['query'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      execute: (input) => execute('search_world', window.GaeCoDm.searchWorld.bind(window.GaeCoDm), input)
    },
    {
      name: 'inspect_entity',
      description: 'Inspects one exact player, room, NPC, item, spell, class, race, quest, monster, narrator preset, or lore entry through existing DM browse data and visibly opens it in the DM detail panel. It is read-only.',
      inputSchema: {
        type: 'object',
        properties: {
          type: entityType,
          id: { type: 'string', minLength: 1, maxLength: 120 },
          worldId: { type: 'string', minLength: 1, maxLength: 120 }
        },
        required: ['type', 'id'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
      execute: (input) => execute('inspect_entity', window.GaeCoDm.inspectEntity.bind(window.GaeCoDm), input)
    },
    {
      name: 'send_dm_message',
      description: 'Sends a visible Dungeon Master message to exactly one selected player. If Discord mirroring is configured for that player, the message may also appear in their Discord thread. This tool does not modify quests, inventory, resources, rooms, or NPC state.',
      inputSchema: {
        type: 'object',
        properties: {
          playerId: { type: 'string', minLength: 1, maxLength: 120 },
          message: { type: 'string', minLength: 1, maxLength: 800 }
        },
        required: ['playerId', 'message'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: true },
      execute: (input) => execute('send_dm_message', window.GaeCoDm.sendMessage.bind(window.GaeCoDm), input)
    },
    {
      name: 'propose_dm_intervention',
      description: 'Creates a visible Co-DM intervention proposal for human review. It does not change game state. A human Dungeon Master must approve the proposal in the DM Console before the existing game APIs are called.',
      inputSchema: {
        type: 'object',
        properties: {
          playerId: { type: 'string', minLength: 1, maxLength: 120 },
          kind: { type: 'string', enum: ['grant_item', 'apply_status', 'adjust_resources', 'teleport'] },
          title: { type: 'string', minLength: 1, maxLength: 160 },
          rationale: { type: 'string', minLength: 1, maxLength: 800 },
          evidenceIds: { type: 'array', maxItems: 10, items: { type: 'string', minLength: 1, maxLength: 140 } },
          itemId: { type: 'string', minLength: 1, maxLength: 120 },
          quantity: { type: 'integer', minimum: 1, maximum: 20 },
          statusName: { type: 'string', minLength: 1, maxLength: 120 },
          statusDescription: { type: 'string', maxLength: 500 },
          durationTurns: { type: 'integer', minimum: 1, maximum: 50 },
          hpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          mpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          goldDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          xpDelta: { type: 'integer', minimum: -10000, maximum: 10000 },
          destinationRoomId: { type: 'string', minLength: 1, maxLength: 120 }
        },
        required: ['playerId', 'kind', 'title', 'rationale'],
        additionalProperties: false
      },
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
      execute: (input) => execute('propose_dm_intervention', window.GaeCoDm.createProposal.bind(window.GaeCoDm), input)
    }
  ];

  try {
    for (const tool of tools) {
      if (registered.has(tool.name)) continue;
      await context.registerTool(tool);
      registered.add(tool.name);
      window.GaeCoDm.setWebMcpDiagnostics({ registeredTools: [...registered] });
    }
  } catch (error) {
    const message = String(error?.message || error || 'WebMCP registration failed.').slice(0, 500);
    window.GaeCoDm.setWebMcpDiagnostics({ registrationError: message, registeredTools: [...registered] });
    console.warn('WebMCP registration stumbled over the threshold; the ordinary dashboard remains unharmed.', error);
  }
})();
