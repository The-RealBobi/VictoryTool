namespace VictoryTool.CfgBin;

public sealed class DeliveryTextWriter
{
    public byte[] AppendTitle(ReadOnlySpan<byte> source, uint titleId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var document = CfgBinDocument.Read(source);
        var begin = document.Entries.Single(entry => entry.Name == "TEXT_INFO_BEGIN");
        var end = document.Entries.Single(entry => entry.Name == "TEXT_INFO_END");
        var template = document.Entries.FirstOrDefault(entry =>
            entry.Name == "TEXT_INFO" && entry.Values.Count >= 3 && entry.Values[2].Type == CfgBinValueType.String)
            ?? throw new InvalidDataException("The post text table has no compatible TEXT_INFO row.");
        if (document.Entries.Any(entry => entry.Name == "TEXT_INFO"
            && entry.Values.Count > 0 && GetUnsigned(entry.Values[0]) == titleId))
            throw new InvalidDataException("The delivery title ID already exists.");
        if (begin.Values.Count == 0 || GetSigned(begin.Values[0]) != end.Index - begin.Index - 1)
            throw new InvalidDataException("The post text counted list is inconsistent.");

        object countValue;
        if (document.ValueWidth == CfgBinValueWidth.Int32)
            countValue = checked((int)GetSigned(begin.Values[0]) + 1);
        else
            countValue = checked(GetSigned(begin.Values[0]) + 1);
        var updated = CfgBinDocument.Read(document.WriteWithValueEdits(
            [new CfgBinValueEdit(begin.Index, 0, countValue)]));
        var values = template.Values.Select(value => value.Value).ToArray();
        if (document.ValueWidth == CfgBinValueWidth.Int32)
            values[0] = unchecked((int)titleId);
        else
            values[0] = (long)titleId;
        values[1] = document.ValueWidth == CfgBinValueWidth.Int32 ? (object)0 : 0L;
        values[2] = title;
        var result = updated.WriteWithInsertedEntries(
            [new CfgBinEntryInsert(end.Index, template.Index, values)]);
        var restored = CfgBinDocument.Read(result);
        if (!restored.Entries.Any(entry => entry.Name == "TEXT_INFO"
            && entry.Values.Count >= 3 && GetUnsigned(entry.Values[0]) == titleId
            && Equals(entry.Values[2].Value, title)))
            throw new InvalidDataException("The delivery title failed read-back validation.");
        return result;
    }

    private static long GetSigned(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("Expected an integer T2B value."),
    };

    private static uint GetUnsigned(CfgBinValue value) => unchecked((uint)GetSigned(value));
}
