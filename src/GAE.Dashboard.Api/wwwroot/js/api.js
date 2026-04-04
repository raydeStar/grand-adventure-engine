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

  async applyStatus(data) {
    return this.postJson(`${this.base}/admin/mutations/status`, data);
  },

  async upsertRoomFixture(data) {
    return this.postJson(`${this.base}/admin/mutations/room-fixture`, data);
  },

  async deletePlayer(playerId) {
    const res = await fetch(`${this.base}/admin/players/${encodeURIComponent(playerId)}`, {
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
