namespace GarticClone.Server.Transport;

public static class WsConstants
{
    public const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public const int MaxControlPayload = 125;
}

public static class WsOpcode
{
    public const byte Continuation = 0x0;
    public const byte Text = 0x1;
    public const byte Binary = 0x2;
    public const byte Close = 0x8;
    public const byte Ping = 0x9;
    public const byte Pong = 0xA;

    public static bool IsKnown(int op) =>
        op is Continuation or Text or Binary or Close or Ping or Pong;

    public static bool IsControl(int op) => (op & 0x8) != 0;

    public static string Name(int op) => op switch
    {
        Continuation => "CONTINUATION",
        Text => "TEXT",
        Binary => "BINARY",
        Close => "CLOSE",
        Ping => "PING",
        Pong => "PONG",
        _ => $"0x{op:X}",
    };
}

public static class CloseCode
{
    public const ushort Normal = 1000;
    public const ushort GoingAway = 1001;
    public const ushort ProtocolError = 1002;
    public const ushort UnsupportedData = 1003;
    public const ushort NoStatus = 1005;   
    public const ushort Abnormal = 1006;   
    public const ushort InvalidPayload = 1007;
    public const ushort PolicyViolation = 1008;
    public const ushort MessageTooBig = 1009;
    public const ushort InternalError = 1011;

    public static bool IsValidOnWire(int code) =>
        code is (>= 1000 and <= 1003) or (>= 1007 and <= 1011) or (>= 3000 and <= 4999);
}
public sealed class WsProtocolException(ushort closeCode, string message) : Exception(message)
{
    public ushort CloseCode { get; } = closeCode;
}
