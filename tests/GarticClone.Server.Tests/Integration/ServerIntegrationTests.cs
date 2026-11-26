using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GarticClone.Server.Modules.Core;
using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Tests.Helpers;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Integration;


public class ServerIntegrationTests
{
    private static ServerApp StartServer() => ServerApp.Start(new ServerConfig
    {
        Host = IPAddress.Loopback,
        Port = 0,
        HeartbeatInterval = TimeSpan.FromMinutes(1),
    });

    private static async Task<ClientWebSocket> ConnectAsync(ServerApp app)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{app.Port}/ws"), CancellationToken.None);
        return ws;
    }

    private static Task SendAsync(ClientWebSocket ws, byte opcode, ReadOnlySpan<byte> payload = default, uint seq = 0) =>
        ws.SendAsync(MessageCodec.Encode(opcode, payload, seq: seq), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static async Task<AppMessage> NextAsync(ClientWebSocket ws, byte opcode)
    {
        while (true)
        {
            var m = MessageCodec.Decode(await LoopbackConnection.ReceiveMessageAsync(ws));
            if (m.Opcode == opcode) return m;
        }
    }

    [Test("HELLO -> WELCOME, PING_APP echo, TELEMETRY_REPORT")]
    public async Task SystemFlow()
    {
        var app = StartServer();
        try
        {
            using var ws = await ConnectAsync(app);

            await SendAsync(ws, SystemOp.Hello, JsonPayload.Encode(new HelloPayload("tester")), seq: 0);
            var welcome = JsonPayload.Decode<WelcomePayload>((await NextAsync(ws, SystemOp.Welcome)).Payload.Span)!;
            Assert.True(welcome.ClientId.StartsWith('c'));
            Assert.Equal(1, welcome.ProtocolVersion);

            await SendAsync(ws, SystemOp.PingApp, [1, 2, 3, 4], seq: 1);
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4 }, (await NextAsync(ws, SystemOp.PongApp)).Payload.ToArray());

            await SendAsync(ws, SystemOp.TelemetryRequest, seq: 2);
            var report = JsonPayload.Decode<TelemetrySnapshot>((await NextAsync(ws, SystemOp.TelemetryReport)).Payload.Span)!;
            Assert.Equal(welcome.ClientId, report.ClientId);
            Assert.True(report.Wire.In.Bytes > 0);
            Assert.Equal(1L, report.ByAppOpcode["system.PingApp"].InCount);
            Assert.True(report.Rtt.Samples >= 1, "client auto-answered the server PING");

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
        }
        finally { await app.StopAsync(); }
    }

    [Test("large message (> 64 KiB, 64-bit length) round-trips through PING_APP")]
    public async Task LargeMessage()
    {
        var app = StartServer();
        try
        {
            using var ws = await ConnectAsync(app);
            var payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
            await SendAsync(ws, SystemOp.PingApp, payload);
            var back = await NextAsync(ws, SystemOp.PongApp);
            Assert.SequenceEqual(payload, back.Payload.ToArray());
        }
        finally { await app.StopAsync(); }
    }

    [Test("raw: accept key, fragmented message reassembly, ping in the middle")]
    public async Task RawFragmentation()
    {
        var app = StartServer();
        try
        {
            using var c = await RawClient.ConnectAsync(app.Port);
            Assert.Equal(101, c.Status);
            Assert.True(c.Head.Contains($"Sec-WebSocket-Accept: {c.ExpectedAccept}"));

            var msg = MessageCodec.Encode(SystemOp.PingApp, [42]);
            c.SendFrame(WsOpcode.Binary, msg.AsSpan(0, 5), fin: false);
            c.SendFrame(WsOpcode.Ping, "mid"u8);
            c.SendFrame(WsOpcode.Continuation, msg.AsSpan(5, 5), fin: false);
            c.SendFrame(WsOpcode.Continuation, msg.AsSpan(10), fin: true);

            await c.WaitForAsync(f => f.Opcode == WsOpcode.Pong && Encoding.ASCII.GetString(f.Payload) == "mid");
            var data = await c.WaitForAsync(f => f.Opcode == WsOpcode.Binary);
            var m = MessageCodec.Decode(data.Payload);
            Assert.Equal(SystemOp.PongApp, m.Opcode);
            Assert.Equal((byte)42, m.Payload.Span[0]);
        }
        finally { await app.StopAsync(); }
    }

    [Test("raw: unmasked frame -> close 1002")]
    public async Task RawUnmasked()
    {
        var app = StartServer();
        try
        {
            using var c = await RawClient.ConnectAsync(app.Port);
            c.SendFrame(WsOpcode.Binary, "x"u8, masked: false);
            var close = await c.WaitForAsync(f => f.Opcode == WsOpcode.Close);
            Assert.Equal(CloseCode.ProtocolError, BinaryPrimitives.ReadUInt16BigEndian(close.Payload));
            await c.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await app.StopAsync(); }
    }

    [Test("raw: text frame -> close 1003 (binary only)")]
    public async Task RawText()
    {
        var app = StartServer();
        try
        {
            using var c = await RawClient.ConnectAsync(app.Port);
            c.SendFrame(WsOpcode.Text, "hello"u8);
            var close = await c.WaitForAsync(f => f.Opcode == WsOpcode.Close);
            Assert.Equal(CloseCode.UnsupportedData, BinaryPrimitives.ReadUInt16BigEndian(close.Payload));
        }
        finally { await app.StopAsync(); }
    }

    [Test("raw: client close handshake is echoed and TCP closed")]
    public async Task RawCloseEcho()
    {
        var app = StartServer();
        try
        {
            using var c = await RawClient.ConnectAsync(app.Port);
            c.SendFrame(WsOpcode.Close, FrameEncoder.ClosePayload(1000));
            var close = await c.WaitForAsync(f => f.Opcode == WsOpcode.Close);
            Assert.Equal((ushort)1000, BinaryPrimitives.ReadUInt16BigEndian(close.Payload));
            await c.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await app.StopAsync(); }
    }

    [Test("raw: wrong path -> 404")]
    public async Task RawWrongPath()
    {
        var app = StartServer();
        try
        {
            using var c = await RawClient.ConnectAsync(app.Port, "/nope");
            Assert.Equal(404, c.Status);
        }
        finally { await app.StopAsync(); }
    }

    [Test("/health and /telemetry HTTP endpoints return JSON")]
    public async Task HttpEndpoints()
    {
        var app = StartServer();
        try
        {
            using var http = new HttpClient();
            var health = await http.GetStringAsync($"http://127.0.0.1:{app.Port}/health");
            Assert.True(JsonDocument.Parse(health).RootElement.GetProperty("ok").GetBoolean());

            var telemetry = await http.GetStringAsync($"http://127.0.0.1:{app.Port}/telemetry");
            Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(telemetry).RootElement.ValueKind);
        }
        finally { await app.StopAsync(); }
    }
}
