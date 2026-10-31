using CFGBINManager;

namespace VictoryTool.CfgBin;

public enum CfgBinFormat
{
    T2b,
    Rdbnp,
}

public enum CfgBinValueWidth
{
    Unknown,
    Int32 = 4,
    Int64 = 8,
}

public enum CfgBinValueType : byte
{
    String = 0,
    Integer = 1,
    FloatingPoint = 2,
    Unknown = 3,
}

public sealed record CfgBinValue(
    CfgBinValueType Type,
    object? Value,
    long RawValue,
    int FileOffset);

public sealed record CfgBinValueEdit(
    int EntryIndex,
    int ValueIndex,
    object Value);

public sealed record CfgBinEntryAppend(
    int TemplateEntryIndex,
    IReadOnlyList<object?> Values);

public sealed record CfgBinEntryInsert(
    int BeforeEntryIndex,
    int TemplateEntryIndex,
    IReadOnlyList<object?> Values);

public sealed record CfgBinEntry(
    int Index,
    uint NameCrc,
    string? Name,
    IReadOnlyList<string> NameCandidates,
    IReadOnlyList<CfgBinValue> Values);

public sealed class CfgBinDocument
{
    private readonly CfgDocument document;

    private CfgBinDocument(CfgDocument document)
    {
        this.document = document;
        var info = document.T2bInfo ?? throw new InvalidDataException("Expected T2B source metadata.");
        StringDataOffset = info.StringOffset;
        StringDataLength = info.StringLength;
        StringDataCount = info.StringCount;
        EncodingCode = checked((short)info.EncodingCode);
        Entries = document.Rows.Select((row, index) =>
        {
            var source = row.T2bInfo ?? throw new InvalidDataException("Expected T2B row provenance.");
            return new CfgBinEntry(index, row.NameHash,
                source.NameCandidates.Count == 1 ? row.Name : null,
                source.NameCandidates,
                row.Values.Select((value, i) => new CfgBinValue(
                    ValueType(value), ToObject(value), unchecked((int)source.RawValues[i]),
                    checked(source.ValueOffset + i * 4))).ToArray());
        }).ToArray();
    }

    public CfgBinFormat Format => CfgBinFormat.T2b;
    public int EntryCount => Entries.Count;
    public int StringDataOffset { get; }
    public int StringDataLength { get; }
    public int StringDataCount { get; }
    public short EncodingCode { get; }
    public CfgBinValueWidth ValueWidth => EntryCount == 0 ? CfgBinValueWidth.Unknown : CfgBinValueWidth.Int32;
    public IReadOnlyList<CfgBinEntry> Entries { get; }

    public static CfgBinDocument Read(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("RDBNP"u8)) throw new NotSupportedException("Use RdbnpDocument or CfgBinSchema.Read for RDBNP.");
        return new(CfgBinSchema.Read(data));
    }

    public byte[] WriteUnmodified() => BinCodec.Write(document);
    public byte[] WriteCanonical() => BinCodec.Write(document);
    public string ExportCfg() => CfgTextCodec.Write(document);

    public byte[] WriteWithValueEdits(IEnumerable<CfgBinValueEdit> edits) => WriteEdits(edits, false);
    public byte[] WriteWithStringEdits(IEnumerable<CfgBinValueEdit> edits) => WriteEdits(edits, true);

    public byte[] WriteWithFixedStringEdits(IEnumerable<CfgBinValueEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var operations = edits.Select(edit =>
        {
            ArgumentNullException.ThrowIfNull(edit);
            if (edit.Value is not string text) throw new ArgumentException("Fixed string edits require string replacements.", nameof(edits));
            return new CfgStringEdit(edit.EntryIndex, edit.ValueIndex, text);
        }).ToArray();
        return BinCodec.WriteWithFixedStringEdits(document, operations);
    }

    public byte[] WriteWithAppendedEntries(IEnumerable<CfgBinEntryAppend> appends)
    {
        ArgumentNullException.ThrowIfNull(appends);
        return WriteWithInsertedEntries(appends.Select(append =>
        {
            ArgumentNullException.ThrowIfNull(append);
            return new CfgBinEntryInsert(EntryCount, append.TemplateEntryIndex, append.Values);
        }));
    }

    public byte[] WriteWithInsertedEntries(IEnumerable<CfgBinEntryInsert> inserts)
    {
        ArgumentNullException.ThrowIfNull(inserts);
        var operations = inserts.ToArray();
        var updated = Copy();
        int inserted = 0;
        foreach (var operation in operations.OrderBy(operation => operation?.BeforeEntryIndex))
        {
            ArgumentNullException.ThrowIfNull(operation);
            if ((uint)operation.BeforeEntryIndex > EntryCount || (uint)operation.TemplateEntryIndex >= EntryCount)
                throw new ArgumentOutOfRangeException(nameof(inserts), "Insertion or template index is outside the document.");
            ArgumentNullException.ThrowIfNull(operation.Values);
            var row = document.Rows[operation.TemplateEntryIndex].Clone();
            if (operation.Values.Count != row.Values.Count) throw new ArgumentException("Inserted values must match their template's field count.", nameof(inserts));
            for (int i = 0; i < row.Values.Count; i++) row.SetValue(i, FromObject(row.Values[i], operation.Values[i]));
            updated.InsertRow(operation.BeforeEntryIndex + inserted, row);
            inserted++;
        }
        return BinCodec.Write(updated);
    }

    private byte[] WriteEdits(IEnumerable<CfgBinValueEdit> edits, bool allowStrings)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var updated = Copy();
        var locations = new HashSet<(int, int)>();
        foreach (var edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if ((uint)edit.EntryIndex >= updated.Rows.Count || (uint)edit.ValueIndex >= updated.Rows[edit.EntryIndex].Values.Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "Edit location is outside the document.");
            if (!locations.Add((edit.EntryIndex, edit.ValueIndex))) throw new ArgumentException("A value can be edited only once per operation.", nameof(edits));
            var row = updated.Rows[edit.EntryIndex];
            var value = row.Values[edit.ValueIndex];
            if (allowStrings && (value.Kind != ValueKind.String || edit.Value is not string))
                throw new ArgumentException("String edits require existing string fields and non-null string replacements.", nameof(edits));
            if (value.Kind == ValueKind.String && !allowStrings) throw new NotSupportedException("Use string edits to rebuild the T2B pool.");
            if (value.Kind == ValueKind.Opaque) throw new NotSupportedException("Opaque T2B words cannot be edited through the compatibility API.");
            row.SetValue(edit.ValueIndex, FromObject(value, edit.Value));
        }
        return BinCodec.Write(updated);
    }

    private CfgDocument Copy() => BinCodec.Read(BinCodec.Write(document), document.Catalog);

    private static CfgBinValueType ValueType(CfgValue value) => value.Kind switch
    {
        ValueKind.String => CfgBinValueType.String,
        ValueKind.Signed => CfgBinValueType.Integer,
        ValueKind.Float32 => CfgBinValueType.FloatingPoint,
        ValueKind.Opaque => CfgBinValueType.Unknown,
        _ => throw new InvalidDataException($"Unexpected T2B value kind {value.Kind}.")
    };

    private static object? ToObject(CfgValue value) => value.Kind switch
    {
        ValueKind.String => value.Text,
        ValueKind.Signed => checked((int)value.AsInt64()),
        ValueKind.Float32 => value.AsSingle(),
        ValueKind.Opaque => checked((uint)value.Bits),
        _ => throw new InvalidDataException($"Unexpected T2B value kind {value.Kind}.")
    };

    private static CfgValue FromObject(CfgValue template, object? value) => (template.Kind, value) switch
    {
        (ValueKind.String, string text) when !text.Contains('\0') => CfgValue.String(text),
        (ValueKind.String, null) => CfgValue.String(null),
        (ValueKind.Signed, int number) => CfgValue.Signed(number),
        (ValueKind.Float32, float number) => CfgValue.Float(number),
        (ValueKind.Opaque, uint bits) when bits == template.Bits => template,
        _ => throw new ArgumentException($"Replacement does not match the native {template.Kind} value or contains NUL.", nameof(value))
    };
}
