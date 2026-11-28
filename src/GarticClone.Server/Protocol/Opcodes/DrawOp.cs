namespace GarticClone.Server.Protocol.Opcodes;

public static class DrawOp
{
    public static readonly OpcodeRange Range = new("draw", 0x10, 0x1F);

    public const byte StrokeStart = 0x10;   // C->S: strokeId u16 | color u8 | thickness u8 | x u16 | y u16
    public const byte StrokeDeltas = 0x11;  // C->S: strokeId u16 | originX u16 | originY u16 | n u8 | n*(dx i8, dy i8)
    public const byte StrokeEnd = 0x12;     // C->S: strokeId u16
    public const byte DrawAck = 0x1D;       // S->C: journalSeq u32 (to the drawer, after StrokeEnd / CanvasClear)
    public const byte CanvasClear = 0x1F;   // C->S: empty
    // 0x1E reserved for a client-uploaded keyframe (PNG/WebP + basedOnSeq), not implemented yet.
}
