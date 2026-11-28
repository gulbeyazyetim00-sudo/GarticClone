import {
  encodeMessage, decodeMessage, encodeJson, decodeJson, SystemOp,
} from '@shared/protocol.js';
import { SessionOp } from './sessionOps.js';

const nowMs = () => performance.now();

/**
 * Thin client for the raw-WebSocket protocol in docs/protocol.md.
 * Events (via on/off): open, welcome, message, error, telemetry, rtt, close.
 */
export class GameSocket {
  constructor({
    url = 'ws://localhost:8080/ws',
    clientName = 'web-client',
    pingIntervalMs = 2000,
    autoReconnect = true,
  } = {}) {
    this.url = url;
    this.clientName = clientName;
    this.pingIntervalMs = pingIntervalMs;
    this.autoReconnect = autoReconnect;

    this.ws = null;
    this.seq = 0;            // per-connection send counter, starts at 0
    this.clientId = null;
    this.closedByUser = false;
    this.retry = 0;
    this.listeners = new Map();
    this.pingTimer = null;
    this.stats = {
      bytesIn: 0, bytesOut: 0, msgsIn: 0, msgsOut: 0,
      srtt: null, rttvar: null, lastRtt: null,
    };
  }

  on(evt, fn) {
    if (!this.listeners.has(evt)) this.listeners.set(evt, new Set());
    this.listeners.get(evt).add(fn);
    return () => this.off(evt, fn);
  }
  off(evt, fn) { this.listeners.get(evt)?.delete(fn); }
  emit(evt, data) { this.listeners.get(evt)?.forEach((fn) => fn(data)); }

  get isOpen() { return this.ws?.readyState === WebSocket.OPEN; }

  connect() {
    this.closedByUser = false;
    const ws = new WebSocket(this.url);
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    this.seq = 0;

    ws.onopen = () => {
      this.retry = 0;
      this.emit('open');
      this.sendJson(SystemOp.HELLO, { clientName: this.clientName });
    };
    ws.onmessage = (e) => this.#onData(e.data);
    ws.onerror = () => {};
    ws.onclose = (e) => {
      clearInterval(this.pingTimer);
      this.emit('close', { code: e.code, reason: e.reason });
      if (this.autoReconnect && !this.closedByUser) {
        const delay = Math.min(500 * 2 ** this.retry++, 5000);
        setTimeout(() => this.connect(), delay);
      }
    };
  }

  close() {
    this.closedByUser = true;
    clearInterval(this.pingTimer);
    this.ws?.close(1000);
  }

  send(opcode, payload = new Uint8Array(0), flags = 0) {
    if (!this.isOpen) return false;
    const msg = encodeMessage({
      opcode, flags, payload,
      seq: this.seq++ >>> 0,
      timestamp: Math.round(nowMs()) >>> 0,
    });
    this.ws.send(msg);
    this.stats.bytesOut += msg.byteLength;
    this.stats.msgsOut++;
    return true;
  }

  sendJson(opcode, obj, flags = 0) { return this.send(opcode, encodeJson(obj), flags); }

  // --- session helpers (JSON payloads are camelCase on the server) ---
  createRoom(playerName, maxPlayers = 8, roundDurationSec = 60) {
    return this.sendJson(SessionOp.ROOM_CREATE, { playerName, maxPlayers, roundDurationSec });
  }
  joinRoom(roomId, playerName) {
    return this.sendJson(SessionOp.ROOM_JOIN, { roomId, playerName });
  }
  requestSync(roomId, playerId, lastAckSeq) {
    return this.sendJson(SessionOp.SYNC_REQUEST, { roomId, playerId, lastAckSeq });
  }
  requestTelemetry() { return this.send(SystemOp.TELEMETRY_REQUEST); }

  #onData(data) {
    const bytes = new Uint8Array(data);
    this.stats.bytesIn += bytes.byteLength;
    this.stats.msgsIn++;
    let msg;
    try { msg = decodeMessage(bytes); } catch (err) { this.emit('error', { code: 'LOCAL_DECODE', message: err.message }); return; }

    switch (msg.opcode) {
      case SystemOp.WELCOME: {
        const w = decodeJson(msg.payload);
        this.clientId = w.clientId;
        this.#startPing();
        this.emit('welcome', w);
        break;
      }
      case SystemOp.PONG_APP:
        this.#onPong(msg.payload);
        break;
      case SystemOp.ERROR:
        this.emit('error', decodeJson(msg.payload));
        break;
      case SystemOp.TELEMETRY_REPORT:
        this.emit('telemetry', decodeJson(msg.payload));
        break;
      default:
        break;
    }
    this.emit('message', msg); // everything, including the system ones above
  }

  #startPing() {
    clearInterval(this.pingTimer);
    this.pingTimer = setInterval(() => {
      const p = new Uint8Array(8);
      new DataView(p.buffer).setFloat64(0, nowMs());
      this.send(SystemOp.PING_APP, p);
    }, this.pingIntervalMs);
  }

  #onPong(payload) {
    if (payload.byteLength !== 8) return;
    const sent = new DataView(payload.buffer, payload.byteOffset, 8).getFloat64(0);
    const rtt = nowMs() - sent;
    const s = this.stats;
    s.lastRtt = rtt;
    if (s.srtt == null) { s.srtt = rtt; s.rttvar = rtt / 2; }   // RFC 6298
    else {
      s.rttvar = 0.75 * s.rttvar + 0.25 * Math.abs(s.srtt - rtt);
      s.srtt = 0.875 * s.srtt + 0.125 * rtt;
    }
    this.emit('rtt', { rtt, srtt: s.srtt, rttvar: s.rttvar });
  }
}
