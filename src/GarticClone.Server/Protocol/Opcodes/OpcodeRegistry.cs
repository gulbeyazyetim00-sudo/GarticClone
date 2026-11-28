using System.Reflection;

namespace GarticClone.Server.Protocol.Opcodes;

public sealed record OpcodeRange(string Name, byte Min, byte Max);


public static class OpcodeRegistry
{
    private static readonly Type[] Domains =
    [
        typeof(SystemOp),
        typeof(SessionOp),
	typeof(DrawOp),
    ];

    private static readonly Lazy<IReadOnlyDictionary<byte, string>> Names = new(Build);

   
    public static void Validate() => _ = Names.Value;

    
    public static string NameOf(byte opcode) =>
        Names.Value.TryGetValue(opcode, out var name) ? name : $"unknown(0x{opcode:x2})";

    private static IReadOnlyDictionary<byte, string> Build()
    {
        var names = new Dictionary<byte, string>();
        foreach (var domain in Domains)
        {
            var range = domain.GetField("Range", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as OpcodeRange
                ?? throw new InvalidOperationException($"{domain.Name} has no public static Range");

            var consts = domain.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte));

            foreach (var field in consts)
            {
                byte value = (byte)field.GetRawConstantValue()!;
                string label = $"{range.Name}.{field.Name}";
                if (value < range.Min || value > range.Max)
                    throw new InvalidOperationException(
                        $"Opcode {label}=0x{value:x2} is outside range 0x{range.Min:x2}-0x{range.Max:x2}");
                if (names.TryGetValue(value, out var existing))
                    throw new InvalidOperationException($"Opcode collision: {label} and {existing} both use 0x{value:x2}");
                names[value] = label;
            }
        }
        return names;
    }
}
