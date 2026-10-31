using CFGBINManager;
using System.Buffers.Binary;

namespace VictoryTool.CfgBin;

public sealed record RdbnpField(string Name, short Type, int ElementSize, int Offset, int Count);

public sealed record RdbnpType(string Name, IReadOnlyList<RdbnpField> Fields);

public sealed record RdbnpTuple(short Offset, short Count);

public sealed class RdbnpRow
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<object>> _values;

    internal RdbnpRow(IReadOnlyDictionary<string, IReadOnlyList<object>> values) => _values = values;

    public uint GetUInt32(string fieldName) => Get<uint>(fieldName);
    public byte GetByte(string fieldName) => Get<byte>(fieldName);
    public int GetInt32(string fieldName) => Get<int>(fieldName);
    public short GetInt16(string fieldName) => Get<short>(fieldName);
    public bool GetBoolean(string fieldName) => Get<bool>(fieldName);
    public string GetString(string fieldName) => Get<string>(fieldName);
    public RdbnpTuple GetTuple(string fieldName) => Get<RdbnpTuple>(fieldName);
    public IReadOnlyList<object> GetValues(string fieldName) =>
        _values.TryGetValue(fieldName, out var values)
            ? values
            : throw new KeyNotFoundException($"RDBNP field '{fieldName}' does not exist in this row.");

    private T Get<T>(string fieldName)
    {
        var values = GetValues(fieldName);
        if (values.Count != 1 || values[0] is not T value)
            throw new InvalidOperationException($"RDBNP field '{fieldName}' is not a scalar {typeof(T).Name}.");
        return value;
    }
}

public sealed record RdbnpList(string Name, RdbnpType Type, IReadOnlyList<RdbnpRow> Rows);

public sealed class RdbnpDocument
{
    internal RdbnpDocument(CfgDocument native)
    {
        if (native.Format != BinaryFormat.Rdbnp) throw new InvalidDataException("Expected an RDBNP document.");
        Types = native.Types.Select(type => new RdbnpType(type.Name,
            type.Fields.Select(field => new RdbnpField(field.Name, checked((short)field.TypeCode),
                field.ElementSize, field.Offset, field.Count)).ToArray())).ToArray();
        Lists = native.Lists.Select(list =>
        {
            var schema = native.Types.SingleOrDefault(type => type.Name == list.TypeName);
            var type = Types.SingleOrDefault(type => type.Name == list.TypeName) ?? new RdbnpType(list.TypeName, []);
            var rows = list.Rows.Select(row =>
            {
                var values = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
                for (int i = 0; i < row.Values.Count; i++)
                {
                    var field = schema?.Fields.Single(field => field.Name == row.FieldNames[i]);
                    var value = row.Values[i];
                    values.Add(row.FieldNames[i], value.Kind == ValueKind.Array
                        ? value.Items.Select(item => ToObject(item, field?.TypeCode, native.IsBigEndian)).ToArray()
                        : [ToObject(value, field?.TypeCode, native.IsBigEndian)]);
                }
                return new RdbnpRow(values);
            }).ToArray();
            return new RdbnpList(list.Name, type, rows);
        }).ToArray();
    }

    public IReadOnlyList<RdbnpType> Types { get; }
    public IReadOnlyList<RdbnpList> Lists { get; }

    public static RdbnpDocument Read(ReadOnlySpan<byte> source) => new(CfgBinSchema.Read(source));

    private static object ToObject(CfgValue value, ushort? type, bool bigEndian) => value.Kind switch
    {
        ValueKind.Boolean => value.AsBoolean(),
        ValueKind.Unsigned when type == 4 => checked((byte)value.Bits),
        ValueKind.Unsigned when type == 5 => unchecked((short)value.Bits),
        ValueKind.Unsigned when type == 6 => unchecked((int)value.Bits),
        ValueKind.Unsigned => value.Bits,
        ValueKind.Signed when type == 8 => checked((sbyte)value.AsInt64()),
        ValueKind.Signed when type == 9 => checked((short)value.AsInt64()),
        ValueKind.Signed when type == 10 => checked((int)value.AsInt64()),
        ValueKind.Signed => value.AsInt64(),
        ValueKind.Hash32 => checked((uint)value.Bits),
        ValueKind.Hash64 => value.Bits,
        ValueKind.Float32 => value.AsSingle(),
        ValueKind.Float64 => value.AsDouble(),
        ValueKind.String => value.Text is { } text ? (object)text : uint.MaxValue,
        ValueKind.Reference16 => new RdbnpTuple(unchecked((short)value.AsRange().Start), unchecked((short)value.AsRange().Count)),
        ValueKind.Vector => VectorBytes(value, bigEndian),
        _ => value
    };

    private static byte[] VectorBytes(CfgValue value, bool bigEndian)
    {
        var bytes = new byte[checked(value.Items.Count * 4)];
        for (int i = 0; i < value.Items.Count; i++)
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), checked((uint)value.Items[i].Bits));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), checked((uint)value.Items[i].Bits));
        return bytes;
    }
}
