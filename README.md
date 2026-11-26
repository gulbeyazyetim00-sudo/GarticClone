# Gartic Phone Clone — Member 1: Transport & Core Protocol (C# / .NET 10)

RFC 6455 WebSocket server written from scratch on a raw `TcpListener`, a binary
application protocol, a message dispatcher, heartbeat and per-client bandwidth
telemetry. **No NuGet packages, no ASP.NET, no `System.Net.WebSockets` on the server.**

## Run in Visual Studio 2022

1. Open `GarticClone.sln`.
2. **Server:** set `GarticClone.Server` as the startup project and press **F5**.
   It prints `listening on ws://0.0.0.0:8080/ws`.
   Check `http://localhost:8080/health` in a browser.
3. **Tests:** right-click `GarticClone.Server.Tests` → *Set as Startup Project* → **F5**
   (or **Ctrl+F5** to keep the window open). All tests print ✓ / ✗.

Command line:

```bash
dotnet run --project src/GarticClone.Server                  # server
dotnet run --project tests/GarticClone.Server.Tests          # all tests
dotnet run --project tests/GarticClone.Server.Tests -- Frame # tests whose name contains "Frame"
```

Endpoints: `ws://localhost:8080/ws` · `GET /health` · `GET /telemetry`

## Folder structure

```
GarticClone.sln
src/GarticClone.Server/
  Program.cs                   entry point (Ctrl+C = graceful shutdown)
  ServerApp.cs                 wires transport + protocol + telemetry together
  ServerConfig.cs              all tunables (env-overridable)
  Transport/                   RFC 6455
    HttpRequest.cs             reads the HTTP upgrade request from the raw socket
    Handshake.cs               upgrade validation, Sec-WebSocket-Accept
    FrameParser.cs             streaming frame parser + unmasking
    FrameEncoder.cs            frame / close payload encoder
    WebSocketConnection.cs     fragmentation, ping/pong, close handshake, send queue
    WebSocketServer.cs         TcpListener accept loop, /health & /telemetry
    WsConstants.cs             opcodes, close codes, exceptions
  Protocol/
    MessageCodec.cs            16-byte header encode/decode, JSON helper
    Dispatcher.cs              header validation + opcode routing, IModule
    ClientSession.cs           per-client seq numbers, Send(), stats
    SessionRegistry.cs         lookup + broadcast
    Opcodes/SystemOp.cs        system opcodes (0x00-0x0F)
    Opcodes/OpcodeRegistry.cs  range + collision check at startup
  Telemetry/
    BandwidthMonitor.cs        wire/app bytes, per opcode, bytes/s
    Heartbeat.cs               WS ping/pong keep-alive, dead-client detection
    RttEstimator.cs            SRTT + jitter (RFC 6298)
    SlidingWindowRate.cs       bucketed bytes/s window
  Modules/
    ModuleList.cs              modules given to the Dispatcher
    Core/SystemModule.cs       HELLO, PING_APP, TELEMETRY handlers
  Utils/                       logger, monotonic clock

tests/GarticClone.Server.Tests/   self-contained test runner (no xUnit needed)
  Transport/ Protocol/ Telemetry/ Integration/ Helpers/ Framework/

shared-js/protocol.js          browser-side codec for the frontend team
docs/protocol.md               formal protocol specification
```

## Integration rules (for merging with the team)

Teammates **add files instead of editing Member 1's files**:

1. **Opcodes:** create `Protocol/Opcodes/<Domain>Op.cs` with a `public static readonly OpcodeRange Range`
   (agree on ranges inside `0x10–0xFF`) and `public const byte` opcodes, then add
   `typeof(<Domain>Op)` to `OpcodeRegistry.Domains`. Out-of-range or duplicate opcodes stop the server at startup.
2. **Handlers:** create `Modules/<Domain>/<Domain>Module.cs` implementing `IModule`
   and add one line to `Modules/ModuleList.cs`. Registering the same opcode twice throws.
3. A handler receives `HandlerContext`: `ctx.Session`, `ctx.Message` (Opcode, Flags, Seq, Timestamp, Payload),
   `ctx.Reply(...)`, `ctx.Services.Sessions.Broadcast(...)`, `ctx.Services.Bandwidth`.
4. Per-client state: `session.Data["<domain>.<key>"]`.
5. Frontend: use `shared-js/protocol.js` to encode/decode messages.

Those two lists are the only shared lines, so a merge conflict there is at most one line.
