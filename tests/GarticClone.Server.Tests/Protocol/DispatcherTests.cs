using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Tests.Helpers;

namespace GarticClone.Server.Tests.Protocol;

public class DispatcherTests
{
    private static ServerServices Services() => new() { Sessions = new SessionRegistry(), Bandwidth = new BandwidthMonitor() };

    private sealed class TestModule(string name, byte opcode, MessageHandler handler) : IModule
    {
        public string Name => name;
        public IReadOnlyDictionary<byte, MessageHandler> Handlers { get; } = new Dictionary<byte, MessageHandler> { [opcode] = handler };
    }

    [Test("routes to registered handler with decoded message")]
    public async Task Routes()
    {
        await using var pair = await LoopbackConnection.CreateAsync();
        var d = new Dispatcher(Services());
        AppMessage? seen = null;
        d.Register(SystemOp.PingApp, ctx => seen = ctx.Message);

        d.Dispatch(new ClientSession(pair.Server), MessageCodec.Encode(SystemOp.PingApp, [1], seq: 7));
        Assert.Equal(7u, Assert.NotNull(seen).Seq);
    }

    [Test("duplicate registration throws (catches merge mistakes)")]
    public void Duplicate()
    {
        var d = new Dispatcher(Services());
        d.RegisterModule(new TestModule("a", SystemOp.Hello, _ => { }));
        var ex = Assert.Throws<InvalidOperationException>(() => d.RegisterModule(new TestModule("b", SystemOp.Hello, _ => { })));
        Assert.True(ex.Message.Contains("already registered by module \"a\""));
    }

    [Test("unknown opcode, bad header and throwing handler all produce ERROR replies")]
    public async Task Errors()
    {
        await using var pair = await LoopbackConnection.CreateAsync();
        var d = new Dispatcher(Services());
        d.Register(SystemOp.Hello, _ => throw new Exception("boom"));
        var session = new ClientSession(pair.Server);

        d.Dispatch(session, MessageCodec.Encode(0xEE));
        d.Dispatch(session, [1, 2, 3]);
        d.Dispatch(session, MessageCodec.Encode(SystemOp.Hello));

        var codes = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var m = MessageCodec.Decode(await pair.ReceiveAsync());
            Assert.Equal(SystemOp.Error, m.Opcode);
            codes.Add(JsonPayload.Decode<ErrorPayload>(m.Payload.Span)!.Code);
        }
        Assert.SequenceEqual(new[] { ErrorCodes.UnknownOpcode, ErrorCodes.DecodeFailed, ErrorCodes.HandlerFailed }, codes);
        Assert.Equal(1, session.Stats.UnknownOpcodes);
        Assert.Equal(1, session.Stats.DecodeErrors);
    }

    [Test("outbound seq increments and inbound gaps are counted")]
    public async Task Sequencing()
    {
        await using var pair = await LoopbackConnection.CreateAsync();
        var d = new Dispatcher(Services());
        d.Register(SystemOp.PingApp, ctx => ctx.Reply(SystemOp.PongApp));
        var session = new ClientSession(pair.Server);

        foreach (uint seq in new uint[] { 0, 1, 5 })
            d.Dispatch(session, MessageCodec.Encode(SystemOp.PingApp, seq: seq));

        var seqs = new List<uint>();
        for (int i = 0; i < 3; i++) seqs.Add(MessageCodec.Decode(await pair.ReceiveAsync()).Seq);
        Assert.SequenceEqual(new uint[] { 0, 1, 2 }, seqs);
        Assert.Equal(1, session.Stats.SeqGaps);
    }
}
