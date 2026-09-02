using System.Buffers.Binary;
using System.Text;

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
    private const uint T2bFooterMagic = 0x62327401;
    private readonly byte[] _source;
    private readonly int _entryDataEnd;
    private readonly IReadOnlyList<(int Start, int End)> _entryRanges;

    private CfgBinDocument(
        byte[] source,
        int entryCount,
        int stringDataOffset,
        int stringDataLength,
        int stringDataCount,
        short encodingCode,
        CfgBinValueWidth valueWidth,
        IReadOnlyList<CfgBinEntry> entries,
        int entryDataEnd,
        IReadOnlyList<(int Start, int End)> entryRanges)
    {
        _source = source;
        Format = CfgBinFormat.T2b;
        EntryCount = entryCount;
        StringDataOffset = stringDataOffset;
        StringDataLength = stringDataLength;
        StringDataCount = stringDataCount;
        EncodingCode = encodingCode;
        ValueWidth = valueWidth;
        Entries = entries;
        _entryDataEnd = entryDataEnd;
        _entryRanges = entryRanges;
    }

    public CfgBinFormat Format { get; }
    public int EntryCount { get; }
    public int StringDataOffset { get; }
    public int StringDataLength { get; }
    public int StringDataCount { get; }
    public short EncodingCode { get; }
    public CfgBinValueWidth ValueWidth { get; }
    public IReadOnlyList<CfgBinEntry> Entries { get; }

    public static CfgBinDocument Read(ReadOnlySpan<byte> data)
    {
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_read_started bytes={data.Length}");
        try
        {
            var document = ReadCore(data);
            global::System.Diagnostics.Trace.WriteLine(
                $"cfgbin_read_completed format={document.Format} entries={document.EntryCount}");
            return document;
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentOutOfRangeException or DecoderFallbackException)
        {
            global::System.Diagnostics.Trace.WriteLine(
                $"cfgbin_read_failed error={exception.GetType().Name}");
            throw new InvalidDataException("The T2B structure contains invalid arithmetic or encoded text.", exception);
        }
    }

    private static CfgBinDocument ReadCore(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("RDBNP"u8))
            throw new NotSupportedException(
                "RDBNP was detected, but its structural parser is not enabled without a verified sample corpus.");
        if (data.Length < 0x30)
            throw new InvalidDataException("The data is too short to contain a valid T2B envelope.");

        var bytes = data.ToArray();
        var footerOffset = bytes.Length - 0x10;
        if (ReadUInt32(bytes, footerOffset, "footer magic") != T2bFooterMagic)
            throw new InvalidDataException("The data does not contain a recognized T2B or RDBNP signature.");

        var entryCount = ReadBoundedInt32(bytes, 0x00, "entry count");
        var stringDataOffset = ReadBoundedInt32(bytes, 0x04, "string data offset");
        var stringDataLength = ReadBoundedInt32(bytes, 0x08, "string data length");
        var stringDataCount = ReadBoundedInt32(bytes, 0x0C, "string data count");
        if (stringDataOffset < 0x10
            || stringDataOffset > footerOffset
            || stringDataLength > footerOffset - stringDataOffset)
        {
            throw new InvalidDataException("The T2B value-string section is outside the file bounds.");
        }
        var actualStringDataCount = bytes.AsSpan(stringDataOffset, stringDataLength).Count((byte)0);
        if (stringDataCount != actualStringDataCount)
        {
            throw new InvalidDataException(
                $"The T2B value-string count is {stringDataCount}, but the string block contains {actualStringDataCount} terminators.");
        }

        var encodingCode = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(footerOffset + 6, 2));
        var encoding = GetEncoding(encodingCode);
        var nameLookup = ParseNameTable(
            bytes,
            Align16(checked(stringDataOffset + stringDataLength)),
            footerOffset,
            encoding);

        var maximumEntryCount = (stringDataOffset - 0x10) / 8;
        if (entryCount > maximumEntryCount)
            throw new InvalidDataException("The T2B entry count cannot fit before the value-string section.");

        if (entryCount == 0)
        {
            return new CfgBinDocument(
                bytes, entryCount, stringDataOffset, stringDataLength, stringDataCount, encodingCode,
                CfgBinValueWidth.Unknown, [], 0x10, []);
        }

        var candidates = new List<(CfgBinValueWidth Width, IReadOnlyList<RawEntry> Entries, int EntryDataEnd)>();
        foreach (var width in new[] { CfgBinValueWidth.Int32, CfgBinValueWidth.Int64 })
        {
            if (TryReadEntries(
                    bytes, entryCount, stringDataOffset, stringDataLength, width,
                    out var rawEntries, out var entryDataEnd))
                candidates.Add((width, rawEntries, entryDataEnd));
        }
        if (candidates.Count != 1)
            throw new InvalidDataException("The T2B value width is invalid or ambiguous.");

        var selected = candidates[0];
        var entries = new CfgBinEntry[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            var rawEntry = selected.Entries[index];
            var names = nameLookup.TryGetValue(rawEntry.NameCrc, out var resolvedNames)
                ? resolvedNames
                : Array.Empty<string>();
            var values = new CfgBinValue[rawEntry.Values.Count];
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                var rawValue = rawEntry.Values[valueIndex];
                values[valueIndex] = new CfgBinValue(
                    rawEntry.Types[valueIndex],
                    DecodeValue(
                        rawEntry.Types[valueIndex],
                        rawValue.Bits,
                        selected.Width,
                        bytes,
                        stringDataOffset,
                        stringDataLength,
                        encoding),
                    rawValue.Bits,
                    rawValue.FileOffset);
            }
            entries[index] = new CfgBinEntry(
                index,
                rawEntry.NameCrc,
                names.Count == 1 ? names[0] : null,
                names,
                Array.AsReadOnly(values));
        }

        return new CfgBinDocument(
            bytes, entryCount, stringDataOffset, stringDataLength, stringDataCount, encodingCode,
            selected.Width,
            Array.AsReadOnly(entries),
            selected.EntryDataEnd,
            selected.Entries.Select(entry => (entry.StartOffset, entry.EndOffset)).ToArray());
    }

    public byte[] WriteUnmodified()
    {
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_unmodified bytes={_source.Length}");
        return (byte[])_source.Clone();
    }

    public byte[] WriteCanonical()
    {
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_canonical entries={Entries.Count}");
        if (ValueWidth == CfgBinValueWidth.Unknown)
            throw new NotSupportedException("A T2B document without entries cannot establish a canonical value width.");
        if (Entries.Any(entry => entry.Name is null))
            throw new InvalidDataException("Canonical T2B writing requires every row CRC to resolve to exactly one name.");

        var encoding = GetEncoding(EncodingCode);
        var values = Entries.Select(entry => (IReadOnlyList<object?>)entry.Values.Select(value => value.Value).ToArray()).ToArray();
        var valueStrings = new List<byte>();
        var valueOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        stream.Position = 0x10;
        for (var index = 0; index < Entries.Count; index++)
            WriteEntry(stream, Entries[index], values[index], valueStrings, valueOffsets, encoding);
        WritePadding(stream, Align16(checked((int)stream.Length)) - checked((int)stream.Length));
        var stringOffset = checked((int)stream.Position);
        stream.Write(valueStrings.ToArray());
        WritePadding(stream, Align16(checked((int)stream.Length)) - checked((int)stream.Length));

        var names = Entries
            .GroupBy(entry => entry.Name!, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Crc: group.First().NameCrc))
            .ToArray();
        var nameSectionOffset = checked((int)stream.Position);
        var nameStringOffset = Align16(checked(0x10 + names.Length * 8));
        var nameStrings = new List<byte>();
        var nameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        stream.Position = checked(nameSectionOffset + 0x10);
        Span<byte> record = stackalloc byte[8];
        foreach (var name in names)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, name.Crc);
            BinaryPrimitives.WriteInt32LittleEndian(
                record[4..], EncodeString(name.Name, nameStrings, nameOffsets, encoding));
            stream.Write(record);
        }
        WritePadding(stream, checked(nameSectionOffset + nameStringOffset - (int)stream.Position));
        stream.Write(nameStrings.ToArray());
        WritePadding(stream, Align16(checked((int)stream.Length)) - checked((int)stream.Length));
        var footerOffset = checked((int)stream.Position);
        var nameSectionSize = checked(footerOffset - nameSectionOffset);

        stream.Position = 0;
        Span<byte> header = stackalloc byte[0x10];
        BinaryPrimitives.WriteInt32LittleEndian(header, Entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], stringOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], valueStrings.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], valueOffsets.Count);
        stream.Write(header);

        stream.Position = nameSectionOffset;
        Span<byte> nameHeader = stackalloc byte[0x10];
        BinaryPrimitives.WriteInt32LittleEndian(nameHeader, nameSectionSize);
        BinaryPrimitives.WriteInt32LittleEndian(nameHeader[4..], names.Length);
        BinaryPrimitives.WriteInt32LittleEndian(nameHeader[8..], nameStringOffset);
        BinaryPrimitives.WriteInt32LittleEndian(nameHeader[12..], nameStrings.Count);
        stream.Write(nameHeader);

        stream.Position = footerOffset;
        Span<byte> footer = stackalloc byte[0x10];
        footer.Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, T2bFooterMagic);
        BinaryPrimitives.WriteInt16LittleEndian(footer[4..], 0x01FE);
        BinaryPrimitives.WriteInt16LittleEndian(footer[6..], EncodingCode == 0 ? (short)0 : (short)1);
        BinaryPrimitives.WriteInt16LittleEndian(footer[8..], 1);
        stream.Write(footer);

        var result = stream.ToArray();
        _ = Read(result);
        return result;
    }

    public byte[] WriteWithValueEdits(IEnumerable<CfgBinValueEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var operations = edits.ToArray();
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_value_edits count={operations.Length}");

        var result = (byte[])_source.Clone();
        var editedLocations = new HashSet<(int EntryIndex, int ValueIndex)>();
        foreach (var edit in operations)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if ((uint)edit.EntryIndex >= (uint)Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "The entry index is outside the document.");

            var entry = Entries[edit.EntryIndex];
            if ((uint)edit.ValueIndex >= (uint)entry.Values.Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "The value index is outside the entry.");
            if (!editedLocations.Add((edit.EntryIndex, edit.ValueIndex)))
                throw new ArgumentException("A value can only be edited once per write operation.", nameof(edits));

            var value = entry.Values[edit.ValueIndex];
            switch (value.Type)
            {
                case CfgBinValueType.String:
                    throw new NotSupportedException(
                        "String edits require rebuilding and rebasing the T2B string section.");
                case CfgBinValueType.Integer:
                    WriteIntegerEdit(result, value.FileOffset, edit.Value);
                    break;
                case CfgBinValueType.FloatingPoint:
                    WriteFloatingPointEdit(result, value.FileOffset, edit.Value);
                    break;
                default:
                    throw new NotSupportedException("Unsupported T2B value types cannot be edited safely.");
            }
        }

        return result;
    }

    public byte[] WriteWithFixedStringEdits(IEnumerable<CfgBinValueEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        var operations = edits.ToArray();
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_fixed_string_edits count={operations.Length}");
        if (operations.Length == 0) return WriteUnmodified();
        var encoding = GetEncoding(EncodingCode);
        var result = (byte[])_source.Clone();
        var editedLocations = new HashSet<(int EntryIndex, int ValueIndex)>();

        foreach (var edit in operations)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if ((uint)edit.EntryIndex >= (uint)Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "The entry index is outside the document.");
            var entry = Entries[edit.EntryIndex];
            if ((uint)edit.ValueIndex >= (uint)entry.Values.Count)
                throw new ArgumentOutOfRangeException(nameof(edits), "The value index is outside the entry.");
            if (!editedLocations.Add((edit.EntryIndex, edit.ValueIndex)))
                throw new ArgumentException("A value can only be edited once per write operation.", nameof(edits));

            var value = entry.Values[edit.ValueIndex];
            if (value.Type != CfgBinValueType.String || value.Value is not string || value.RawValue < 0)
                throw new ArgumentException("A fixed string edit requires an existing non-null string value.", nameof(edits));
            if (edit.Value is not string replacement)
                throw new ArgumentException("A fixed string replacement must be a string.", nameof(edits));
            if (replacement.IndexOf('\0') >= 0)
                throw new ArgumentException("A fixed string replacement cannot contain a null character.", nameof(edits));

            var references = Entries.SelectMany(candidate => candidate.Values)
                .Count(candidate => candidate.Type == CfgBinValueType.String && candidate.RawValue == value.RawValue);
            if (references != 1)
                throw new InvalidOperationException("The existing string allocation is shared by multiple values.");

            var encoded = encoding.GetBytes(replacement);
            var allocationOffset = checked(StringDataOffset + (int)value.RawValue);
            var allocationEnd = Array.IndexOf(_source, (byte)0, allocationOffset, StringDataOffset + StringDataLength - allocationOffset);
            if (allocationEnd < 0)
                throw new InvalidDataException("The existing string allocation is not null terminated.");
            var allocationLength = allocationEnd - allocationOffset;
            if (encoded.Length > allocationLength)
                throw new ArgumentException(
                    $"The replacement requires {encoded.Length} bytes but the existing allocation contains {allocationLength}.",
                    nameof(edits));

            result.AsSpan(allocationOffset, allocationLength).Fill((byte)' ');
            result[allocationEnd] = 0;
            encoded.CopyTo(result.AsSpan(allocationOffset));
        }

        _ = Read(result);
        return result;
    }

    public byte[] WriteWithAppendedEntries(IEnumerable<CfgBinEntryAppend> appends)
    {
        ArgumentNullException.ThrowIfNull(appends);
        if (ValueWidth == CfgBinValueWidth.Unknown)
            throw new NotSupportedException("Rows cannot be appended to a T2B document without a verified value width.");

        var operations = appends.ToArray();
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_appended_entries count={operations.Length}");
        if (operations.Length == 0) return WriteUnmodified();
        var encoding = GetEncoding(EncodingCode);
        var stringData = new List<byte>(_source.AsSpan(StringDataOffset, StringDataLength).ToArray());
        var stringOffsets = Entries
            .SelectMany(entry => entry.Values)
            .Where(value => value.Type == CfgBinValueType.String && value.Value is string && value.RawValue >= 0)
            .GroupBy(value => (string)value.Value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => checked((int)group.First().RawValue), StringComparer.Ordinal);
        var originalKnownStringCount = stringOffsets.Count;

        using var entryStream = new MemoryStream();
        entryStream.Write(_source, 0, _entryDataEnd);
        foreach (var operation in operations)
        {
            if ((uint)operation.TemplateEntryIndex >= (uint)Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(appends), "The template entry index is outside the document.");
            var template = Entries[operation.TemplateEntryIndex];
            if (operation.Values.Count != template.Values.Count)
                throw new ArgumentException("An appended row must have the same value count as its template.", nameof(appends));
            WriteEntry(entryStream, template, operation.Values, stringData, stringOffsets, encoding);
        }

        WritePadding(entryStream, Align16(checked((int)entryStream.Length)) - checked((int)entryStream.Length));
        var newStringDataOffset = checked((int)entryStream.Length);
        entryStream.Write(stringData.ToArray());
        WritePadding(entryStream, Align16(checked((int)entryStream.Length)) - checked((int)entryStream.Length));
        var oldNameTableOffset = Align16(checked(StringDataOffset + StringDataLength));
        entryStream.Write(_source, oldNameTableOffset, _source.Length - oldNameTableOffset);

        var result = entryStream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x00, 4), checked(EntryCount + operations.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x04, 4), newStringDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x08, 4), stringData.Count);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(0x0C, 4),
            checked(StringDataCount + stringOffsets.Count - originalKnownStringCount));
        return Read(result).WriteCanonical();
    }

    public byte[] WriteWithInsertedEntries(IEnumerable<CfgBinEntryInsert> inserts)
    {
        ArgumentNullException.ThrowIfNull(inserts);
        if (ValueWidth == CfgBinValueWidth.Unknown)
            throw new NotSupportedException("Rows cannot be inserted into a T2B document without a verified value width.");

        var operations = inserts.ToArray();
        global::System.Diagnostics.Trace.WriteLine($"cfgbin_write_inserted_entries count={operations.Length}");
        if (operations.Length == 0) return WriteUnmodified();
        foreach (var operation in operations)
        {
            if ((uint)operation.BeforeEntryIndex > (uint)Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(inserts), "The insertion position is outside the document.");
            if ((uint)operation.TemplateEntryIndex >= (uint)Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(inserts), "The template entry index is outside the document.");
            if (operation.Values.Count != Entries[operation.TemplateEntryIndex].Values.Count)
                throw new ArgumentException("An inserted row must have the same value count as its template.", nameof(inserts));
        }

        var encoding = GetEncoding(EncodingCode);
        var stringData = new List<byte>(_source.AsSpan(StringDataOffset, StringDataLength).ToArray());
        var stringOffsets = Entries
            .SelectMany(entry => entry.Values)
            .Where(value => value.Type == CfgBinValueType.String && value.Value is string && value.RawValue >= 0)
            .GroupBy(value => (string)value.Value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => checked((int)group.First().RawValue), StringComparer.Ordinal);
        var originalKnownStringCount = stringOffsets.Count;
        var byPosition = operations
            .Select((operation, order) => (operation, order))
            .GroupBy(item => item.operation.BeforeEntryIndex)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.order).Select(item => item.operation));

        using var entryStream = new MemoryStream();
        entryStream.Write(_source, 0, 0x10);
        for (var entryIndex = 0; entryIndex <= Entries.Count; entryIndex++)
        {
            if (byPosition.TryGetValue(entryIndex, out var positioned))
            {
                foreach (var operation in positioned)
                    WriteEntry(
                        entryStream,
                        Entries[operation.TemplateEntryIndex],
                        operation.Values,
                        stringData,
                        stringOffsets,
                        encoding);
            }
            if (entryIndex == Entries.Count) break;
            var range = _entryRanges[entryIndex];
            entryStream.Write(_source, range.Start, range.End - range.Start);
        }

        WritePadding(entryStream, Align16(checked((int)entryStream.Length)) - checked((int)entryStream.Length));
        var newStringDataOffset = checked((int)entryStream.Length);
        entryStream.Write(stringData.ToArray());
        WritePadding(entryStream, Align16(checked((int)entryStream.Length)) - checked((int)entryStream.Length));
        var oldNameTableOffset = Align16(checked(StringDataOffset + StringDataLength));
        entryStream.Write(_source, oldNameTableOffset, _source.Length - oldNameTableOffset);

        var result = entryStream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x00, 4), checked(EntryCount + operations.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x04, 4), newStringDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x08, 4), stringData.Count);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(0x0C, 4),
            checked(StringDataCount + stringOffsets.Count - originalKnownStringCount));
        return Read(result).WriteCanonical();
    }

    private void WriteEntry(
        Stream destination,
        CfgBinEntry template,
        IReadOnlyList<object?> values,
        List<byte> stringData,
        IDictionary<string, int> stringOffsets,
        Encoding encoding)
    {
        Span<byte> header = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header, template.NameCrc);
        header[4] = checked((byte)values.Count);
        destination.Write(header);
        var typeByteCount = (values.Count + 3) / 4;
        var packedTypes = new byte[typeByteCount];
        for (var index = 0; index < values.Count; index++)
            packedTypes[index / 4] |= checked((byte)((byte)template.Values[index].Type << ((index % 4) * 2)));
        destination.Write(packedTypes);
        WritePadding(destination, Align4(checked((int)destination.Position)) - checked((int)destination.Position));

        for (var index = 0; index < values.Count; index++)
        {
            var templateValue = template.Values[index];
            var rawValue = EncodeValue(templateValue.Type, values[index], stringData, stringOffsets, encoding);
            var encoded = new byte[8];
            if (ValueWidth == CfgBinValueWidth.Int32)
                BinaryPrimitives.WriteInt32LittleEndian(encoded, checked((int)rawValue));
            else
                BinaryPrimitives.WriteInt64LittleEndian(encoded, rawValue);
            destination.Write(encoded.AsSpan(0, (int)ValueWidth));
        }
    }

    private long EncodeValue(
        CfgBinValueType type,
        object? value,
        List<byte> stringData,
        IDictionary<string, int> stringOffsets,
        Encoding encoding) => type switch
        {
            CfgBinValueType.String => EncodeString(value, stringData, stringOffsets, encoding),
            CfgBinValueType.Integer when ValueWidth == CfgBinValueWidth.Int32 && value is int intValue => intValue,
            CfgBinValueType.Integer when ValueWidth == CfgBinValueWidth.Int64 && value is long longValue => longValue,
            CfgBinValueType.FloatingPoint when ValueWidth == CfgBinValueWidth.Int32 && value is float single =>
                BitConverter.SingleToInt32Bits(single),
            CfgBinValueType.FloatingPoint when ValueWidth == CfgBinValueWidth.Int64 && value is double doubleValue =>
                BitConverter.DoubleToInt64Bits(doubleValue),
            CfgBinValueType.Integer => throw new ArgumentException("An appended integer must match the document value width."),
            CfgBinValueType.FloatingPoint => throw new ArgumentException("An appended floating-point value must match the document value width."),
            _ => throw new NotSupportedException("Unsupported T2B value types cannot be appended safely."),
        };

    private static int EncodeString(
        object? value,
        List<byte> stringData,
        IDictionary<string, int> stringOffsets,
        Encoding encoding)
    {
        if (value is null) return -1;
        if (value is not string text) throw new ArgumentException("An appended string value must be a string or null.");
        if (stringOffsets.TryGetValue(text, out var existingOffset)) return existingOffset;
        var offset = stringData.Count;
        stringData.AddRange(encoding.GetBytes(text));
        stringData.Add(0);
        stringOffsets.Add(text, offset);
        return offset;
    }

    private static void WritePadding(Stream destination, int count)
    {
        for (var index = 0; index < count; index++) destination.WriteByte(0xFF);
    }

    private void WriteIntegerEdit(byte[] destination, int offset, object value)
    {
        if (ValueWidth == CfgBinValueWidth.Int32 && value is int int32)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, 4), int32);
            return;
        }
        if (ValueWidth == CfgBinValueWidth.Int64 && value is long int64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(offset, 8), int64);
            return;
        }
        throw new ArgumentException(
            $"Integer edits must use {(ValueWidth == CfgBinValueWidth.Int32 ? "Int32" : "Int64")} values.",
            nameof(value));
    }

    private void WriteFloatingPointEdit(byte[] destination, int offset, object value)
    {
        if (ValueWidth == CfgBinValueWidth.Int32 && value is float single)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                destination.AsSpan(offset, 4),
                BitConverter.SingleToInt32Bits(single));
            return;
        }
        if (ValueWidth == CfgBinValueWidth.Int64 && value is double doubleValue)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                destination.AsSpan(offset, 8),
                BitConverter.DoubleToInt64Bits(doubleValue));
            return;
        }
        throw new ArgumentException(
            $"Floating-point edits must use {(ValueWidth == CfgBinValueWidth.Int32 ? "Single" : "Double")} values.",
            nameof(value));
    }

    private static bool TryReadEntries(
        byte[] source,
        int entryCount,
        int stringDataOffset,
        int stringDataLength,
        CfgBinValueWidth width,
        out IReadOnlyList<RawEntry> entries,
        out int entryDataEnd)
    {
        var result = new RawEntry[entryCount];
        var cursor = 0x10;
        try
        {
            for (var index = 0; index < entryCount; index++)
            {
                var entryStart = cursor;
                if (cursor > stringDataOffset - 5) throw new InvalidDataException();
                var crc = ReadUInt32(source, cursor, "entry CRC");
                var valueCount = source[cursor + 4];
                cursor += 5;
                var typeByteCount = (valueCount + 3) / 4;
                if (cursor > stringDataOffset - typeByteCount) throw new InvalidDataException();
                var types = new CfgBinValueType[valueCount];
                for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
                {
                    var code = (source[cursor + valueIndex / 4] >> ((valueIndex % 4) * 2)) & 3;
                    types[valueIndex] = (CfgBinValueType)code;
                    if (types[valueIndex] == CfgBinValueType.Unknown) throw new InvalidDataException();
                }
                cursor = Align4(checked(cursor + typeByteCount));
                var values = new RawValue[valueCount];
                for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
                {
                    var fileOffset = cursor;
                    long rawValue = width == CfgBinValueWidth.Int32
                        ? ReadInt32(source, cursor, "entry value")
                        : ReadInt64(source, cursor, "entry value");
                    cursor = checked(cursor + (int)width);
                    if (types[valueIndex] == CfgBinValueType.String
                        && rawValue != -1
                        && (rawValue < 0 || rawValue >= stringDataLength))
                    {
                        throw new InvalidDataException();
                    }
                    values[valueIndex] = new RawValue(rawValue, fileOffset);
                }
                result[index] = new RawEntry(crc, types, values, entryStart, cursor);
            }

            entryDataEnd = cursor;
            if (Align16(cursor) != stringDataOffset) throw new InvalidDataException();
            for (; cursor < stringDataOffset; cursor++)
                if (source[cursor] != 0xFF) throw new InvalidDataException();
            entries = result;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            entries = [];
            entryDataEnd = 0;
            return false;
        }
    }

    private static Dictionary<uint, IReadOnlyList<string>> ParseNameTable(
        byte[] source,
        int sectionOffset,
        int footerOffset,
        Encoding encoding)
    {
        EnsureRange(source, sectionOffset, 0x10, "name table header");
        var sectionSize = ReadBoundedInt32(source, sectionOffset, "name table size");
        var count = ReadBoundedInt32(source, sectionOffset + 4, "name count");
        var stringOffset = ReadBoundedInt32(source, sectionOffset + 8, "name-string offset");
        var stringLength = ReadBoundedInt32(source, sectionOffset + 12, "name-string length");
        if (sectionSize < 0x10 || sectionOffset + (long)sectionSize != footerOffset)
            throw new InvalidDataException("The T2B name table does not end at the footer.");
        EnsureRange(source, sectionOffset + 0x10, checked(count * 8), "name records");
        var stringBase = checked(sectionOffset + stringOffset);
        var recordsLength = checked(count * 8);
        var minimumStringOffset = checked(0x10 + recordsLength);
        if (stringOffset < minimumStringOffset || stringOffset > sectionSize - stringLength)
            throw new InvalidDataException("The T2B name strings overlap the name records or section boundary.");
        EnsureRange(source, stringBase, stringLength, "name strings");
        if (stringBase + stringLength > footerOffset)
            throw new InvalidDataException("The T2B name strings overlap the footer.");

        var lookup = new Dictionary<uint, List<string>>();
        for (var index = 0; index < count; index++)
        {
            var recordOffset = sectionOffset + 0x10 + index * 8;
            var crc = ReadUInt32(source, recordOffset, "name CRC");
            var relativeOffset = ReadBoundedInt32(source, recordOffset + 4, "name offset");
            var name = ReadNullTerminatedString(source, stringBase, stringLength, relativeOffset, encoding);
            if (!lookup.TryGetValue(crc, out var names)) lookup.Add(crc, names = []);
            names.Add(name);
        }
        return lookup.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static object? DecodeValue(
        CfgBinValueType type,
        long rawValue,
        CfgBinValueWidth width,
        byte[] source,
        int stringDataOffset,
        int stringDataLength,
        Encoding encoding) => type switch
        {
            CfgBinValueType.String => rawValue == -1
                ? null
                : ReadNullTerminatedString(source, stringDataOffset, stringDataLength, checked((int)rawValue), encoding),
            CfgBinValueType.Integer => DecodeInteger(rawValue, width),
            CfgBinValueType.FloatingPoint => DecodeFloatingPoint(rawValue, width),
            _ => throw new InvalidDataException("Unsupported T2B value type 3 was encountered."),
        };

    private static object DecodeInteger(long rawValue, CfgBinValueWidth width) =>
        width == CfgBinValueWidth.Int32 ? (object)(int)rawValue : rawValue;

    private static object DecodeFloatingPoint(long rawValue, CfgBinValueWidth width) =>
        width == CfgBinValueWidth.Int32
            ? (object)BitConverter.Int32BitsToSingle((int)rawValue)
            : BitConverter.Int64BitsToDouble(rawValue);

    private static string ReadNullTerminatedString(
        byte[] source,
        int blockOffset,
        int blockLength,
        int relativeOffset,
        Encoding encoding)
    {
        if (relativeOffset < 0 || relativeOffset >= blockLength)
            throw new InvalidDataException("A T2B string offset points outside its string block.");
        var start = checked(blockOffset + relativeOffset);
        var end = start;
        var limit = checked(blockOffset + blockLength);
        while (end < limit && source[end] != 0) end++;
        if (end == limit) throw new InvalidDataException("A T2B string is not null terminated.");
        return encoding.GetString(source, start, end - start);
    }

    private static Encoding GetEncoding(short encodingCode)
    {
        if (encodingCode is 1 or 256 or 257)
            return new UTF8Encoding(false, true);
        if (encodingCode == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        throw new InvalidDataException($"Unsupported T2B encoding code {encodingCode}.");
    }

    private static int ReadBoundedInt32(byte[] source, int offset, string field)
    {
        var value = ReadUInt32(source, offset, field);
        if (value > int.MaxValue) throw new InvalidDataException($"The T2B {field} exceeds supported bounds.");
        return (int)value;
    }

    private static uint ReadUInt32(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, 4, field);
        return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, 4, field);
        return BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, 4));
    }

    private static long ReadInt64(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, 8, field);
        return BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(offset, 8));
    }

    private static void EnsureRange(byte[] source, int offset, int length, string field)
    {
        if (offset < 0 || length < 0 || offset > source.Length - length)
            throw new InvalidDataException($"The T2B {field} points outside the file.");
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
    private static int Align16(int value) => checked((value + 15) & ~15);

    private sealed record RawEntry(
        uint NameCrc,
        IReadOnlyList<CfgBinValueType> Types,
        IReadOnlyList<RawValue> Values,
        int StartOffset,
        int EndOffset);

    private readonly record struct RawValue(long Bits, int FileOffset);
}
