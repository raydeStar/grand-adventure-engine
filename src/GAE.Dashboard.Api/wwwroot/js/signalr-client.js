// signalr-client.js — SignalR real-time connection
const GameHub = {
  connection: null,
  _handlers: {},
  realtimeEnabled: false,

  async connect() {
    if (this.connection) {
      try {
        await this.connection.stop();
      } catch {
        // Ignore stale connection shutdown failures.
      }
      this.connection = null;
    }

    if (!window.signalR) {
      this.connection = null;
      this.realtimeEnabled = false;
      this._emit('status', 'polling');
      return false;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/game')
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.connection.onreconnecting(() => this._emit('status', 'reconnecting'));
    this.connection.onreconnected(() => this._emit('status', 'connected'));
    this.connection.onclose(() => this._emit('status', 'disconnected'));

    // Server events
    this.connection.on('Connected', (msg) => {
      console.log('SignalR:', msg);
      this._emit('status', 'connected');
    });

    this.connection.on('GameEvent', (evt) => this._emit('gameEvent', evt));
    this.connection.on('PlayerEvent', (evt) => this._emit('playerEvent', evt));
    this.connection.on('RoomEvent', (evt) => this._emit('roomEvent', evt));
    this.connection.on('ActionResult', (result) => this._emit('actionResult', result));
    this.connection.on('AdminEvent', (evt) => this._emit('adminEvent', evt));

    try {
      await this.connection.start();
      this.realtimeEnabled = true;
      this._emit('status', 'connected');
      return true;
    } catch (err) {
      this.realtimeEnabled = false;
      console.error('SignalR connection failed:', err);
      const message = String(err?.message || err || '');
      this._emit('status', /401|403/.test(message) ? 'disconnected' : 'polling');
      return false;
    }
  },

  async disconnect() {
    if (this.connection) {
      try {
        await this.connection.stop();
      } catch {
        // Ignore disconnect failures.
      }
    }

    this.connection = null;
    this.realtimeEnabled = false;
    this._emit('status', 'disconnected');
  },

  async joinPlayerFeed(playerId) {
    return this._invokeIfConnected('JoinPlayerFeed', playerId);
  },

  async joinRoomFeed(roomId) {
    return this._invokeIfConnected('JoinRoomFeed', roomId);
  },

  async leaveRoomFeed(roomId) {
    return this._invokeIfConnected('LeaveRoomFeed', roomId);
  },

  async joinAdminFeed() {
    return this._invokeIfConnected('JoinAdminFeed');
  },

  async _invokeIfConnected(method, ...args) {
    if (!window.signalR || this.connection?.state !== signalR.HubConnectionState.Connected) {
      return false;
    }

    try {
      await this.connection.invoke(method, ...args);
      return true;
    } catch (err) {
      this.realtimeEnabled = false;
      this._emit('status', 'polling');
      console.warn(`SignalR ${method} failed; continuing with polling.`, err);
      return false;
    }
  },

  isRealtimeAvailable() {
    return this.realtimeEnabled;
  },

  on(event, handler) {
    if (!this._handlers[event]) this._handlers[event] = [];
    this._handlers[event].push(handler);
  },

  _emit(event, data) {
    (this._handlers[event] || []).forEach(fn => fn(data));
  }
};
