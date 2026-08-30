using VictoryTool.Application.Profiles;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Statistics;

public enum CharacterStatKind
{
    Kick,
    Control,
    Technique,
    Pressure,
    Physical,
    Agility,
    Intelligence,
}

public readonly record struct CharacterLevel
{
    public CharacterLevel(int value)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Character level must be positive.");
        Value = value;
    }

    public int Value { get; }
}

public sealed record CharacterStatBlock(
    int Kick,
    int Control,
    int Technique,
    int Pressure,
    int Physical,
    int Agility,
    int Intelligence)
{
    public int GetValue(CharacterStatKind kind) => kind switch
    {
        CharacterStatKind.Kick => Kick,
        CharacterStatKind.Control => Control,
        CharacterStatKind.Technique => Technique,
        CharacterStatKind.Pressure => Pressure,
        CharacterStatKind.Physical => Physical,
        CharacterStatKind.Agility => Agility,
        CharacterStatKind.Intelligence => Intelligence,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record StatFormulaDefinition(
    string Version,
    int GrowthProfile,
    int Rank,
    int MinimumLevel,
    int MaximumLevel,
    IReadOnlyDictionary<int, CharacterStatBlock> ValuesByLevel,
    string Evidence);

public interface ICharacterStatFormulaProvider
{
    StatFormulaDefinition? GetDefinition(string version, int growthProfile, int rank);
}

public interface ICharacterStatCalculator
{
    CharacterStatCalculation Calculate(string version, int growthProfile, int rank, CharacterLevel level);
}

public interface IContextualCharacterStatCalculator : ICharacterStatCalculator
{
    CharacterStatCalculation Calculate(
        string version,
        int mainPosition,
        int subPosition,
        int playStyle,
        int growthProfile,
        int rank,
        CharacterLevel level);
}

public sealed record CharacterStatCalculation(
    bool IsAvailable,
    CharacterStatBlock? Stats,
    string? DiagnosticCode)
{
    public static CharacterStatCalculation Available(CharacterStatBlock stats) => new(true, stats, null);

    public static CharacterStatCalculation Unavailable(string diagnosticCode) => new(false, null, diagnosticCode);
}

public sealed class DocumentedStatFormulaProvider : ICharacterStatFormulaProvider
{
    private readonly IReadOnlyDictionary<FormulaKey, StatFormulaDefinition> _definitions;

    public DocumentedStatFormulaProvider(IEnumerable<StatFormulaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(
            definition => new FormulaKey(definition.Version, definition.GrowthProfile, definition.Rank));
    }

    public StatFormulaDefinition? GetDefinition(string version, int growthProfile, int rank) =>
        _definitions.GetValueOrDefault(new FormulaKey(version, growthProfile, rank));

    private readonly record struct FormulaKey(string Version, int GrowthProfile, int Rank);
}

public sealed class CharacterStatCalculator(ICharacterStatFormulaProvider formulaProvider) : ICharacterStatCalculator
{
    private readonly ICharacterStatFormulaProvider _formulaProvider =
        formulaProvider ?? throw new ArgumentNullException(nameof(formulaProvider));

    public CharacterStatCalculation Calculate(string version, int growthProfile, int rank, CharacterLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var definition = _formulaProvider.GetDefinition(version, growthProfile, rank);
        if (definition is null)
            return CharacterStatCalculation.Unavailable("statistics.formula_profile_unavailable");

        if (level.Value < definition.MinimumLevel || level.Value > definition.MaximumLevel)
            return CharacterStatCalculation.Unavailable("statistics.level_out_of_range");

        return definition.ValuesByLevel.TryGetValue(level.Value, out var stats)
            ? CharacterStatCalculation.Available(stats)
            : CharacterStatCalculation.Unavailable("statistics.level_value_unverified");
    }
}

public sealed class RdbnpGrowthStatCalculator(
    string version,
    RdbnpGrowthTableIndex index) : IContextualCharacterStatCalculator
{
    private readonly string _version = string.IsNullOrWhiteSpace(version)
        ? throw new ArgumentException("A dump version is required.", nameof(version))
        : version;
    private readonly RdbnpGrowthTableIndex _index = index ?? throw new ArgumentNullException(nameof(index));

    public static RdbnpGrowthStatCalculator Load(GameDumpProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var path = Path.Combine(
            profile.GameDataPath,
            "character",
            "growth_table_config_0.00.00.00.cfg.bin");
        var document = RdbnpDocument.Read(File.ReadAllBytes(path));
        return new RdbnpGrowthStatCalculator(profile.Version, RdbnpGrowthTableIndex.FromDocument(document));
    }

    public CharacterStatCalculation Calculate(
        string version,
        int growthProfile,
        int rank,
        CharacterLevel level) =>
        CharacterStatCalculation.Unavailable("statistics.position_context_required");

    public CharacterStatCalculation Calculate(
        string version,
        int mainPosition,
        int subPosition,
        int playStyle,
        int growthProfile,
        int rank,
        CharacterLevel level)
    {
        if (!string.Equals(version, _version, StringComparison.Ordinal))
            return CharacterStatCalculation.Unavailable("statistics.dump_version_mismatch");

        var stats = level.Value switch
        {
            1 => _index.GetLevel1(mainPosition, subPosition, playStyle)?.Stats,
            30 => _index.GetLevel30(mainPosition, subPosition, growthProfile, rank)?.Stats,
            50 => _index.GetMain(mainPosition, growthProfile, rank)?.Level50,
            99 => _index.GetMain(mainPosition, growthProfile, rank)?.Level99,
            _ => null,
        };
        return stats is not null
            ? CharacterStatCalculation.Available(stats)
            : CharacterStatCalculation.Unavailable("statistics.level_value_unverified");
    }
}
