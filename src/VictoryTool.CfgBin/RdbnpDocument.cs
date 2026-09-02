using System.Buffers.Binary;
using System.Text;

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
    private RdbnpDocument(IReadOnlyList<RdbnpType> types, IReadOnlyList<RdbnpList> lists)
    {
        Types = types;
        Lists = lists;
    }

    public IReadOnlyList<RdbnpType> Types { get; }
    public IReadOnlyList<RdbnpList> Lists { get; }

    public static RdbnpDocument Read(ReadOnlySpan<byte> source)
    {
        global::System.Diagnostics.Trace.WriteLine($"rdbnp_read_started bytes={source.Length}");
        try
        {
            var document = ReadCore(source);
            global::System.Diagnostics.Trace.WriteLine(
                $"rdbnp_read_completed types={document.Types.Count} lists={document.Lists.Count}");
            return document;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException or DecoderFallbackException)
        {
            global::System.Diagnostics.Trace.WriteLine(
                $"rdbnp_read_failed error={exception.GetType().Name}");
            throw new InvalidDataException("The RDBNP structure contains an invalid range or encoded string.", exception);
        }
    }

    private static RdbnpDocument ReadCore(ReadOnlySpan<byte> source)
    {
        if (source.Length < 0x3C || !source.StartsWith("RDBNP"u8))
            throw new InvalidDataException("The data does not contain an RDBNP header.");

        var dataBase = checked(ReadInt16(source, 0x0A) * 4);
        var dataSize = ReadInt32(source, 0x0C);
        RequireRange(source, dataBase, dataSize, "data section");

        var typeOffset = RelativeWords(source, 0x24, dataBase);
        var typeCount = ReadCount(source, 0x26, "type count");
        var fieldOffset = RelativeWords(source, 0x28, dataBase);
        var fieldCount = ReadCount(source, 0x2A, "field count");
        var rootOffset = RelativeWords(source, 0x2C, dataBase);
        var rootCount = ReadCount(source, 0x2E, "root count");
        var hashOffset = RelativeWords(source, 0x30, dataBase);
        var stringOffsetsOffset = RelativeWords(source, 0x32, dataBase);
        var hashCount = ReadCount(source, 0x34, "string hash count");
        var valueOffset = RelativeWords(source, 0x36, dataBase);
        var stringOffset = checked(dataBase + ReadInt32(source, 0x38));

        RequireRange(source, typeOffset, checked(typeCount * 0x20), "type table");
        RequireRange(source, fieldOffset, checked(fieldCount * 0x20), "field table");
        RequireRange(source, rootOffset, checked(rootCount * 0x20), "root table");
        RequireRange(source, hashOffset, checked(hashCount * 4), "string hash table");
        RequireRange(source, stringOffsetsOffset, checked(hashCount * 4), "string offset table");
        RequireRange(source, stringOffset, 1, "string section");

        var strings = new Dictionary<uint, string>();
        for (var index = 0; index < hashCount; index++)
        {
            var hash = ReadUInt32(source, hashOffset + index * 4);
            var relativeStringOffset = ReadInt32(source, stringOffsetsOffset + index * 4);
            if (relativeStringOffset < 0)
                throw new InvalidDataException("An RDBNP string offset is negative.");
            strings[hash] = ReadNullTerminatedString(source, checked(stringOffset + relativeStringOffset));
        }

        var fields = new RawField[fieldCount];
        for (var index = 0; index < fieldCount; index++)
        {
            var offset = fieldOffset + index * 0x20;
            fields[index] = new RawField(
                ResolveName(strings, ReadUInt32(source, offset)),
                ReadInt16(source, offset + 4),
                ReadInt32(source, offset + 8),
                ReadInt32(source, offset + 12),
                ReadInt32(source, offset + 16));
            if (fields[index].ElementSize <= 0 || fields[index].Offset < 0 || fields[index].Count <= 0)
                throw new InvalidDataException("An RDBNP field descriptor contains invalid dimensions.");
        }

        var rawTypes = new RawType[typeCount];
        var publicTypes = new RdbnpType[typeCount];
        for (var index = 0; index < typeCount; index++)
        {
            var offset = typeOffset + index * 0x20;
            var fieldIndex = ReadCount(source, offset + 8, "type field index");
            var count = ReadCount(source, offset + 10, "type field count");
            if (fieldIndex > fields.Length - count)
                throw new InvalidDataException("An RDBNP type references fields outside the field table.");
            var selectedFields = fields.AsSpan(fieldIndex, count).ToArray();
            rawTypes[index] = new RawType(ResolveName(strings, ReadUInt32(source, offset)), selectedFields);
            publicTypes[index] = new RdbnpType(
                rawTypes[index].Name,
                selectedFields.Select(field => new RdbnpField(
                    field.Name, field.Type, field.ElementSize, field.Offset, field.Count)).ToArray());
        }

        var lists = new RdbnpList[rootCount];
        for (var index = 0; index < rootCount; index++)
        {
            var offset = rootOffset + index * 0x20;
            var typeIndex = ReadCount(source, offset, "root type index");
            if (typeIndex >= rawTypes.Length)
                throw new InvalidDataException("An RDBNP root references an unknown type.");
            var relativeValueOffset = ReadInt32(source, offset + 4);
            var rowSize = ReadInt32(source, offset + 8);
            var rowCount = ReadInt32(source, offset + 12);
            if (relativeValueOffset < 0 || rowSize <= 0 || rowCount < 0)
                throw new InvalidDataException("An RDBNP root contains invalid row dimensions.");
            var rowsOffset = checked(valueOffset + relativeValueOffset);
            RequireRange(source, rowsOffset, checked(rowSize * rowCount), "root values");
            var rows = new RdbnpRow[rowCount];
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var rowOffset = checked(rowsOffset + rowIndex * rowSize);
                var values = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
                foreach (var field in rawTypes[typeIndex].Fields)
                {
                    var fieldSize = checked(field.ElementSize * field.Count);
                    if (field.Offset > rowSize - fieldSize)
                        throw new InvalidDataException("An RDBNP field lies outside its row.");
                    var fieldValues = new object[field.Count];
                    for (var valueIndex = 0; valueIndex < field.Count; valueIndex++)
                    {
                        var itemOffset = checked(rowOffset + field.Offset + valueIndex * field.ElementSize);
                        fieldValues[valueIndex] = ReadValue(source, itemOffset, field.Type, field.ElementSize, stringOffset);
                    }
                    values.Add(field.Name, fieldValues);
                }
                rows[rowIndex] = new RdbnpRow(values);
            }
            lists[index] = new RdbnpList(
                ResolveName(strings, ReadUInt32(source, offset + 16)),
                publicTypes[typeIndex],
                rows);
        }

        return new RdbnpDocument(publicTypes, lists);
    }

    private static object ReadValue(ReadOnlySpan<byte> source, int offset, short type, int size, int stringOffset)
    {
        RequireRange(source, offset, size, "field value");
        return type switch
        {
            3 => source[offset] != 0,
            4 => source[offset],
            5 or 9 => ReadInt16(source, offset),
            6 or 10 => ReadInt32(source, offset),
            13 => BitConverter.Int32BitsToSingle(ReadInt32(source, offset)),
            15 => ReadUInt32(source, offset),
            20 => ReadConditionString(source, offset, stringOffset),
            21 => new RdbnpTuple(ReadInt16(source, offset), ReadInt16(source, offset + 2)),
            0 or 1 or 2 or 17 => source.Slice(offset, size).ToArray(),
            _ => throw new NotSupportedException($"RDBNP field type {type} is not supported by the read-only parser."),
        };
    }

    private static object ReadConditionString(ReadOnlySpan<byte> source, int offset, int stringOffset)
    {
        var value = ReadUInt32(source, offset);
        if (value > int.MaxValue)
            return value;
        var absolute = checked(stringOffset + (int)value);
        return absolute >= 0 && absolute < source.Length
            ? ReadNullTerminatedString(source, absolute)
            : value;
    }

    private static string ReadNullTerminatedString(ReadOnlySpan<byte> source, int offset)
    {
        RequireRange(source, offset, 1, "string");
        var remainder = source[offset..];
        var length = remainder.IndexOf((byte)0);
        if (length < 0)
            throw new InvalidDataException("An RDBNP string is not null-terminated.");
        return Encoding.UTF8.GetString(remainder[..length]);
    }

    private static string ResolveName(IReadOnlyDictionary<uint, string> strings, uint hash) =>
        strings.TryGetValue(hash, out var name)
            ? name
            : throw new InvalidDataException($"RDBNP name hash 0x{hash:X8} has no string entry.");

    private static int RelativeWords(ReadOnlySpan<byte> source, int offset, int dataBase) =>
        checked(dataBase + ReadInt16(source, offset) * 4);

    private static int ReadCount(ReadOnlySpan<byte> source, int offset, string label)
    {
        var value = ReadInt16(source, offset);
        if (value < 0)
            throw new InvalidDataException($"The RDBNP {label} is negative.");
        return value;
    }

    private static short ReadInt16(ReadOnlySpan<byte> source, int offset)
    {
        RequireRange(source, offset, 2, "Int16");
        return BinaryPrimitives.ReadInt16LittleEndian(source[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, int offset)
    {
        RequireRange(source, offset, 4, "Int32");
        return BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
    {
        RequireRange(source, offset, 4, "UInt32");
        return BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
    }

    private static void RequireRange(ReadOnlySpan<byte> source, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > source.Length - length)
            throw new InvalidDataException($"The RDBNP {label} lies outside the file bounds.");
    }

    private sealed record RawField(string Name, short Type, int ElementSize, int Offset, int Count);
    private sealed record RawType(string Name, IReadOnlyList<RawField> Fields);
}
