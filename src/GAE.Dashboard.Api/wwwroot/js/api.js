// api.js — REST API client
const API = {
  base: '/api/dashboard',

  async getLoginOptions() {
    const res = await fetch(`${this.base}/auth/options`, { credentials: 'same-origin' });
    if (!res.ok) throw new Error(await this.readError(res));
    const body = await res.json();
    return body.accounts || [];
  },

  async getSession() {
    const res = await fetch(`${this.base}/auth/session`, { credentials: 'same-origin' });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    const session = await res.json();
    return session || null;
  },

  async login(username, password, rememberMe = false) {
    return this.postJson(`${this.base}/auth/login`, { username, password, rememberMe });
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

  async getPlayers() {
    return this.getJson(`${this.base}/players`);
  },

  async getPlayer(id) {
    return this.getOptionalJson(`${this.base}/players/${encodeURIComponent(id)}`);
  },

  async getRooms() {
    return this.getJson(`${this.base}/rooms`);
  },

  async getRoom(id) {
    return this.getOptionalJson(`${this.base}/rooms/${encodeURIComponent(id)}`);
  },

  async getStory(playerId, limit = 50) {
    const params = new URLSearchParams({ limit: String(limit) });
    if (playerId) params.set('playerId', playerId);
    return this.getJson(`${this.base}/story?${params}`);
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

  async sendMessage(data) {
    return this.postJson(`${this.base}/admin/send-message`, data);
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

  async getHealth() {
    return this.getJson(`${this.base}/health`);
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
  async dmSearch(query, typeFilter, worldId) {
    const params = new URLSearchParams({ q: query });
    if (typeFilter) params.set('type', typeFilter);
    if (worldId) params.set('worldId', worldId);
    return this.getJson(`${this.base}/admin/dm/search?${params}`);
  },

  async dmBrowse(type, worldId) {
    const params = worldId ? `?worldId=${encodeURIComponent(worldId)}` : '';
    return this.getJson(`${this.base}/admin/dm/browse/${encodeURIComponent(type)}${params}`);
  },

  // ── Content Registry ───────────────────────────────────
  async getRegistry(type) {
    return this.getJson(`${this.base}/admin/registry/${encodeURIComponent(type)}`);
  },

  async getRegistryEntry(type, id) {
    return this.getOptionalJson(`${this.base}/admin/registry/${encodeURIComponent(type)}/${encodeURIComponent(id)}`);
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

  async activateWorld(worldId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/activate`, {});
  },

  async deactivateWorld(worldId) {
    return this.postJson(`${this.base}/admin/worlds/${encodeURIComponent(worldId)}/deactivate`, {});
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

  async getJson(url) {
    const res = await fetch(url, { credentials: 'same-origin' });
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async getOptionalJson(url) {
    const res = await fetch(url, { credentials: 'same-origin' });
    if (res.status === 404) return null;
    if (!res.ok) throw this.createHttpError(res, await this.readError(res));
    return res.json();
  },

  async postJson(url, data) {
    const res = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
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
