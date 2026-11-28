// Mirrors GarticClone.Server/Protocol/Opcodes/SessionOp.cs (0x20-0x2F).
// Drawing opcodes (0x10-0x1F) do not exist on the server yet.
export const SessionOp = Object.freeze({
  ROOM_CREATE: 0x20,   // C->S
  ROOM_JOIN: 0x21,     // C->S
  ROOM_LEAVE: 0x22,    // C->S
  ROOM_STATE: 0x23,    // S->C
  GAME_START: 0x24,    // C->S (host only)
  PHASE_SUBMIT: 0x25,  // C->S
  PHASE_CHANGED: 0x26, // S->C
  SYNC_REQUEST: 0x27,  // C->S
  SYNC_SNAPSHOT: 0x28, // S->C
  PLAYER_STATUS: 0x29, // S->C
});

const names = {};
for (const [k, v] of Object.entries(SessionOp)) names[v] = `session.${k}`;
export const sessionOpName = (op) => names[op];
