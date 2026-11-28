### 3.2 Draw opcodes (`0x10–0x1F`, owner: client canvas / session)

Payloads are big-endian and ride inside the §2 envelope. Coordinates are canvas pixels (u16).

| Op | Name | Dir | Payload |
|---|---|---|---|
| 0x10 | STROKE_START | C→S | `strokeId u16, color u8, thickness u8, x u16, y u16` (8 B) |
| 0x11 | STROKE_DELTAS | C→S | `strokeId u16, originX u16, originY u16, n u8, n × (dx i8, dy i8)` |
| 0x12 | STROKE_END | C→S | `strokeId u16` |
| 0x1D | DRAW_ACK | S→C | `journalSeq u32` — sent to the drawer after STROKE_END / CANVAS_CLEAR |
| 0x1F | CANVAS_CLEAR | C→S | empty |

- Each STROKE_DELTAS batch is self-contained: `origin` is the last point of the previous batch, and
  every step is within [-128, 127]. Larger jumps are split into several steps by the encoder.
- The client batches on a 30 Hz tick (≤ 255 points per message).
- **Relay (S→C):** same opcode, payload = `journalSeq u32` + the client payload. The sender is excluded.
  `journalSeq` is the room's delta-journal counter (not the envelope `seq`) and is what `LastAckSeq` refers to.
- **Journal:** each entry stores `opcode u8, flags u8, payload`. `SYNC_SNAPSHOT.missedDeltas[].payloadBase64`
  decodes to this format, with `seq` given alongside.
- CANVAS_CLEAR empties the journal, then appends itself as the first entry.
