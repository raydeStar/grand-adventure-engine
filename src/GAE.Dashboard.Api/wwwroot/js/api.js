// api.js — REST API client
const API = {
  base: '/api/dashboard',

  async getLoginOptions() {
    const res = await fetch(`${this.base}/auth/options`, { credentials: 'same-origin' });
    if (!res.ok) throw new Error(await this.readError(res));
    const body = await res.json();
    return {
      accounts: body.accounts || [],
      registrationOpen: body.registrationOpen === true
    };
  },

  async getSession() {
    const res = await fetch(`${this.base}/auth/session`, { credentials: 'same-origin', cache: 'no-store' });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    const session = await res.json();
    return session || null;
  },

  async login(username, password, rememberMe = false) {
    return this.postJson(`${this.base}/auth/login`, { username, password, rememberMe });
  },

  async register(username, password, rememberMe = false) {
    return this.postJson(`${this.base}/auth/register`, { username, password, rememberMe });
  },

  async logout() {
    const res = await fetch(`${this.base}/auth/logout`, {
      method: 'POST',
      credentials: 'same-origin'
    });

    if (res.status === 401) return { success: true };
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async getPlayers(options = {}) {
    return this.getJson(`${this.base}/players`, options);
  },

  async getPlayer(id, options = {}) {
    return this.getOptionalJson(`${this.base}/players/${encodeURIComponent(id)}`, options);
  },

  async getRooms(options = {}) {
    return this.getJson(`${this.base}/rooms`, options);
  },

  async getRoom(id, playerId, options = {}) {
    const params = new URLSearchParams();
    if (playerId) params.set('playerId', playerId);
    const suffix = params.size ? `?${params}` : '';
    return this.getOptionalJson(`${this.base}/rooms/${encodeURIComponent(id)}${suffix}`, options);
  },

  async getStory(playerId, limit = 50, worldId, options = {}) {
    const params = new URLSearchParams({ limit: String(limit) });
    if (playerId) params.set('playerId', playerId);
    if (worldId) params.set('worldId', worldId);
    return this.getJson(`${this.base}/story?${params}`, options);
  },

  async getRoomStory(roomId, limit = 10) {
    return this.getJson(`${this.base}/story/room/${encodeURIComponent(roomId)}?limit=${limit}`);
  },

  async sendCommand(playerId, command) {
    return this.postJson(`${this.base}/action`, { playerId, command });
  },

  async createCharacter(data) {
    return this.postJson(`${this.base}/characters`, data);
  },

  async getCreationOptions() {
    return this.getJson(`${this.base}/creation-options`);
  },

  async getGameConfig() {
    return this.getJson(`${this.base}/config`);
  },

  async getAdminSummary() {
    return this.getJson(`${this.base}/admin/summary`);
  },

  async seedDemoCharacters(replaceExisting = false) {
    return this.postJson(`${this.base}/admin/seed-demo`, { replaceExisting });
  },

  async editPlayer(data) {
    return this.postJson(`${this.base}/admin/mutations/edit-player`, data);
  },

  async adjustResources(data) {
    return this.postJson(`${this.base}/admin/mutations/resources`, data);
  },

  async teleportPlayer(data) {
    return this.postJson(`${this.base}/admin/mutations/teleport`, data);
  },

  async grantItem(data) {
    return this.postJson(`${this.base}/admin/mutations/grant-item`, data);
  },

  async itemAction(data) {
    return this.postJson(`${this.base}/admin/mutations/item-action`, data);
  },

  async applyStatus(data) {
    return this.postJson(`${this.base}/admin/mutations/status`, data);
  },

  async upsertRoomFixture(data) {
    return this.postJson(`${this.base}/admin/mutations/room-fixture`, data);
  },

  async sendMessage(data, options = {}) {
    return this.postJson(`${this.base}/admin/send-message`, data, options);
  },

  async getCoDmActions(options = {}) {
    return this.getJson(`${this.base}/admin/co-dm/actions`, options);
  },

  async sendCoDmPlayerFlowMessage(data, options = {}) {
    return this.postJson(`${this.base}/admin/co-dm/messages`, data, { ...options, coDm: true });
  },

  async createCoDmProposal(data, options = {}) {
    return this.postJson(`${this.base}/admin/co-dm/proposals`, data, { ...options, coDm: true });
  },

  async approveCoDmAction(actionId, approvalToken, options = {}) {
    return this.postJson(`${this.base}/admin/co-dm/actions/${encodeURIComponent(actionId)}/approve`, { approvalToken }, { ...options, coDm: true });
  },

  async rejectCoDmAction(actionId, approvalToken, options = {}) {
    return this.postJson(`${this.base}/admin/co-dm/actions/${encodeURIComponent(actionId)}/reject`, { approvalToken }, { ...options, coDm: true });
  },

  async resetWorld(keepPlayers = true) {
    return this.postJson(`${this.base}/admin/reset-world`, { keepPlayers });
  },

  async deletePlayer(playerId) {
    const res = await fetch(`${this.base}/admin/players/${encodeURIComponent(playerId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async deleteRoom(roomId) {
    const res = await fetch(`${this.base}/admin/rooms/${encodeURIComponent(roomId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async updateRoom(roomId, data) {
    return this.putJson(`${this.base}/admin/rooms/${encodeURIComponent(roomId)}`, data);
  },

  async createRoom(data) {
    return this.postJson(`${this.base}/admin/rooms`, data);
  },

  async updatePlayer(playerId, data) {
    return this.putJson(`${this.base}/admin/players/${encodeURIComponent(playerId)}`, data);
  },

  async getHealth(options = {}) {
    return this.getJson(`${this.base}/health`, options);
  },

  async getLlmModels() {
    return this.getJson(`${this.base}/admin/llm/models`);
  },

  async setLlmModel(model) {
    return this.postJson(`${this.base}/admin/llm/model`, { model });
  },

  async getConversationLogs(operation, playerId, limit = 50, offset = 0) {
    const params = new URLSearchParams({ limit: String(limit), offset: String(offset) });
    if (operation) params.set('operation', operation);
    if (playerId) params.set('playerId', playerId);
    return this.getJson(`${this.base}/admin/conversations?${params}`);
  },

  async getConversationStats() {
    return this.getJson(`${this.base}/admin/conversations/stats`);
  },

  // ── DM Console ─────────────────────────────────────────
  async dmSearch(query, typeFilter, worldId, options = {}) {
    const params = new URLSearchParams({ q: query });
    if (typeFilter) params.set('type', typeFilter);
    if (worldId) params.set('worldId', worldId);
    return this.getJson(`${this.base}/admin/dm/search?${params}`, options);
  },

  async dmBrowse(type, worldId) {
    const params = worldId ? `?worldId=${encodeURIComponent(worldId)}` : '';
    return this.getJson(`${this.base}/admin/dm/browse/${encodeURIComponent(type)}${params}`);
  },

  // ── Content Registry ───────────────────────────────────
  async getRegistry(type) {
    return this.getJson(`${this.base}/admin/registry/${encodeURIComponent(type)}`);
  },

  async getRegistryEntry(type, id, options = {}) {
    return this.getOptionalJson(`${this.base}/admin/registry/${encodeURIComponent(type)}/${encodeURIComponent(id)}`, options);
  },

  async getRegistrySummary() {
    return this.getJson(`${this.base}/admin/registry/summary`);
  },

  async upsertRegistryEntry(type, data) {
    return this.postJson(`${this.base}/admin/registry/${encodeURIComponent(type)}`, data);
  },

  async deleteRegistryEntry(type, id) {
    const res = await fetch(`${this.base}/admin/registry/${encodeURIComponent(type)}/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async generateContent(contentType, description, existingJson) {
    return this.postJson(`${this.base}/admin/registry/generate`, { contentType, description, existingJson });
  },

  async generateQuest(brief, worldId, loreEntryIds, minLevel, maxLevel) {
    return this.postJson(`${this.base}/admin/registry/generate-quest`, { brief, worldId, loreEntryIds, minLevel, maxLevel });
  },

  // ── World Management ───────────────────────────────────
  async getWorlds() {
    return this.getJson(`${this.base}/admin/worlds`);
  },

  async getWorld(worldId) {
    return this.getOptionalJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}`);
  },

  async createWorld(data) {
    return this.postJson(`${this.base}/admin/worlds`, data);
  },

  async updateWorld(worldId, data) {
    return this.putJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}`, data);
  },

  async deleteWorld(worldId) {
    const res = await fetch(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async generateWorldIntro(worldId, narratorPresetId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/generate-intro`, { narratorPresetId: narratorPresetId || null });
  },

  async activateWorld(worldId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/activate`, {});
  },

  async deactivateWorld(worldId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/deactivate`, {});
  },

  async setDiscordDefaultWorld(worldId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/set-discord-default`, {});
  },

  async getWorldPlayers(worldId) {
    return this.getJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/players`);
  },

  async getWorldPortals(worldId) {
    return this.getJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/portals`);
  },

  async createPortal(worldId, data) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/portals`, data);
  },

  async updatePortal(worldId, portalId, data) {
    return this.putJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/portals/${encodeURIComponent(portalId)}`, data);
  },

  async deletePortal(worldId, portalId) {
    const res = await fetch(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/portals/${encodeURIComponent(portalId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async transferPlayerToWorld(playerId, destinationWorldId) {
    return this.postJson(`${this.base}/admin/worlds/transfer`, { playerId, destinationWorldId });
  },

  exportWorldYaml(worldId) {
    const a = document.createElement('a');
    a.href = `${this.base}/admin/worlds/${encodeURIComponent(worldId)}/export`;
    a.download = `world-${worldId}.yaml`;
    document.body.appendChild(a);
    a.click();
    a.remove();
  },

  async importWorldYaml(file) {
    const form = new FormData();
    form.append('file', file);
    const res = await fetch(`${this.base}/admin/worlds/import`, {
      method: 'POST',
      credentials: 'same-origin',
      body: form
    });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async getJson(url, options = {}) {
    const res = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: options.signal });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async getOptionalJson(url, options = {}) {
    const res = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: options.signal });
    if (res.status === 404) return null;
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async postJson(url, data, options = {}) {
    const res = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        ...(options.coDm ? { 'X-GAE-Request': 'co-dm' } : {})
      },
      signal: options.signal,
      body: JSON.stringify(data)
    });

    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async putJson(url, data) {
    const res = await fetch(url, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    });

    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  createHttpError(res, message) {
    const error = new Error(message || `Request failed (${res.status})`);
    error.status = res.status;
    error.code = res.status === 401 ? 'unauthorized' : res.status === 403 ? 'forbidden' : 'http_error';
    return error;
  },

  async readError(res) {
    try {
      const data = await res.json();
      return data.error || data.title || `Request failed (${res.status})`;
    } catch {
      return `Request failed (${res.status})`;
    }
  }
};
