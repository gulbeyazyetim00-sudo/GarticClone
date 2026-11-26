namespace GarticClone.Server.Protocol.Opcodes;


public static class SystemOp
{
    public static readonly OpcodeRange Range = new("system", 0x00, 0x0F);

    public const byte Hello = 0x01;             
    public const byte Welcome = 0x02;           
    public const byte PingApp = 0x03;           
    public const byte PongApp = 0x04;           
    public const byte Error = 0x05;             
    public const byte TelemetryRequest = 0x06;  
    public const byte TelemetryReport = 0x07;   
}
