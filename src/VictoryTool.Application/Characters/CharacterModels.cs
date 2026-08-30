using System.Globalization;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Statistics;
using VictoryTool.CfgBin;
using VictoryTool.Application.Assets;

namespace VictoryTool.Application.Characters;

public enum CharacterDataConfidence
{
    InventoryOnly,
    Parsed,
    Confirmed,
}

public enum CharacterAffinity
{
    Neutral = 0,
    Wind = 1,
    Forest = 2,
    Fire = 3,
    Earth = 4,
    Unknown = 255,
}

/// <summary>
/// Values used by CHARA_BASE_INFO[11].  The dump does not use zero-based
/// gender IDs: 1 is male, 2 is female and 5 is the explicit unknown value.
/// </summary>
public static class CharacterGenderCatalog
{
    public static string ResolveName(int value, string locale = "en")
    {
        var spanish = string.Equals(locale, "es", StringComparison.OrdinalIgnoreCase);
        return value switch
        {
            1 => spanish ? "Chico" : "Male",
            2 => spanish ? "Chica" : "Female",
            5 => spanish ? "Desconocido" : "Unknown",
            _ => spanish ? $"Género {value}" : $"Gender {value}",
        };
    }
}

public static class CharacterBodyTypeCatalog
{
    /// <summary>
    /// The six editor meanings currently exposed by the tool. These are not
    /// the complete set of raw CHARA_BODY_INFO[5] values observed in a dump.
    /// </summary>
    public static IReadOnlyList<int> Values { get; } = [1, 2, 3, 4, 5, 6];

    public static IReadOnlyList<int> ObservedTableValues { get; } = [0, 1, 2, 3, 4, 5, 6, 7];

    public static string ResolveName(int value, string locale = "en") => (value, locale) switch
    {
        (1, "es") => "Bajo delgado",
        (2, "es") => "Medio delgado",
        (3, "es") => "Alto gordo",
        (4, "es") => "Bajo gordo",
        (5, "es") => "Alto delgado",
        (6, "es") => "Alto musculoso",
        (1, _) => "Short thin",
        (2, _) => "Medium thin",
        (3, _) => "Tall fat",
        (4, _) => "Short fat",
        (5, _) => "Tall thin",
        (6, _) => "Tall muscular",
        _ => locale == "es" ? $"Tipo corporal {value}" : $"Body type {value}",
    };
}

/// <summary>
/// The model files use a separate physical-name family from the semantic body
/// value in CHARA_BODY_INFO. In the current dump, sh00010x and u00010x share
/// the final six digits, so this helper exposes that evidence without
/// pretending the two table values are interchangeable.
/// </summary>
public static class CharacterBodyModelCatalog
{
    public static IReadOnlyList<string> ObservedPhysicalKeys { get; } =
        ["u000101", "u000102", "u000103", "u000104", "u000105", "u000106", "u000107", "u000108", "u000191"];

    public static string? ResolvePhysicalKey(string? bodyModelPath)
    {
        if (string.IsNullOrWhiteSpace(bodyModelPath)) return null;
        var fileName = Path.GetFileNameWithoutExtension(bodyModelPath.Replace('\\', '/'));
        if (fileName.StartsWith("u", StringComparison.OrdinalIgnoreCase)
            && fileName.Length == 7
            && fileName[1..].All(char.IsDigit))
            return $"u{fileName[1..]}";
        if (!fileName.StartsWith("sh", StringComparison.OrdinalIgnoreCase)
            || fileName.Length != 8
            || !fileName[2..].All(char.IsDigit))
            return null;
        return $"u{fileName[2..]}";
    }
}

public static class CharacterUniformVariantCatalog
{
    private const int ThinMaleMember = 0;
    private const int LargeMaleMember = 3;
    private const int SmallerFemaleMember = 1;
    private const int LargerFemaleMember = 2;

    /// <summary>
    /// Maps editor body semantics to the evidenced uniform portrait member.
    /// The returned values are catalog keys, not CHARA_BODY_INFO indexes.
    /// </summary>
    public static int Resolve(int gender, int bodyType, int chestSize) =>
        gender switch
        {
            1 when bodyType is 1 or 2 or 5 => ThinMaleMember,
            1 when bodyType is 3 or 4 or 6 => LargeMaleMember,
            2 when chestSize >= 2 => LargerFemaleMember,
            2 when chestSize is >= 0 and <= 1 => SmallerFemaleMember,
            _ => -1,
        };
}

/// <summary>
/// CHARA_BASE_INFO[22..30] are the per-game association flags.  The first
/// non-zero flag is used as the concise origin-game choice in the wizard;
/// the complete source tuple remains opaque and is copied unless edited.
/// </summary>
public static class CharacterOriginGameCatalog
{
    public const int FirstAssociationIndex = 22;
    public const int LastAssociationIndex = 30;

    public static int? ResolveChoice(IEnumerable<int> associationValues)
    {
        ArgumentNullException.ThrowIfNull(associationValues);
        var values = associationValues.ToArray();
        for (var offset = 0; offset <= LastAssociationIndex - FirstAssociationIndex; offset++)
            if (values.ElementAtOrDefault(offset) != 0)
                return FirstAssociationIndex + offset;
        return null;
    }

    public static string ResolveName(int index, string locale = "en")
    {
        var spanish = string.Equals(locale, "es", StringComparison.OrdinalIgnoreCase);
        return index switch
        {
            22 => "Inazuma Eleven 1",
            23 => "Inazuma Eleven 2",
            24 => "Inazuma Eleven 3",
            25 => "Inazuma Eleven GO 1",
            26 => "Inazuma Eleven GO 2",
            27 => "Inazuma Eleven GO 3",
            28 => "Inazuma Eleven ARES",
            29 => "Inazuma Eleven Orion",
            30 => "Inazuma Eleven Victory Road",
            _ => spanish ? $"Juego {index}" : $"Game {index}",
        };
    }
}

public enum CharacterPosition
{
    Unknown = 0,
    Goalkeeper = 1,
    Forward = 2,
    Midfielder = 3,
    Defender = 4,
}

public static class CharacterPositionCatalog
{
    public static CharacterPosition Resolve(int gameValue) => gameValue switch
    {
        1 => CharacterPosition.Goalkeeper,
        2 => CharacterPosition.Forward,
        3 => CharacterPosition.Midfielder,
        4 => CharacterPosition.Defender,
        _ => CharacterPosition.Unknown,
    };

    public static int? ResolveSpriteValue(int gameValue) => Resolve(gameValue) switch
    {
        CharacterPosition.Forward => 1,
        CharacterPosition.Midfielder => 2,
        CharacterPosition.Defender => 3,
        CharacterPosition.Goalkeeper => 4,
        _ => null,
    };

    public static string ResolveName(int gameValue) => Resolve(gameValue) switch
    {
        CharacterPosition.Goalkeeper => "Goalkeeper",
        CharacterPosition.Forward => "Forward",
        CharacterPosition.Midfielder => "Midfielder",
        CharacterPosition.Defender => "Defender",
        _ => "Unknown",
    };
}

public enum CharacterAssetPlatform
{
    Pc,
    Switch,
}

public enum CharacterOrigin
{
    Original,
    Custom,
    ImportedPackage,
}

public sealed record CharacterNameSet(string? FullName, string? FamilyName, string? GivenName);

public sealed record CharacterCatalogItem(
    string Id,
    string DisplayName,
    CharacterDataConfidence Confidence,
    string? PortraitResourcePath,
    CharacterAffinity Affinity = CharacterAffinity.Unknown,
    string Series = "Unknown",
    string Position = "Unknown",
    CharacterPortraitMetadata? PortraitMetadata = null,
    CharacterBaseMetadata? BaseMetadata = null,
    IReadOnlyList<CharacterVariantSummary>? Variants = null,
    IReadOnlyDictionary<string, CharacterLocalizedText>? Localizations = null,
    IReadOnlyDictionary<CharacterAssetPlatform, CharacterPortraitAsset>? Portraits = null,
    CharacterOrigin Origin = CharacterOrigin.Original,
    IReadOnlyDictionary<string, CharacterNameSet>? RomanizedNames = null,
    CharacterAssetAvailability? AssetAvailability = null,
    GameTaxonomyIndex? Taxonomy = null,
    UniformAssetDescriptor? UniformPreviewAsset = null,
    CharacterTextReferenceIndex? TextReferences = null,
    string? StandardPortraitResourcePath = null,
    string? UniformPortraitResourcePath = null);

public sealed record CharacterPortraitAsset(
    string ResourcePath,
    CharacterPortraitMetadata? Metadata);

public sealed record CharacterAssetAvailability(
    bool HasPcPortrait,
    bool HasSwitchPortrait,
    bool HasReadablePortrait,
    bool HasModelReference)
{
    public static CharacterAssetAvailability FromPortraits(
        IReadOnlyDictionary<CharacterAssetPlatform, CharacterPortraitAsset> portraits) => new(
            portraits.ContainsKey(CharacterAssetPlatform.Pc),
            portraits.ContainsKey(CharacterAssetPlatform.Switch),
            portraits.Values.Any(portrait => portrait.Metadata is not null),
            false);
}

public sealed record CharacterPortraitMetadata(
    int TextureCount,
    int Width,
    int Height,
    string PayloadFormat,
    bool HasStandardPortrait,
    bool HasUniformPortrait,
    string? StandardPortraitEntryName = null,
    string? UniformPortraitEntryName = null);

public sealed record CharacterBaseMetadata(
    int BaseId,
    string InternalName,
    int Gender,
    int BodyType,
    int AcademicYear,
    int SourceSeries,
    int FullNameTextId = 0,
    int ShortNameTextId = 0,
    int UpperNameTextId = 0,
    int DescriptionTextId = 0,
    int ModelId = 0,
    int BodyModelId = 0,
    int BodyGroup = -1,
    int BodyPoseType = -1,
    int UniformPortraitVariant = 0,
    int TeamAssociation1 = 0,
    int TeamAssociation2 = 0,
    int TeamAssociation3 = 0,
    string? HeadModelPath = null,
    string? BodyModelPath = null,
    uint SkinColorRgba = 0,
    int UniformModel = 0,
    int ShoesModel = 0,
    int GloveModel = 0,
    int EquipmentColor = 0,
    int UniformCollarOpen = 0,
    int EquipmentFlag2 = 0,
    int ChestSize = 0,
    int ForceKit = 0,
    int? OriginGameAssociationIndex = null,
    string? PhysicalBodyModelKey = null);

public sealed record CharacterLocalizedText(
    string Locale,
    string? FullName,
    string? FamilyName,
    string? GivenName,
    string? ShortName,
    string? UpperName,
    string? Description);

public sealed record CharacterSkillSlot(int SkillId, int UnlockLevel);

public sealed record CharacterVariantSummary(
    int ParameterId,
    int BaseId,
    CharacterAffinity Affinity,
    int MainPosition,
    int SubPosition,
    int PlayStyle,
    int Growth,
    int Rank,
    int AbilityBoardId,
    IReadOnlyList<CharacterSkillSlot> SkillSlots,
    int SpecialRarity,
    CharacterStatBlock? StatModifiers = null)
{
    public const int MainSkillSlotCount = 6;

    public string DisplayLabel => $"0x{unchecked((uint)ParameterId):X8} · {Affinity} · R{SpecialRarity}";

    public IEnumerable<CharacterSkillSlot> EnumerateMainSkillSlots() =>
        SkillSlots.Take(MainSkillSlotCount);

    public IEnumerable<CharacterSkillSlot> EnumerateAlternateSkillSlots() =>
        SkillSlots.Skip(MainSkillSlotCount);
}

public sealed record CharacterSemanticRecord(
    CharacterBaseMetadata Base,
    IReadOnlyList<CharacterVariantSummary> Variants);

public static class CharacterSemanticMapper
{
    public static IReadOnlyList<CharacterSemanticRecord> Map(
        IEnumerable<CfgBinEntry> baseEntries,
        IEnumerable<CfgBinEntry> parameterEntries,
        CancellationToken cancellationToken = default)
        => MapCore(baseEntries, parameterEntries, [], cancellationToken);

    public static IReadOnlyList<CharacterSemanticRecord> Map(
        IEnumerable<CfgBinEntry> baseEntries,
        IEnumerable<CfgBinEntry> parameterEntries,
        IEnumerable<CfgBinEntry> modelEntries,
        CancellationToken cancellationToken = default)
        => MapCore(baseEntries, parameterEntries, modelEntries, cancellationToken);

    private static IReadOnlyList<CharacterSemanticRecord> MapCore(
        IEnumerable<CfgBinEntry> baseEntries,
        IEnumerable<CfgBinEntry> parameterEntries,
        IEnumerable<CfgBinEntry> modelEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(parameterEntries);
        ArgumentNullException.ThrowIfNull(modelEntries);
        var models = modelEntries
            .Where(entry => entry.Name == "CHARA_MODEL_INFO" && entry.Values.Count >= 32)
            .GroupBy(entry => GetInteger(entry, 0))
            .ToDictionary(group => group.Key, group => group.First());
        var bodies = modelEntries
            .Where(entry => entry.Name == "CHARA_BODY_INFO" && entry.Values.Count >= 7)
            .GroupBy(entry => GetInteger(entry, 0))
            .ToDictionary(group => group.Key, group => group.First());
        var bases = new Dictionary<int, CharacterBaseMetadata>();
        foreach (var entry in baseEntries.Where(entry => entry.Name == "CHARA_BASE_INFO" && entry.Values.Count >= 20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseId = GetInteger(entry, 0);
            var internalName = GetString(entry, 1);
            if (string.IsNullOrWhiteSpace(internalName)) continue;
            var modelId = GetInteger(entry, 6);
            models.TryGetValue(modelId, out var model);
            var bodyModelId = model is null ? 0 : GetInteger(model, 4);
            bodies.TryGetValue(bodyModelId, out var body);
            var bodyType = body is null ? GetInteger(entry, 12) : GetInteger(body, 5);
            var chestSize = model is null ? 0 : GetInteger(model, 31);
            bases.TryAdd(baseId, new CharacterBaseMetadata(
                baseId,
                internalName,
                GetInteger(entry, 11),
                bodyType,
                GetInteger(entry, 13),
                GetInteger(entry, 15),
                GetInteger(entry, 3),
                GetInteger(entry, 4),
                GetInteger(entry, 5),
                GetInteger(entry, 19),
                modelId,
                bodyModelId,
                body is null ? -1 : GetInteger(body, 4),
                body is null ? -1 : GetInteger(body, 6),
                CharacterUniformVariantCatalog.Resolve(
                    GetInteger(entry, 11), bodyType, chestSize),
                GetInteger(entry, 16),
                GetInteger(entry, 17),
                GetInteger(entry, 18),
                model is null ? null : GetString(model, 10),
                body is null ? null : GetString(body, 2),
                model is null || model.Values.Count <= 16
                    ? 0u
                    : unchecked((uint)GetInteger(model, 16)),
                model is null ? 0 : GetInteger(model, 5),
                model is null ? 0 : GetInteger(model, 6),
                model is null ? 0 : GetInteger(model, 7),
                model is null ? 0 : GetInteger(model, 8),
                model is null ? 0 : GetInteger(model, 12),
                model is null ? 0 : GetInteger(model, 13),
                chestSize,
                model is null ? 0 : GetInteger(model, 33),
                CharacterOriginGameCatalog.ResolveChoice(
                    Enumerable.Range(CharacterOriginGameCatalog.FirstAssociationIndex,
                            CharacterOriginGameCatalog.LastAssociationIndex - CharacterOriginGameCatalog.FirstAssociationIndex + 1)
                        .Select(index => entry.Values.Count > index ? GetInteger(entry, index) : 0)),
                PhysicalBodyModelKey: CharacterBodyModelCatalog.ResolvePhysicalKey(
                    body is null ? null : GetString(body, 2))));
        }

        var variantsByBase = new Dictionary<int, List<CharacterVariantSummary>>();
        foreach (var entry in parameterEntries.Where(entry => entry.Name == "CHARA_PARAM_INFO" && entry.Values.Count >= 43))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseId = GetInteger(entry, 1);
            if (!bases.ContainsKey(baseId)) continue;
            var skills = new CharacterSkillSlot[9];
            for (var index = 0; index < skills.Length; index++)
                skills[index] = new CharacterSkillSlot(GetInteger(entry, 11 + index * 2), GetInteger(entry, 12 + index * 2));
            var affinityValue = GetInteger(entry, 2);
            var affinity = Enum.IsDefined(typeof(CharacterAffinity), affinityValue)
                ? (CharacterAffinity)affinityValue
                : CharacterAffinity.Unknown;
            var variant = new CharacterVariantSummary(
                GetInteger(entry, 0),
                baseId,
                affinity,
                GetInteger(entry, 3),
                GetInteger(entry, 4),
                GetInteger(entry, 8),
                GetInteger(entry, 7),
                GetInteger(entry, 9),
                GetInteger(entry, 10),
                skills,
                GetInteger(entry, 41),
                new CharacterStatBlock(
                    GetInteger(entry, 29),
                    GetInteger(entry, 30),
                    GetInteger(entry, 31),
                    GetInteger(entry, 32),
                    GetInteger(entry, 33),
                    GetInteger(entry, 34),
                    GetInteger(entry, 35)));
            if (!variantsByBase.TryGetValue(baseId, out var variants))
                variantsByBase.Add(baseId, variants = []);
            variants.Add(variant);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bases.Values
            .Where(characterBase => variantsByBase.ContainsKey(characterBase.BaseId))
            .Select(characterBase => new CharacterSemanticRecord(
                characterBase,
                variantsByBase[characterBase.BaseId].OrderBy(variant => variant.ParameterId).ToArray()))
            .OrderBy(record => record.Base.InternalName, StringComparer.Ordinal)
            .ToArray();
    }

    private static int GetInteger(CfgBinEntry entry, int index) => entry.Values[index].Value switch
    {
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        _ => throw new InvalidDataException($"{entry.Name}[{index}] is not a 32-bit integer."),
    };

    private static string? GetString(CfgBinEntry entry, int index) => entry.Values[index].Value as string;
}

public sealed record CharacterCatalogResult(
    IReadOnlyList<CharacterCatalogItem> Characters,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CharacterSnapshot(
    string Id,
    string DisplayName,
    CharacterDataConfidence Confidence,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyList<CharacterVariantSummary>? Variants = null)
{
    public static CharacterSnapshot Inventory(string id, string displayName) =>
        new(id, displayName, CharacterDataConfidence.InventoryOnly, new Dictionary<string, string?>());
}

public sealed record CharacterDraft(
    string SymbolicId,
    string? SourceCharacterId,
    string DisplayName,
    IReadOnlyDictionary<string, string?> Fields,
    string? StandardPortraitPath = null,
    string? UniformPortraitPath = null,
    CharacterOrigin Origin = CharacterOrigin.Custom,
    CharacterDraftIdentity Identity = default!,
    CharacterDraftGameplay Gameplay = default!,
    CharacterDraftModels Models = default!,
    CharacterDraftAssets Assets = default!,
    CharacterDraftLocalization Localization = default!,
    CharacterDraftAcquisition Acquisition = default!,
    CharacterDraftSkills Skills = default!,
    IReadOnlyList<CharacterDraftDiagnostic> Diagnostics = default!,
    bool IsDirty = true,
    IReadOnlyList<CharacterDraftVariant>? Variants = null);

public sealed record CharacterDraftIdentity(string? DisplayName, string? LocalizedName);

public enum CharacterRegistrationProfile
{
    Standard,
    FunctionalBank,
}

public sealed record CharacterDraftGameplay(
    string Affinity,
    int? MainPosition,
    int? SubPosition,
    CharacterRegistrationProfile RegistrationProfile = CharacterRegistrationProfile.Standard,
    int? PlayStyle = null,
    int? Growth = null,
    int? Rank = null,
    int? AbilityBoardId = null,
    int? SpecialRarity = null);

public sealed record CharacterDraftModels(
    string? HeadModelPath,
    string? BodyModelPath,
    string? SkinColorRgba = null,
    int? UniformModel = null,
    int? ShoesModel = null,
    int? GloveModel = null,
    int? EquipmentColor = null,
    int? UniformCollarOpen = null,
    int? EquipmentFlag2 = null,
    int? ChestSize = null,
    int? ForceKit = null);

public enum CharacterSkillPath
{
    Main,
    Alternate,
}

public sealed record CharacterDraftSkillSlot(
    int Slot,
    CharacterSkillPath Path,
    int? SkillId,
    int? UnlockLevel);

public sealed record CharacterDraftSkills(IReadOnlyList<CharacterDraftSkillSlot> Slots)
{
    public static CharacterDraftSkills Empty { get; } = new(
        Enumerable.Range(1, 9)
            .Select(slot => new CharacterDraftSkillSlot(
                slot,
                slot <= CharacterVariantSummary.MainSkillSlotCount
                    ? CharacterSkillPath.Main
                    : CharacterSkillPath.Alternate,
                null,
                null))
            .ToArray());

    public static CharacterDraftSkills FromLegacyFields(IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var slots = Empty.Slots.ToArray();
        for (var index = 0; index < slots.Length; index++)
        {
            if (!fields.TryGetValue($"Skills.Slot{index + 1}", out var value)
                || string.IsNullOrWhiteSpace(value))
                continue;
            var parts = value.Split(':', 2, StringSplitOptions.None);
            slots[index] = slots[index] with
            {
                SkillId = ParseNullableInt(parts.ElementAtOrDefault(0)),
                UnlockLevel = ParseNullableInt(parts.ElementAtOrDefault(1)),
            };
        }
        return new CharacterDraftSkills(slots);
    }

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

public sealed record CharacterDraftVariant(
    int SourceParameterId,
    CharacterDraftGameplay Gameplay,
    CharacterDraftSkills Skills);

/// <summary>
/// Variant helpers for character parameter rows. The dump does not guarantee
/// that every character has every rarity, so authored/source variants must be
/// preserved exactly rather than synthesized globally.
/// </summary>
public static class CharacterRarityCatalog
{
    public static IReadOnlyList<CharacterDraftVariant>? EnsureDraftVariants(
        IReadOnlyList<CharacterDraftVariant>? variants)
        => variants;

    /// <summary>
    /// Keeps the delivered (rarity 0) parameter row in the primary slot while
    /// retaining every authored variant and its relative order.
    /// </summary>
    public static IReadOnlyList<CharacterDraftVariant>? OrderForRuntime(
        IReadOnlyList<CharacterDraftVariant>? variants)
    {
        if (variants is not { Count: > 1 }) return variants;
        var primaryIndex = -1;
        for (var index = 0; index < variants.Count; index++)
        {
            if (variants[index].Gameplay.SpecialRarity == 0)
            {
                primaryIndex = index;
                break;
            }
        }

        if (primaryIndex <= 0) return variants;
        var ordered = new List<CharacterDraftVariant>(variants.Count) { variants[primaryIndex] };
        for (var index = 0; index < variants.Count; index++)
        {
            if (index != primaryIndex) ordered.Add(variants[index]);
        }
        return ordered;
    }
}

public enum UniformPortraitFallback
{
    Transparent,
    DuplicateStandard,
}

public sealed record CharacterDraftAssets(
    string? StandardPortraitPath,
    string? UniformPortraitPath,
    UniformPortraitFallback UniformFallback = UniformPortraitFallback.Transparent);

public sealed record CharacterDraftLocalizedText(
    string? LocalizedName,
    string? Description,
    string? RomanizedName,
    string? JapaneseName,
    string? ShortName = null,
    string? UpperName = null);

public sealed record CharacterDraftLocalization(
    string? LocalizedName,
    string? RomanizedName,
    IReadOnlyDictionary<string, CharacterDraftLocalizedText>? LocaleValues = null)
{
    public IReadOnlyDictionary<string, CharacterDraftLocalizedText> Locales =>
        LocaleValues ?? GameLocaleCatalog.CreateEmptyLocalizations();
}

public static class GameLocaleCatalog
{
    public static IReadOnlyList<string> SupportedCharacterLocales { get; } =
        ["de", "en", "es", "fr", "it", "ja", "pt", "zh_hans", "zh_hant"];

    public static IReadOnlyDictionary<string, CharacterDraftLocalizedText> CreateEmptyLocalizations() =>
        SupportedCharacterLocales.ToDictionary(
            locale => locale,
            _ => new CharacterDraftLocalizedText(null, null, null, null),
            StringComparer.OrdinalIgnoreCase);
}

public sealed record CharacterDraftAcquisition(string? Method, string? Source);

public sealed record CharacterDraftDiagnostic(string Field, string Code, string Message);

public sealed record CharacterFieldChange(
    string Field,
    string? OriginalValue,
    string? DraftValue);

public sealed record CharacterComparison(IReadOnlyList<CharacterFieldChange> Changes)
{
    public static CharacterComparison Create(CharacterSnapshot source, CharacterDraft draft)
    {
        var changes = new List<CharacterFieldChange>();
        if (!string.Equals(source.DisplayName, draft.DisplayName, StringComparison.Ordinal))
        {
            changes.Add(new CharacterFieldChange("DisplayName", source.DisplayName, draft.DisplayName));
        }

        foreach (var field in source.Fields.Keys.Union(draft.Fields.Keys, StringComparer.Ordinal).Order())
        {
            source.Fields.TryGetValue(field, out var originalValue);
            draft.Fields.TryGetValue(field, out var draftValue);
            if (!string.Equals(originalValue, draftValue, StringComparison.Ordinal))
            {
                changes.Add(new CharacterFieldChange(field, originalValue, draftValue));
            }
        }

        return new CharacterComparison(changes);
    }
}
