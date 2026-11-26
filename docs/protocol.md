# Wire Protocol Specification — v1

Owner: Member 1 (Transport & Core Protocol)

## 1. Transport

- RFC 6455 WebSocket, implemented from scratch on a raw `TcpListener`
  (`src/GarticClone.Server/Transport/`). No ASP.NET, no `System.Net.WebSockets` on the server.
- Endpoint: `ws://<host>:<port>/ws`
- **Binary frames only.** A TEXT message closes the connection with `1003 Unsupported Data`.
- No extensions are negotiated (`permessage-deflate` is intentionally off so that the
  team's own compression can be measured in isolation). Any RSV bit → `1002`.
- Max message size: 1 MiB (`MAX_MESSAGE_SIZE` env var), enforced from the frame header,
  before the payload is received → `1009`.
- `TCP_NODELAY` is enabled on every socket (no Nagle batching delay).

### 1.1 Handshake validation

| Condition | Response |
|---|---|
| Path ≠ `/ws` | `404` |
| Method ≠ `GET` | `405` |
| Missing `Upgrade: websocket` / `Connection: Upgrade` | `400` |
| `Sec-WebSocket-Version` ≠ `13` | `426` + `Sec-WebSocket-Version: 13` |
| `Sec-WebSocket-Key` not 16 bytes base64 | `400` |
| `Origin` not in `ALLOWED_ORIGINS` (if set) | `403` |
| OK | `101`, `Sec-WebSocket-Accept = base64(SHA1(key + GUID))` |

### 1.2 Frame-level rules enforced

| Violation | Close code |
|---|---|
| Client frame not masked | 1002 |
| RSV1-3 set | 1002 |
| Unknown opcode (0x3-0x7, 0xB-0xF) | 1002 |
| Control frame fragmented or payload > 125 | 1002 |
| CONTINUATION without start / new data frame during fragmentation | 1002 |
| 64-bit length with MSB set | 1002 |
| Close payload of length 1 or invalid close code | 1002 |
| Invalid UTF-8 in TEXT message or close reason | 1007 |
| Message larger than limit | 1009 |

Control frames interleaved inside a fragmented message are handled.
The closing handshake follows §7: the peer's close code is echoed, the server
closes TCP, and a 5 s timer destroys the socket if the peer never answers.

## 2. Application message format

Every WebSocket binary message carries exactly one application message:

```
 0               1               2               3
 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7
+---------------+---------------+---------------+---------------+
|    version    |    opcode     |     flags     |   reserved=0  |
+---------------+---------------+---------------+---------------+
|                         seq (u32)                             |
+---------------------------------------------------------------+
|                      timestamp (u32, ms)                      |
+---------------------------------------------------------------+
|                    payloadLength (u32)                        |
+---------------------------------------------------------------+
|                        payload ...                            |
```

All integers big-endian. Header = 16 bytes. Codec: `Protocol/MessageCodec.cs` (server) and
`shared-js/protocol.js` (browser). Keep both in sync.

| Field | Meaning |
|---|---|
| version | `1`. Other values → `ERROR DECODE_FAILED` |
| opcode | Application opcode (§3) |
| flags | bit0 `COMPRESSED`, bit1 `BATCHED`, bit2 `ACK_REQUIRED`, rest 0 |
| reserved | Must be 0 |
| seq | Per-sender counter starting at 0, +1 per message, wraps at 2^32. Receiver counts gaps (`session.stats.seqGaps`). Used by the state-sync layer on reconnect. |
| timestamp | Sender's monotonic ms clock (server: ms since process start). Only meaningful relative to the same sender. |
| payloadLength | Must equal `messageLength − 16` |

## 3. Opcode ranges

Each domain owns a range; ranges are checked at startup, collisions throw.

| Range | Domain | Owner | File |
|---|---|---|---|
| `0x00–0x0F` | system | Member 1 | `Protocol/Opcodes/SystemOp.cs` |
| `0x10–0xFF` | reserved for other domains | other members | `Protocol/Opcodes/<Domain>Op.cs` |

### 3.1 System opcodes

| Op | Name | Dir | Payload |
|---|---|---|---|
| 0x01 | HELLO | C→S | JSON `{ clientName?: string }` (≤32 chars kept) |
| 0x02 | WELCOME | S→C | JSON `{ clientId, protocolVersion, serverTime, heartbeatIntervalMs }` |
| 0x03 | PING_APP | C→S | opaque bytes (e.g. nonce) |
| 0x04 | PONG_APP | S→C | same bytes as PING_APP |
| 0x05 | ERROR | S→C | JSON `{ code, message, refSeq? }` |
| 0x06 | TELEMETRY_REQUEST | C→S | empty |
| 0x07 | TELEMETRY_REPORT | S→C | JSON bandwidth/RTT snapshot (§5) |

`ERROR.code` values: `DECODE_FAILED`, `UNKNOWN_OPCODE`, `HANDLER_FAILED`.
Errors don't close the connection.

JSON is used only for small, infrequent system messages; high-frequency data
(drawing) should define its own binary payload.

## 4. Heartbeat & latency

- **Server → client:** RFC 6455 `PING` every `HEARTBEAT_INTERVAL_MS` (5 s) with a
  4-byte nonce. Browsers answer automatically with `PONG`. RTT = time to the
  matching pong. After `HEARTBEAT_MAX_MISSED` (3) unanswered pings the TCP
  connection is dropped (detection ≈ 15 s).
- **Client → server:** the browser API cannot send WS pings, so clients use
  `PING_APP`/`PONG_APP` to measure their own RTT.
- RTT smoothing follows RFC 6298: `SRTT` (α = 1/8) and `RTTVAR` / jitter (β = 1/4).

## 5. Telemetry

Per client, `BandwidthMonitor` records:

- **wire level**: bytes and frames in/out including WebSocket framing, plus
  bytes/s over a 5 s sliding window (250 ms buckets)
- **per WS opcode** (BINARY / PING / PONG / CLOSE …)
- **per application opcode**: bytes and message count in/out
- **RTT**: last, SRTT, jitter, min, max, sample count
- **sendBufferBytes**: bytes queued but not yet sent (backpressure indicator)

Wire minus application bytes = framing overhead.
Opcode names in telemetry use the C# constant names, e.g. `system.PingApp`.

Access: `TELEMETRY_REQUEST` (own client) or `GET /telemetry` (all clients, JSON).
