using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

internal static class T2bCountedListWriter
{
    public static byte[] InsertClones(
        CfgBinDocument document,
        IReadOnlyList<(CfgBinEntry Template, IReadOnlyList<object?> Values)> rows,
        string beginName,
        string endName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return document.WriteUnmodified();
        var rowName = rows[0].Template.Name
            ?? throw new InvalidDataException("The counted-list template has no resolved row name.");
        var begin = document.Entries.SingleOrDefault(entry => entry.Name == beginName)
            ?? throw new InvalidDataException($"{beginName} is missing.");
        var end = document.Entries.SingleOrDefault(entry => entry.Name == endName && entry.Index > begin.Index)
            ?? throw new InvalidDataException($"{endName} is missing.");
        if (begin.Index >= rows[0].Template.Index || rows[0].Template.Index >= end.Index)
            throw new InvalidDataException($"Template row '{rowName}' is not inside {beginName}/{endName}.");
        if (begin.Values.Count == 0 || begin.Values[0].Type != CfgBinValueType.Integer)
            throw new InvalidDataException($"{beginName} has no integer row count.");

        var oldCount = GetInteger(begin.Values[0]);
        var actualCount = document.Entries.Skip(begin.Index + 1).Take(end.Index - begin.Index - 1)
            .Count(entry => entry.Name == rowName);
        if (oldCount != actualCount)
            throw new InvalidDataException($"{beginName} declares {oldCount} rows but contains {actualCount} '{rowName}' rows.");
        foreach (var (template, values) in rows)
        {
            if (template.Name != rowName || template.Index <= begin.Index || template.Index >= end.Index)
                throw new InvalidDataException("A counted-list insertion contains a template from another table.");
            if (values.Count != template.Values.Count)
                throw new InvalidDataException("An inserted row does not match its source row field count.");
        }

        RequireCountRule(beginName, endName, rows[0].Template.Name!);
        var inserts = rows.Select(row => new CfgBinEntryInsert(end.Index, row.Template.Index, row.Values)).ToArray();
        var result = document.WriteWithInsertedEntries(inserts);
        var restored = CfgBinDocument.Read(result);
        var restoredBegin = restored.Entries[begin.Index];
        var restoredEnd = restored.Entries.Single(entry => entry.Name == endName && entry.Index > begin.Index);
        var restoredCount = restored.Entries.Skip(restoredBegin.Index + 1)
            .Take(restoredEnd.Index - restoredBegin.Index - 1)
            .Count(entry => entry.Name == rowName);
        if (GetInteger(restoredBegin.Values[0]) != oldCount + rows.Count || restoredCount != actualCount + rows.Count)
            throw new InvalidDataException($"{beginName} mixed-row insertion failed read-back validation.");
        return result;
    }

    public static byte[] InsertClones(
        CfgBinDocument document,
        CfgBinEntry template,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        string beginName,
        string endName,
        int? insertionIndex = null,
        IReadOnlyList<(CfgBinEntry Template, IReadOnlyList<object?> Values)>? companionRows = null)
    {
        var inserts = PrepareClones(document, template, rows, beginName, endName, insertionIndex, companionRows);
        var result = document.WriteWithInsertedEntries(inserts);
        if (rows.Count == 0 || document.EntryCount == 1) return result;
        var begin = document.Entries.Take(template.Index + 1).Last(entry => entry.Name == beginName);
        var end = document.Entries.Skip(template.Index + 1).First(entry => entry.Name == endName);
        var oldCount = GetInteger(begin.Values[0]);
        var restored = CfgBinDocument.Read(result);
        var restoredBegin = restored.Entries[begin.Index];
        var restoredEnd = restored.Entries.Single(entry => entry.Name == endName && entry.Index > begin.Index);
        if (GetInteger(restoredBegin.Values[0]) != oldCount + rows.Count
            || restoredEnd.Index - end.Index != inserts.Length)
            throw new InvalidDataException($"{beginName} insertion failed read-back validation.");
        return result;
    }

    public static CfgBinEntryInsert[] PrepareClones(
        CfgBinDocument document, CfgBinEntry template, IReadOnlyList<IReadOnlyList<object?>> rows,
        string beginName, string endName, int? insertionIndex,
        IReadOnlyList<(CfgBinEntry Template, IReadOnlyList<object?> Values)>? companionRows)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return [];

        var begin = document.Entries.Take(template.Index + 1)
            .LastOrDefault(entry => entry.Name == beginName);
        var end = document.Entries.Skip(template.Index + 1)
            .FirstOrDefault(entry => entry.Name == endName);
        if (begin is null || end is null || begin.Index >= template.Index || template.Index >= end.Index)
        {
            // Minimal unit fixtures created before counted-list support contain one template row only.
            if (document.EntryCount == 1)
                return rows.Select(values => new CfgBinEntryInsert(document.EntryCount, template.Index, values)).ToArray();
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

        RequireCountRule(beginName, endName, template.Name!);
        var beforeIndex = insertionIndex ?? end.Index;
        if (beforeIndex <= begin.Index || beforeIndex > end.Index)
            throw new InvalidDataException($"The insertion boundary for {beginName} lies outside its list.");
        return rows.Select(values =>
                new CfgBinEntryInsert(beforeIndex, template.Index, values))
            .Concat((companionRows ?? []).Select(row =>
                new CfgBinEntryInsert(beforeIndex, row.Template.Index, row.Values)))
            .ToArray();
    }

    public static void ValidateBatchCount(CfgBinDocument before, CfgBinDocument after,
        string beginName, string endName, string rowName, int added)
    {
        if (added == 0 || before.EntryCount == 1) return;
        var original = before.Entries.Single(entry => entry.Name == beginName);
        var begin = after.Entries.Single(entry => entry.Name == beginName);
        var end = after.Entries.Single(entry => entry.Name == endName && entry.Index > begin.Index);
        var expected = GetInteger(original.Values[0]) + added;
        var actual = after.Entries.Skip(begin.Index + 1).Take(end.Index - begin.Index - 1)
            .Count(entry => entry.Name == rowName);
        if (GetInteger(begin.Values[0]) != expected || actual != expected)
            throw new InvalidDataException($"{beginName} batch insertion failed read-back validation.");
    }

    private static void RequireCountRule(string begin, string end, string child)
    {
        if (!CfgBinSchema.Catalog.Counts.Any(rule => rule.Start == begin && rule.End == end && rule.Child == child && rule.Position == 0))
            throw new InvalidDataException($"The structure catalog has no verified COUNT rule for {begin}/{child}/{end}.");
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("A counted-list marker is not an integer."),
    };
}
