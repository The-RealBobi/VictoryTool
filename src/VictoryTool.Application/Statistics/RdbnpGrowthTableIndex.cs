using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Statistics;

public sealed record GrowthLevel1Row(
    int MainPosition,
    int SubPosition,
    int PlayStyle,
    CharacterStatBlock Stats);

public sealed record GrowthLevel30Row(
    int MainPosition,
    int SubPosition,
    int GrowthPattern,
    int CharacterRank,
    CharacterStatBlock Stats);

public sealed record GrowthMainRow(
    int MainPosition,
    int GrowthPattern,
    int CharacterRank,
    CharacterStatBlock Level50,
    CharacterStatBlock Level99);

public sealed record GrowthSubRow(
    int SubPosition,
    int GrowthPattern,
    int CharacterRank,
    CharacterStatBlock Level50,
    CharacterStatBlock Level99);

public sealed class RdbnpGrowthTableIndex
{
    private readonly IReadOnlyDictionary<(int Position, int Growth, int Rank), GrowthMainRow> _main;
    private readonly IReadOnlyDictionary<(int Position, int Growth, int Rank), GrowthSubRow> _sub;
    private readonly IReadOnlyDictionary<(int Main, int Sub, int PlayStyle), GrowthLevel1Row> _level1;
    private readonly IReadOnlyDictionary<(int Main, int Sub, int Growth, int Rank), GrowthLevel30Row> _level30;

    public RdbnpGrowthTableIndex(
        IReadOnlyList<GrowthMainRow> mainRows,
        IReadOnlyList<GrowthSubRow> subRows,
        IReadOnlyList<GrowthLevel1Row>? level1Rows = null,
        IReadOnlyList<GrowthLevel30Row>? level30Rows = null)
    {
        ArgumentNullException.ThrowIfNull(mainRows);
        ArgumentNullException.ThrowIfNull(subRows);
        MainRows = mainRows;
        SubRows = subRows;
        Level1Rows = level1Rows ?? [];
        Level30Rows = level30Rows ?? [];
        _main = mainRows.ToDictionary(row => (row.MainPosition, row.GrowthPattern, row.CharacterRank));
        _sub = subRows.ToDictionary(row => (row.SubPosition, row.GrowthPattern, row.CharacterRank));
        _level1 = Level1Rows.ToDictionary(row => (row.MainPosition, row.SubPosition, row.PlayStyle));
        _level30 = Level30Rows.ToDictionary(
            row => (row.MainPosition, row.SubPosition, row.GrowthPattern, row.CharacterRank));
    }

    public IReadOnlyList<GrowthLevel1Row> Level1Rows { get; }
    public IReadOnlyList<GrowthLevel30Row> Level30Rows { get; }
    public IReadOnlyList<GrowthMainRow> MainRows { get; }
    public IReadOnlyList<GrowthSubRow> SubRows { get; }

    public GrowthMainRow? GetMain(int mainPosition, int growthPattern, int characterRank) =>
        _main.GetValueOrDefault((mainPosition, growthPattern, characterRank));

    public GrowthSubRow? GetSub(int subPosition, int growthPattern, int characterRank) =>
        _sub.GetValueOrDefault((subPosition, growthPattern, characterRank));

    public GrowthLevel1Row? GetLevel1(int mainPosition, int subPosition, int playStyle) =>
        _level1.GetValueOrDefault((mainPosition, subPosition, playStyle));

    public GrowthLevel30Row? GetLevel30(
        int mainPosition, int subPosition, int growthPattern, int characterRank) =>
        _level30.GetValueOrDefault((mainPosition, subPosition, growthPattern, characterRank));

    public static RdbnpGrowthTableIndex FromDocument(RdbnpDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var operation = GlobalLog.BeginOperation("growth_tables_index");
        var level1 = GetSingleList(document, "m_growthTableLv1List").Rows.Select(row =>
            new GrowthLevel1Row(
                ReadInteger(row, "mainPosition"),
                ReadInteger(row, "subPosition"),
                ReadInteger(row, "playStyle"),
                ReadStats(row, "_1"))).ToArray();
        var level30 = GetSingleList(document, "m_growthTableLv30List").Rows.Select(row =>
            new GrowthLevel30Row(
                ReadInteger(row, "mainPosition"),
                ReadInteger(row, "subPosition"),
                ReadInteger(row, "growthPattern"),
                ReadInteger(row, "charaRank"),
                ReadStats(row, "_30"))).ToArray();
        var main = GetSingleList(document, "m_growthTableMainList").Rows.Select(row =>
            new GrowthMainRow(
                ReadInteger(row, "mainPosition"),
                ReadInteger(row, "growthPattern"),
                ReadInteger(row, "charaRank"),
                ReadStats(row, "_50"),
                ReadStats(row, "_99"))).ToArray();
        var sub = GetSingleList(document, "m_growthTableSubList").Rows.Select(row =>
            new GrowthSubRow(
                ReadInteger(row, "subPosition"),
                ReadInteger(row, "growthPattern"),
                ReadInteger(row, "charaRank"),
                ReadStats(row, "_50"),
                ReadStats(row, "_99"))).ToArray();
        var result = new RdbnpGrowthTableIndex(main, sub, level1, level30);
        GlobalLog.Info("growth_tables_indexed", new Dictionary<string, object?>
        {
            ["level1Count"] = level1.Length,
            ["level30Count"] = level30.Length,
            ["mainCount"] = main.Length,
            ["subCount"] = sub.Length,
        });
        return result;
    }

    private static RdbnpList GetSingleList(RdbnpDocument document, string name)
    {
        var lists = document.Lists.Where(list => list.Name == name).ToArray();
        return lists.Length == 1
            ? lists[0]
            : throw new InvalidDataException($"Expected exactly one RDBNP list '{name}', found {lists.Length}.");
    }

    private static CharacterStatBlock ReadStats(RdbnpRow row, string suffix) => new(
        ReadInteger(row, "Kc" + suffix),
        ReadInteger(row, "Cr" + suffix),
        ReadInteger(row, "Tc" + suffix),
        ReadInteger(row, "Pr" + suffix),
        ReadInteger(row, "Ps" + suffix),
        ReadInteger(row, "Ag" + suffix),
        ReadInteger(row, "It" + suffix));

    private static int ReadInteger(RdbnpRow row, string fieldName)
    {
        var values = row.GetValues(fieldName);
        if (values.Count != 1)
            throw new InvalidDataException($"RDBNP field '{fieldName}' is not scalar.");
        return values[0] switch
        {
            byte value => value,
            short value => value,
            int value => value,
            uint value when value <= int.MaxValue => (int)value,
            _ => throw new InvalidDataException($"RDBNP field '{fieldName}' is not an integer."),
        };
    }
}
