using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

internal static class T2bCountedListWriter
{
    public static byte[] InsertClones(
        CfgBinDocument document,
        CfgBinEntry template,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        string beginName,
        string endName,
        int? insertionIndex = null,
        IReadOnlyList<(CfgBinEntry Template, IReadOnlyList<object?> Values)>? companionRows = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return document.WriteUnmodified();

        var begin = document.Entries.Take(template.Index + 1)
            .LastOrDefault(entry => entry.Name == beginName);
        var end = document.Entries.Skip(template.Index + 1)
            .FirstOrDefault(entry => entry.Name == endName);
        if (begin is null || end is null || begin.Index >= template.Index || template.Index >= end.Index)
        {
            // Minimal unit fixtures created before counted-list support contain one template row only.
            if (document.EntryCount == 1)
                return document.WriteWithAppendedEntries(rows.Select(values =>
                    new CfgBinEntryAppend(template.Index, values)));
            throw new InvalidDataException(
                $"Template row '{template.Name}' is not inside {beginName}/{endName}.");
        }
        if (begin.Values.Count == 0 || begin.Values[0].Type != CfgBinValueType.Integer)
            throw new InvalidDataException($"{beginName} has no integer row count.");

        var oldCount = GetInteger(begin.Values[0]);
        var actualCount = document.Entries.Skip(begin.Index + 1).Take(end.Index - begin.Index - 1)
            .Count(entry => entry.Name == template.Name);
        if (oldCount != actualCount)
            throw new InvalidDataException(
                $"{beginName} declares {oldCount} rows but contains {actualCount} '{template.Name}' rows.");

        var updated = CfgBinDocument.Read(document.WriteWithValueEdits(
            [new CfgBinValueEdit(begin.Index, 0, AddCount(document, oldCount, rows.Count))]));
        var beforeIndex = insertionIndex ?? end.Index;
        if (beforeIndex <= begin.Index || beforeIndex > end.Index)
            throw new InvalidDataException($"The insertion boundary for {beginName} lies outside its list.");
        var inserts = rows.Select(values =>
                new CfgBinEntryInsert(beforeIndex, template.Index, values))
            .Concat((companionRows ?? []).Select(row =>
                new CfgBinEntryInsert(beforeIndex, row.Template.Index, row.Values)))
            .ToArray();
        var result = updated.WriteWithInsertedEntries(inserts);
        var restored = CfgBinDocument.Read(result);
        var restoredBegin = restored.Entries[begin.Index];
        var restoredEnd = restored.Entries.Single(entry => entry.Name == endName && entry.Index > begin.Index);
        if (GetInteger(restoredBegin.Values[0]) != oldCount + rows.Count
            || restoredEnd.Index - end.Index != inserts.Length)
            throw new InvalidDataException($"{beginName} insertion failed read-back validation.");
        return result;
    }

    private static object AddCount(CfgBinDocument document, long count, int increment)
    {
        if (document.ValueWidth == CfgBinValueWidth.Int32)
            return checked((int)(count + increment));
        return checked(count + increment);
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("A counted-list marker is not an integer."),
    };
}
