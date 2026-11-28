// Drawing payloads (opcodes 0x10-0x1F). All integers big-endian.
// These ride inside the 16-byte envelope from shared-js/protocol.js.
//
//  C->S  STROKE_START  0x10: strokeId u16 | color u8 | thickness u8 | x u16 | y u16        (8 B)
//  C->S  STROKE_DELTAS 0x11: strokeId u16 | originX u16 | originY u16 | n u8 | n*(dx i8, dy i8)
//  C->S  STROKE_END    0x12: strokeId u16                                                  (2 B)
//  C->S  CANVAS_CLEAR  0x1F: (empty)
//  S->C  same opcodes, payload = journalSeq u32 | <the client payload above>
//  S->C  DRAW_ACK      0x1D: journalSeq u32   (sent to the drawer after STROKE_END / CANVAS_CLEAR)
//
// Journal entries (SYNC_SNAPSHOT.missedDeltas[].payloadBase64) decode as:
//  opcode u8 | flags u8 | <client payload above>

export const DrawOp = Object.freeze({
  STROKE_START: 0x10,
  STROKE_DELTAS: 0x11,
  STROKE_END: 0x12,
  DRAW_ACK: 0x1d,
  CANVAS_CLEAR: 0x1f,
});

export const isDrawOp = (op) => op >= 0x10 && op <= 0x1f;
export const MAX_POINTS_PER_BATCH = 255;
export const PALETTE = ['#111111', '#ffffff', '#e53935', '#fb8c00', '#fdd835', '#43a047', '#1e88e5', '#8e24aa'];

export function encodeStrokeStart({ strokeId, color, thickness, x, y }) {
  const b = new Uint8Array(8);
  const v = new DataView(b.buffer);
  v.setUint16(0, strokeId);
  v.setUint8(2, color);
  v.setUint8(3, thickness);
  v.setUint16(4, x);
  v.setUint16(6, y);
  return b;
}

export function encodeStrokeDeltas({ strokeId, originX, originY, steps }) {
  if (steps.length > MAX_POINTS_PER_BATCH) throw new RangeError('too many points in one batch');
  const b = new Uint8Array(7 + 2 * steps.length);
  const v = new DataView(b.buffer);
  v.setUint16(0, strokeId);
  v.setUint16(2, originX);
  v.setUint16(4, originY);
  v.setUint8(6, steps.length);
  steps.forEach(([dx, dy], i) => {
    v.setInt8(7 + 2 * i, dx);
    v.setInt8(8 + 2 * i, dy);
  });
  return b;
}

export function encodeStrokeEnd(strokeId) {
  const b = new Uint8Array(2);
  new DataView(b.buffer).setUint16(0, strokeId);
  return b;
}

/** Decode a client-format draw payload (no seq prefix). */
export function decodeDrawPayload(opcode, p) {
  const v = new DataView(p.buffer, p.byteOffset, p.byteLength);
  switch (opcode) {
    case DrawOp.STROKE_START:
      if (p.byteLength !== 8) throw new RangeError('bad STROKE_START length');
      return { type: 'start', strokeId: v.getUint16(0), color: v.getUint8(2), thickness: v.getUint8(3), x: v.getUint16(4), y: v.getUint16(6) };
    case DrawOp.STROKE_DELTAS: {
      if (p.byteLength < 7) throw new RangeError('bad STROKE_DELTAS length');
      const n = v.getUint8(6);
      if (p.byteLength !== 7 + 2 * n) throw new RangeError('STROKE_DELTAS count mismatch');
      const steps = [];
      for (let i = 0; i < n; i++) steps.push([v.getInt8(7 + 2 * i), v.getInt8(8 + 2 * i)]);
      return { type: 'deltas', strokeId: v.getUint16(0), originX: v.getUint16(2), originY: v.getUint16(4), steps };
    }
    case DrawOp.STROKE_END:
      if (p.byteLength !== 2) throw new RangeError('bad STROKE_END length');
      return { type: 'end', strokeId: v.getUint16(0) };
    case DrawOp.CANVAS_CLEAR:
      return { type: 'clear' };
    default:
      throw new RangeError(`not a draw opcode: 0x${opcode.toString(16)}`);
  }
}

/** Decode a server->client draw message: payload = seq u32 | client payload (ACK: seq only). */
export function decodeBroadcast(opcode, payload) {
  if (payload.byteLength < 4) throw new RangeError('draw message too short');
  const seq = new DataView(payload.buffer, payload.byteOffset, payload.byteLength).getUint32(0);
  if (opcode === DrawOp.DRAW_ACK) return { type: 'ack', seq };
  return { seq, ...decodeDrawPayload(opcode, payload.subarray(4)) };
}

/** Decode one journal entry from a SYNC_SNAPSHOT (opcode u8 | flags u8 | payload). */
export function decodeJournalEntry(seq, bytes) {
  if (bytes.byteLength < 2) throw new RangeError('journal entry too short');
  if (bytes[1] & 0x01) throw new Error('compressed journal entries not supported yet');
  return { seq, ...decodeDrawPayload(bytes[0], bytes.subarray(2)) };
}

export const base64ToBytes = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
