export const PROTOCOL_VERSION = 1;
export const HEADER_SIZE = 16;

export const Flags = Object.freeze({
  COMPRESSED: 0x01,
  BATCHED: 0x02,
  ACK_REQUIRED: 0x04,
});

export const SystemOp = Object.freeze({
  HELLO: 0x01,
  WELCOME: 0x02,
  PING_APP: 0x03,
  PONG_APP: 0x04,
  ERROR: 0x05,
  TELEMETRY_REQUEST: 0x06,
  TELEMETRY_REPORT: 0x07,
});

export class ProtocolDecodeError extends Error {}

export function encodeMessage({ opcode, flags = 0, seq = 0, timestamp = 0, payload = new Uint8Array(0) }) {
  const out = new Uint8Array(HEADER_SIZE + payload.byteLength);
  const view = new DataView(out.buffer);
  view.setUint8(0, PROTOCOL_VERSION);
  view.setUint8(1, opcode);
  view.setUint8(2, flags);
  view.setUint8(3, 0);
  view.setUint32(4, seq >>> 0);
  view.setUint32(8, timestamp >>> 0);
  view.setUint32(12, payload.byteLength);
  out.set(payload, HEADER_SIZE);
  return out;
}

export function decodeMessage(bytes) {
  if (bytes.byteLength < HEADER_SIZE) throw new ProtocolDecodeError('message too short');
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint8(0) !== PROTOCOL_VERSION) throw new ProtocolDecodeError('unsupported version');
  if (view.getUint8(3) !== 0) throw new ProtocolDecodeError('reserved byte must be 0');
  const payloadLength = view.getUint32(12);
  if (payloadLength !== bytes.byteLength - HEADER_SIZE) throw new ProtocolDecodeError('payload length mismatch');
  return {
    version: 1,
    opcode: view.getUint8(1),
    flags: view.getUint8(2),
    seq: view.getUint32(4),
    timestamp: view.getUint32(8),
    payload: bytes.subarray(HEADER_SIZE),
  };
}

const enc = new TextEncoder();
const dec = new TextDecoder();
export const encodeJson = (obj) => enc.encode(JSON.stringify(obj));
export const decodeJson = (payload) => JSON.parse(dec.decode(payload));
