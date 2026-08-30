using System.Globalization;
using System.Text.RegularExpressions;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Projects;
using VictoryTool.Application.Profiles;
using VictoryTool.Application.Workspaces;
using VictoryTool.Application.Assets;
using VictoryTool.G4.Textures;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Characters;

public interface IGameDumpIndexService
{
    Task<CharacterCatalogResult> IndexAsync(GameDumpProfile profile, CancellationToken cancellationToken);

    Task<CharacterCatalogResult> IndexAsync(
        GameDumpProfile profile,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken) => IndexAsync(profile, cancellationToken);
}

public interface ICharacterCatalogService : IGameDumpIndexService;

public interface ICharacterCloneService
{
    CharacterDraft Clone(CharacterSnapshot source);
    CharacterDraft Clone(CharacterDraft source);
}

public interface ICharacterDraftService
{
    CharacterDraft CreateBlank();
    CharacterDraft Duplicate(CharacterSnapshot source);
    CharacterDraft Duplicate(CharacterDraft source);
    CharacterDraft Update(CharacterDraft draft, string field, string? value);
    IReadOnlyList<CharacterDraftDiagnostic> Validate(CharacterDraft draft);
    void RemoveFromProject(ModProjectDocument project, Guid draftId);
}

public sealed partial class FileSystemCharacterCatalogService : ICharacterCatalogService
{
    public Task<CharacterCatalogResult> IndexAsync(
        GameDumpProfile profile,
        CancellationToken cancellationToken) => IndexAsync(profile, progress: null, cancellationToken);

    public Task<CharacterCatalogResult> IndexAsync(
        GameDumpProfile profile,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(
            () => Index(profile, progress, cancellationToken),
            cancellationToken);

    private static CharacterCatalogResult Index(
        GameDumpProfile profile,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var characters = new Dictionary<string, CharacterCatalogItem>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CharacterSemanticRecord> semanticRecords = [];
        var rejectedCharacterTables = 0;
        var rejectedLocalizationFiles = 0;
        var localizedCharacterCount = 0;
        var localizationReported = false;
        var taxonomy = GameTaxonomyIndex.Empty;
        string? taxonomyError = null;
        var textReferences = CharacterTextReferenceIndex.Empty;
        string? textReferenceError = null;
        var characterDataRoot = Path.Combine(profile.GameDataPath, "character");
        progress?.Report(new IndexProgress(IndexStage.CfgBinIndexing, 0, 1, "Reading character CFGBIN tables."));
        if (Directory.Exists(characterDataRoot))
        {
            var baseDocument = ReadLargestTable(
                characterDataRoot, "chara_base_*.cfg.bin", "CHARA_BASE_INFO", cancellationToken, out var rejectedBaseTables);
            var parameterDocument = ReadLargestTable(
                characterDataRoot, "chara_param_*.cfg.bin", "CHARA_PARAM_INFO", cancellationToken, out var rejectedParameterTables);
            var modelDocument = ReadLargestTable(
                characterDataRoot, "chara_model_*.cfg.bin", "CHARA_MODEL_INFO", cancellationToken, out var rejectedModelTables);
            rejectedCharacterTables = rejectedBaseTables + rejectedParameterTables + rejectedModelTables;
            if (baseDocument is not null && parameterDocument is not null)
            {
                semanticRecords = CharacterSemanticMapper.Map(
                    baseDocument.Entries,
                    parameterDocument.Entries,
                    modelDocument?.Entries ?? [],
                    cancellationToken);
                progress?.Report(new IndexProgress(IndexStage.Localization, 0, 1, "Reading character localization tables."));
                localizationReported = true;
                var localizationResult = ReadLocalizations(
                    profile.RootPath,
                    semanticRecords.Select(record => record.Base),
                    cancellationToken);
                var romanizationResult = ReadRomanizedNames(
                    profile.RootPath,
                    semanticRecords.Select(record => record.Base),
                    cancellationToken);
                try
                {
                    taxonomy = GameTaxonomyLoader.Load(profile.RootPath, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    taxonomyError = exception.Message;
                }
                var localizations = localizationResult.Localizations;
                try
                {
                    textReferences = CharacterTextReferenceIndex.Load(
                        profile.RootPath,
                        semanticRecords.Select(record => record.Base),
                        localizations,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    textReferenceError = exception.Message;
                }
                rejectedLocalizationFiles = localizationResult.RejectedFileCount + romanizationResult.RejectedFileCount;
                localizedCharacterCount = localizations.Count(pair =>
                    pair.Value.Values.Any(localization => !string.IsNullOrWhiteSpace(localization.FullName)));
                foreach (var record in semanticRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var primaryVariant = record.Variants.FirstOrDefault();
                    localizations.TryGetValue(record.Base.BaseId, out var characterLocalizations);
                    romanizationResult.Names.TryGetValue(record.Base.BaseId, out var romanizedNames);
                    var displayName = GetPreferredDisplayName(characterLocalizations) ?? record.Base.InternalName;
                    if (characters.TryGetValue(record.Base.InternalName, out var existing))
                    {
                        characters[record.Base.InternalName] = existing with
                        {
                            DisplayName = displayName,
                            Confidence = CharacterDataConfidence.Parsed,
                            Affinity = primaryVariant?.Affinity ?? CharacterAffinity.Unknown,
                            Series = taxonomy.ResolveSeries(unchecked((uint)record.Base.SourceSeries), "en"),
                            Position = primaryVariant is null
                                ? "Unknown"
                                : CharacterPositionCatalog.ResolveName(primaryVariant.MainPosition),
                            BaseMetadata = record.Base,
                            Variants = record.Variants,
                            Localizations = characterLocalizations,
                            RomanizedNames = romanizedNames,
                            Taxonomy = taxonomy,
                            TextReferences = textReferences,
                        };
                    }
                    else
                    {
                        characters.Add(record.Base.InternalName, new CharacterCatalogItem(
                            record.Base.InternalName,
                            displayName,
                            CharacterDataConfidence.Parsed,
                            null,
                            primaryVariant?.Affinity ?? CharacterAffinity.Unknown,
                            taxonomy.ResolveSeries(unchecked((uint)record.Base.SourceSeries), "en"),
                            primaryVariant is null
                                ? "Unknown"
                                : CharacterPositionCatalog.ResolveName(primaryVariant.MainPosition),
                            BaseMetadata: record.Base,
                            Variants: record.Variants,
                            Localizations: characterLocalizations,
                            RomanizedNames: romanizedNames,
                            Taxonomy: taxonomy,
                            TextReferences: textReferences));
                    }
                }
            }
        }

        if (!localizationReported)
            progress?.Report(new IndexProgress(IndexStage.Localization, 0, 1, "Reading character localization tables."));
        progress?.Report(new IndexProgress(IndexStage.Assets, 0, 1, "Reading character portrait containers."));
        var paths = new[]
        {
            (CharacterAssetPlatform.Pc, Path.Combine(
                profile.RootPath, "dx11", "menu", "200_icon", "10_icon_chr", "face")),
            (CharacterAssetPlatform.Switch, Path.Combine(
                profile.RootPath, "nx", "menu", "200_icon", "10_icon_chr", "face")),
        };
        var invalidPortraitCount = 0;
        var validPortraitCount = 0;
        foreach (var (platform, directory) in paths.Where(path => Directory.Exists(path.Item2)))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*_l.g4tx", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = PortraitFileName().Match(Path.GetFileName(path));
                if (!match.Success) continue;

                var id = match.Groups["id"].Value;
                CharacterPortraitMetadata? portraitMetadata = null;
                try
                {
                    var container = G4TxDocument.Read(ReadAllBytes(path, cancellationToken));
                    var firstTexture = container.Textures.FirstOrDefault();
                    if (firstTexture is not null)
                    {
                        portraitMetadata = new CharacterPortraitMetadata(
                            container.TextureCount,
                            firstTexture.Width,
                            firstTexture.Height,
                            firstTexture.PayloadKind switch
                            {
                                G4TexturePayloadKind.Dds => "DDS",
                                G4TexturePayloadKind.NxTexture => "NXTCH",
                                _ => "Unknown",
                            },
                            container.TextureCount >= 1,
                            container.TextureCount >= 2,
                            container.Textures.ElementAtOrDefault(0)?.Name,
                            container.Textures.ElementAtOrDefault(1)?.Name);
                        validPortraitCount++;
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    invalidPortraitCount++;
                }

                characters.TryGetValue(id, out var existingCharacter);
                var portraits = existingCharacter?.Portraits?.ToDictionary() ??
                    new Dictionary<CharacterAssetPlatform, CharacterPortraitAsset>();
                portraits[platform] = new CharacterPortraitAsset(Path.GetFullPath(path), portraitMetadata);
                var preferredPortrait = portraits.GetValueOrDefault(CharacterAssetPlatform.Pc)
                    ?? portraits.GetValueOrDefault(CharacterAssetPlatform.Switch);
                characters[id] = existingCharacter is null
                    ? new CharacterCatalogItem(
                        id,
                        id,
                        CharacterDataConfidence.InventoryOnly,
                        preferredPortrait?.ResourcePath,
                        PortraitMetadata: preferredPortrait?.Metadata,
                        Portraits: portraits,
                        AssetAvailability: CharacterAssetAvailability.FromPortraits(portraits))
                    : existingCharacter with
                    {
                        PortraitResourcePath = preferredPortrait?.ResourcePath,
                        PortraitMetadata = preferredPortrait?.Metadata,
                        Portraits = portraits,
                        AssetAvailability = CharacterAssetAvailability.FromPortraits(portraits),
                    };
            }
        }

        ApplyUniformPreviewAssets(profile, semanticRecords, characters, taxonomy, cancellationToken);

        var diagnostics = new List<Diagnostic>();
        if (validPortraitCount == 0)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.portraits_missing",
                DiagnosticSeverity.Warning,
                "No character portrait resources were found for PC or Switch.",
                "Verify that the selected dump includes platform menu resources."));
        }

        if (invalidPortraitCount != 0)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.portraits_unreadable",
                DiagnosticSeverity.Warning,
                $"{invalidPortraitCount} portrait containers could not be parsed as G4TX.",
                "Inspect the affected platform assets or game version before exporting them."));
        }

        if (semanticRecords.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.character_tables_missing",
                DiagnosticSeverity.Warning,
                "No compatible CHARA_BASE_INFO and CHARA_PARAM_INFO tables were parsed.",
                "Select the extracted data folder that contains common/gamedata and check the game version."));
        }

        if (rejectedCharacterTables != 0)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.character_tables_rejected",
                DiagnosticSeverity.Warning,
                $"{rejectedCharacterTables} character CFGBIN files were incompatible or unreadable.",
                "Check that common/gamedata contains the character tables from the same game dump version."));
        }

        if (rejectedLocalizationFiles != 0)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.localization_files_rejected",
                DiagnosticSeverity.Warning,
                $"{rejectedLocalizationFiles} character localization files were incompatible or unreadable.",
                "The catalog retained any name or description table that could be parsed independently."));
        }

        if (taxonomyError is not null)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.taxonomy_unreadable",
                DiagnosticSeverity.Warning,
                $"Character taxonomy tables could not be resolved: {taxonomyError}",
                "Series, academic-year and skill IDs remain available in Advanced."));
        }

        if (textReferenceError is not null)
        {
            diagnostics.Add(new Diagnostic(
                "catalog.character_name_tags_unreadable",
                DiagnosticSeverity.Warning,
                $"Character name-tag references could not be resolved: {textReferenceError}",
                "Descriptions retain unresolved <FST>, <LST> and <FUL> tokens."));
        }

        diagnostics.Add(new Diagnostic(
            "catalog.indexed",
            DiagnosticSeverity.Information,
            $"Indexed {characters.Count} character records, {semanticRecords.Count} semantic records, " +
            $"{localizedCharacterCount} localized records and {validPortraitCount} portrait containers."));

        progress?.Report(new IndexProgress(IndexStage.Completed, 1, 1, "Character indexing completed."));
        return new CharacterCatalogResult(
            characters.Values.OrderBy(character => character.Id, StringComparer.Ordinal).ToArray(),
            diagnostics);
    }

    private static void ApplyUniformPreviewAssets(
        GameDumpProfile profile,
        IReadOnlyList<CharacterSemanticRecord> semanticRecords,
        IDictionary<string, CharacterCatalogItem> characters,
        GameTaxonomyIndex taxonomy,
        CancellationToken cancellationToken)
    {
        var exactAssets = new Dictionary<string, UniformAssetDescriptor>(StringComparer.OrdinalIgnoreCase);
        var platforms = profile.HasPcResources ? new[] { "dx11" } : new[] { "nx" };
        foreach (var record in semanticRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.Base.InternalName.StartsWith('c')) continue;
            var id = record.Base.InternalName[1..];
            foreach (var platform in platforms)
            {
                var directory = Path.Combine(
                    profile.RootPath, platform, "menu", "200_icon", "10_icon_chr", "uniform");
                foreach (var fileName in new[] { $"u{id}_l.g4tx", $"u{id}_00_l.g4tx", $"u{id}_10_00_l.g4tx" })
                {
                    var path = Path.Combine(directory, fileName);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var document = G4TxDocument.Read(ReadAllBytes(path, cancellationToken));
                        var shirt = document.Textures.FirstOrDefault(texture => texture.Name.EndsWith("_1", StringComparison.Ordinal));
                        var mask = document.Textures.FirstOrDefault(texture => texture.Name.EndsWith("_2", StringComparison.Ordinal));
                        if (shirt is null || mask is null) continue;
                        exactAssets[record.Base.InternalName] = new UniformAssetDescriptor(
                            Path.GetRelativePath(profile.RootPath, path), shirt.Name, mask.Name);
                        break;
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
                if (exactAssets.ContainsKey(record.Base.InternalName)) break;
            }
        }

        var teams = taxonomy.GetTeams("en").ToDictionary(team => team.Id);
        var uniforms = taxonomy.GetEquipment("en", EquipmentCategory.Uniform)
            .Where(item => item.ResourceKey.EndsWith("_10", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var modelCatalog = DefaultUniformCatalog.Load(profile, cancellationToken);
        var assetCache = new Dictionary<(string ResourceKey, string Suffix), UniformAssetDescriptor?>(
            new UniformAssetCacheKeyComparer());

        foreach (var record in semanticRecords)
        {
            if (!characters.TryGetValue(record.Base.InternalName, out var character)) continue;
            var asset = exactAssets.GetValueOrDefault(record.Base.InternalName);
            if (asset is null)
            {
                asset = ResolveTeamUniformAsset(
                    profile,
                    record.Base,
                    teams,
                    uniforms,
                    modelCatalog,
                    assetCache,
                    record.Variants.FirstOrDefault()?.MainPosition == 1,
                    cancellationToken);
            }
            if (asset is null
                && record.Base.UniformModel != 0
                && modelCatalog.TryResolveModel(
                    record.Base.UniformModel,
                    record.Base.Gender,
                    record.Base.UniformPortraitVariant,
                    out var modelAsset))
            {
                asset = modelAsset;
            }
            if (asset is not null)
                characters[record.Base.InternalName] = character with { UniformPreviewAsset = asset };
        }
    }

    private static UniformAssetDescriptor? ResolveTeamUniformAsset(
        GameDumpProfile profile,
        CharacterBaseMetadata character,
        IReadOnlyDictionary<int, LocalizedTeam> teams,
        IReadOnlyList<LocalizedEquipment> uniforms,
        DefaultUniformCatalog modelCatalog,
        IDictionary<(string ResourceKey, string Suffix), UniformAssetDescriptor?> assetCache,
        bool goalkeeper,
        CancellationToken cancellationToken)
    {
        var variant = character.UniformPortraitVariant;
        var suffixes = variant switch
        {
            0 => new[] { "00" },
            1 => new[] { "02", "00" },
            2 => new[] { "03", "00" },
            3 => new[] { "01", "00" },
            _ => Array.Empty<string>(),
        };
        if (suffixes.Length == 0) return null;
        var preferredFamilyPrefix = character.InternalName.Length >= 4
            ? $"u{character.InternalName[1..4]}"
            : null;

        foreach (var teamId in new[]
                 {
                     character.TeamAssociation1,
                     character.TeamAssociation2,
                     character.TeamAssociation3,
                 }.Where(teamId => teamId != 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!teams.TryGetValue(teamId, out var team)) continue;
            foreach (var uniformInfo in EnumerateTeamUniformInfos(team, character.OriginGameAssociationIndex))
            {
                if (uniformInfo == 0) continue;
                if (modelCatalog.TryResolveUniformKit(
                    uniformInfo,
                    character.Gender,
                    variant,
                    goalkeeper,
                    character.UniformModel == 0 ? null : character.UniformModel,
                    out var kitAsset))
                    return kitAsset;
            }

            var equipment = uniforms
                .Where(item => item.Name.Contains(team.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => preferredFamilyPrefix is not null
                    && item.ResourceKey.StartsWith(preferredFamilyPrefix, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.ResourceKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (equipment is null) continue;

            foreach (var suffix in suffixes)
            {
                var cacheKey = (equipment.ResourceKey, suffix);
                if (!assetCache.TryGetValue(cacheKey, out var asset))
                {
                    asset = DefaultUniformCatalog.TryReadAsset(
                        profile,
                        profile.HasPcResources ? "dx11" : "nx",
                        equipment.ResourceKey,
                        suffix,
                        cancellationToken,
                        out var loadedAsset)
                        ? loadedAsset
                        : null;
                    assetCache[cacheKey] = asset;
                }
                if (asset is not null)
                    return asset;
            }
        }
        return null;
    }

    private static IEnumerable<uint> EnumerateTeamUniformInfos(
        LocalizedTeam team,
        int? originGameAssociationIndex)
    {
        var preferred = originGameAssociationIndex switch
        {
            >= 22 and <= 24 => team.TeamKitIe,
            >= 25 and <= 27 => team.TeamKitGo,
            28 or 29 => team.TeamKitAreOri,
            30 => team.TeamKitV,
            _ => 0u,
        };
        if (preferred != 0) yield return preferred;
        foreach (var candidate in new[] { team.TeamKitIe, team.TeamKitGo, team.TeamKitAreOri, team.TeamKitV })
        {
            if (candidate != 0 && candidate != preferred) yield return candidate;
        }
    }

    private sealed class UniformAssetCacheKeyComparer : IEqualityComparer<(string ResourceKey, string Suffix)>
    {
        public bool Equals(
            (string ResourceKey, string Suffix) left,
            (string ResourceKey, string Suffix) right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.ResourceKey, right.ResourceKey)
            && StringComparer.Ordinal.Equals(left.Suffix, right.Suffix);

        public int GetHashCode((string ResourceKey, string Suffix) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.ResourceKey),
            StringComparer.Ordinal.GetHashCode(value.Suffix));
    }

    private static CfgBinDocument? ReadLargestTable(
        string directory,
        string pattern,
        string tableName,
        CancellationToken cancellationToken,
        out int rejectedFileCount)
    {
        CfgBinDocument? selected = null;
        var selectedCount = 0;
        rejectedFileCount = 0;
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = CfgBinDocument.Read(ReadAllBytes(path, cancellationToken));
                var count = document.Entries.Count(entry => entry.Name == tableName);
                if (count <= selectedCount) continue;
                selected = document;
                selectedCount = count;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                rejectedFileCount++;
            }
        }
        return selected;
    }

    private static LocalizationIndexResult ReadLocalizations(
        string rootPath,
        IEnumerable<CharacterBaseMetadata> characters,
        CancellationToken cancellationToken)
    {
        var characterArray = characters.ToArray();
        var textRoot = Path.Combine(rootPath, "common", "text");
        if (characterArray.Length == 0 || !Directory.Exists(textRoot))
            return new LocalizationIndexResult(
                new Dictionary<int, IReadOnlyDictionary<string, CharacterLocalizedText>>(),
                0);

        var result = new Dictionary<int, Dictionary<string, CharacterLocalizedText>>();
        var rejectedFileCount = 0;
        foreach (var localeDirectory in Directory.EnumerateDirectories(textRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locale = Path.GetFileName(localeDirectory);
            var namesPath = Path.Combine(localeDirectory, "chara_text.cfg.bin");
            var descriptionsPath = Path.Combine(localeDirectory, "chara_description_text.cfg.bin");
            if (!File.Exists(namesPath) && !File.Exists(descriptionsPath)) continue;

            IReadOnlyList<CfgBinEntry> nameEntries = [];
            IReadOnlyList<CfgBinEntry> descriptionEntries = [];
            if (File.Exists(namesPath))
            {
                try
                {
                    nameEntries = CfgBinDocument.Read(ReadAllBytes(namesPath, cancellationToken)).Entries;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    rejectedFileCount++;
                }
            }
            if (File.Exists(descriptionsPath))
            {
                try
                {
                    descriptionEntries = CfgBinDocument.Read(ReadAllBytes(descriptionsPath, cancellationToken)).Entries;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    rejectedFileCount++;
                }
            }

            if (nameEntries.Count == 0 && descriptionEntries.Count == 0) continue;
            var mapped = CharacterLocalizationMapper.Map(
                locale,
                characterArray,
                nameEntries,
                descriptionEntries,
                cancellationToken);
            foreach (var (baseId, localization) in mapped)
            {
                if (!result.TryGetValue(baseId, out var localeMap))
                    result.Add(baseId, localeMap = new Dictionary<string, CharacterLocalizedText>(StringComparer.OrdinalIgnoreCase));
                localeMap[locale] = localization;
            }
        }

        return new LocalizationIndexResult(
            result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, CharacterLocalizedText>)pair.Value),
            rejectedFileCount);
    }

    private static RomanizedNameIndexResult ReadRomanizedNames(
        string rootPath,
        IEnumerable<CharacterBaseMetadata> characters,
        CancellationToken cancellationToken)
    {
        var characterArray = characters.ToArray();
        var textRoot = Path.Combine(rootPath, "common", "text");
        if (characterArray.Length == 0 || !Directory.Exists(textRoot))
            return new RomanizedNameIndexResult(
                new Dictionary<int, IReadOnlyDictionary<string, CharacterNameSet>>(),
                0);

        var result = new Dictionary<int, Dictionary<string, CharacterNameSet>>();
        var rejectedFileCount = 0;
        foreach (var localeDirectory in Directory.EnumerateDirectories(textRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(localeDirectory, "chara_text_roma.cfg.bin");
            if (!File.Exists(path)) continue;

            IReadOnlyList<CfgBinEntry> entries;
            try
            {
                entries = CfgBinDocument.Read(ReadAllBytes(path, cancellationToken)).Entries;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                rejectedFileCount++;
                continue;
            }

            var locale = Path.GetFileName(localeDirectory);
            foreach (var (baseId, nameSet) in CharacterRomanizedNameMapper.Map(
                locale,
                characterArray,
                entries,
                cancellationToken))
            {
                if (!result.TryGetValue(baseId, out var localeMap))
                    result.Add(baseId, localeMap = new Dictionary<string, CharacterNameSet>(StringComparer.OrdinalIgnoreCase));
                localeMap[locale] = nameSet;
            }
        }

        return new RomanizedNameIndexResult(
            result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, CharacterNameSet>)pair.Value),
            rejectedFileCount);
    }

    private static string? GetPreferredDisplayName(
        IReadOnlyDictionary<string, CharacterLocalizedText>? localizations)
    {
        if (localizations is null) return null;
        if (localizations.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english.FullName))
            return english.FullName;
        if (localizations.TryGetValue("es", out var spanish) && !string.IsNullOrWhiteSpace(spanish.FullName))
            return spanish.FullName;
        return localizations.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.FullName))?.FullName;
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken).GetAwaiter().GetResult();

    private sealed record LocalizationIndexResult(
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, CharacterLocalizedText>> Localizations,
        int RejectedFileCount);

    private sealed record RomanizedNameIndexResult(
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, CharacterNameSet>> Names,
        int RejectedFileCount);

    [GeneratedRegex("^(?<id>.+)_l\\.g4tx$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PortraitFileName();
}

public sealed class CharacterCloneService : ICharacterCloneService
{
    public CharacterDraft Clone(CharacterSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fields = new Dictionary<string, string?>(source.Fields, StringComparer.Ordinal);
        return new CharacterDraft(
            CreateSymbolicId(),
            source.Id,
            source.DisplayName,
            fields,
            Origin: CharacterOrigin.Custom,
            Identity: new CharacterDraftIdentity(source.DisplayName, null),
            Gameplay: CreateGameplay(fields),
            Models: new CharacterDraftModels(
                source.Fields.GetValueOrDefault("Models.HeadModelPath"),
                source.Fields.GetValueOrDefault("Models.BodyModelPath"),
                source.Fields.GetValueOrDefault("Models.SkinColorRgba"),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.UniformModel")),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.ShoesModel")),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.GloveModel")),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.EquipmentColor")),
                ParseNullableInt(ReadModelField(source.Fields, "Models.UniformCollarOpen", "Models.EquipmentFlag1")),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.EquipmentFlag2")),
                ParseNullableInt(ReadModelField(source.Fields, "Models.ChestSize", "Models.BoobSize")),
                ParseNullableInt(source.Fields.GetValueOrDefault("Models.ForceKit"))),
            Assets: new CharacterDraftAssets(null, null),
            Localization: new CharacterDraftLocalization(
                null,
                null,
                GameLocaleCatalog.SupportedCharacterLocales.ToDictionary(
                    locale => locale,
                    locale => new CharacterDraftLocalizedText(
                        source.Fields.GetValueOrDefault($"Localization.{locale}.FullName"),
                        source.Fields.GetValueOrDefault($"Localization.{locale}.Description"),
                        source.Fields.GetValueOrDefault($"Romanization.{locale}.FullName"),
                        locale == "ja" ? source.Fields.GetValueOrDefault($"Localization.{locale}.FullName") : null,
                        source.Fields.GetValueOrDefault($"Localization.{locale}.ShortName"),
                        source.Fields.GetValueOrDefault($"Localization.{locale}.UpperName")),
                    StringComparer.OrdinalIgnoreCase)),
            Acquisition: new CharacterDraftAcquisition(null, null),
            Skills: CharacterDraftSkills.FromLegacyFields(source.Fields),
            Diagnostics: [],
            Variants: CharacterRarityCatalog.OrderForRuntime(
                CharacterRarityCatalog.EnsureDraftVariants(
                    source.Variants?.Select(ToDraftVariant).ToArray())));
    }

    public CharacterDraft Clone(CharacterDraft source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            SymbolicId = CreateSymbolicId(),
            DisplayName = $"{source.DisplayName} Copy",
            Fields = new Dictionary<string, string?>(source.Fields, StringComparer.Ordinal),
            Assets = source.Assets is { } assets
                ? assets with { }
                : new CharacterDraftAssets(null, null),
            Localization = CloneLocalization(source.Localization),
            Acquisition = source.Acquisition is { } acquisition
                ? acquisition with { }
                : new CharacterDraftAcquisition(null, null),
            Skills = CloneSkills(source.Skills),
            Diagnostics = [],
            Origin = CharacterOrigin.Custom,
            IsDirty = true,
            Variants = source.Variants is null
                ? null
                : source.Variants.Select(CloneVariant).ToArray(),
        };
    }

    private static string CreateSymbolicId() => $"custom.{Guid.NewGuid():N}";

    private static CharacterDraftLocalization CloneLocalization(CharacterDraftLocalization? source)
    {
        if (source is null)
            return new CharacterDraftLocalization(null, null, GameLocaleCatalog.CreateEmptyLocalizations());
        var locales = source.Locales.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { },
            StringComparer.OrdinalIgnoreCase);
        return source with { LocaleValues = locales };
    }

    private static CharacterDraftSkills CloneSkills(CharacterDraftSkills? source) =>
        source is null
            ? CharacterDraftSkills.Empty
            : new CharacterDraftSkills(source.Slots.Select(slot => slot with { }).ToArray());

    private static CharacterDraftVariant CloneVariant(CharacterDraftVariant source) =>
        source with
        {
            Gameplay = source.Gameplay with { },
            Skills = CloneSkills(source.Skills),
        };

    private static CharacterDraftVariant ToDraftVariant(CharacterVariantSummary variant) =>
        new(
            variant.ParameterId,
            new CharacterDraftGameplay(
                variant.Affinity.ToString(),
                variant.MainPosition,
                variant.SubPosition,
                PlayStyle: variant.PlayStyle,
                Growth: variant.Growth,
                Rank: variant.Rank,
                AbilityBoardId: variant.AbilityBoardId,
                SpecialRarity: variant.SpecialRarity),
            new CharacterDraftSkills(variant.SkillSlots
                .Select((skill, index) => new CharacterDraftSkillSlot(
                    index + 1,
                    index < CharacterVariantSummary.MainSkillSlotCount
                        ? CharacterSkillPath.Main
                        : CharacterSkillPath.Alternate,
                    skill.SkillId,
                    skill.UnlockLevel))
                .ToArray()));

    private static CharacterDraftGameplay CreateGameplay(IReadOnlyDictionary<string, string?> fields) => new(
        fields.GetValueOrDefault("Gameplay.Affinity") ?? "Neutral",
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.MainPosition")),
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.SubPosition")),
        Enum.TryParse<CharacterRegistrationProfile>(
            fields.GetValueOrDefault("Gameplay.RegistrationProfile"), out var registrationProfile)
            ? registrationProfile
            : CharacterRegistrationProfile.Standard,
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.PlayStyle")),
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.Growth")),
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.Rank")),
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.AbilityBoardId")),
        ParseNullableInt(fields.GetValueOrDefault("Gameplay.SpecialRarity")));

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? ReadModelField(
        IReadOnlyDictionary<string, string?> fields,
        string canonicalName,
        string legacyName) =>
        fields.GetValueOrDefault(canonicalName) ?? fields.GetValueOrDefault(legacyName);
}

public sealed class CharacterDraftService : ICharacterDraftService
{
    private readonly ICharacterCloneService _cloneService;

    public CharacterDraftService(ICharacterCloneService? cloneService = null)
    {
        _cloneService = cloneService ?? new CharacterCloneService();
    }

    public CharacterDraft CreateBlank()
    {
        var symbolicId = $"custom.{Guid.NewGuid():N}";
        var draft = new CharacterDraft(
            symbolicId,
            null,
            "Untitled character",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Gameplay.Affinity"] = "Neutral",
            },
            Origin: CharacterOrigin.Custom,
            Identity: new CharacterDraftIdentity(null, null),
            Gameplay: new CharacterDraftGameplay("Neutral", null, null),
            Models: new CharacterDraftModels(null, null),
            Assets: new CharacterDraftAssets(null, null),
            Localization: new CharacterDraftLocalization(
                null, null, GameLocaleCatalog.CreateEmptyLocalizations()),
            Acquisition: new CharacterDraftAcquisition(null, null),
            Skills: CharacterDraftSkills.Empty,
            Diagnostics: []);
        return draft with { Diagnostics = Validate(draft) };
    }

    public CharacterDraft Duplicate(CharacterSnapshot source) => _cloneService.Clone(source);

    public CharacterDraft Duplicate(CharacterDraft source) => _cloneService.Clone(source);

    public CharacterDraft Update(CharacterDraft draft, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var fields = new Dictionary<string, string?>(draft.Fields, StringComparer.Ordinal)
        {
            [field] = value,
        };
        var updated = draft with { Fields = fields, IsDirty = true };
        updated = field switch
        {
            "Identity.DisplayName" => updated with
            {
                DisplayName = value ?? string.Empty,
                Identity = (draft.Identity ?? new CharacterDraftIdentity(null, null)) with { DisplayName = value },
            },
            "Identity.LocalizedName" => updated with
            {
                Identity = (draft.Identity ?? new CharacterDraftIdentity(null, null)) with { LocalizedName = value },
            },
            "Gameplay.Affinity" => updated with
            {
                Gameplay = (draft.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null)) with
                {
                    Affinity = value ?? string.Empty,
                },
            },
            "Gameplay.MainPosition" => updated with
            {
                Gameplay = (draft.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null)) with
                {
                    MainPosition = ParseNullableInt(value),
                },
            },
            "Gameplay.SubPosition" => updated with
            {
                Gameplay = (draft.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null)) with
                {
                    SubPosition = ParseNullableInt(value),
                },
            },
            "Gameplay.RegistrationProfile" => updated with
            {
                Gameplay = (draft.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null)) with
                {
                    RegistrationProfile = Enum.TryParse<CharacterRegistrationProfile>(value, out var profile)
                        ? profile
                        : CharacterRegistrationProfile.Standard,
                },
            },
            "Gameplay.PlayStyle" => UpdateGameplay(updated, draft, gameplay => gameplay with { PlayStyle = ParseNullableInt(value) }),
            "Gameplay.Growth" => UpdateGameplay(updated, draft, gameplay => gameplay with { Growth = ParseNullableInt(value) }),
            "Gameplay.Rank" => UpdateGameplay(updated, draft, gameplay => gameplay with { Rank = ParseNullableInt(value) }),
            "Gameplay.AbilityBoardId" => UpdateGameplay(updated, draft, gameplay => gameplay with { AbilityBoardId = ParseNullableInt(value) }),
            "Gameplay.SpecialRarity" => UpdateGameplay(updated, draft, gameplay => gameplay with { SpecialRarity = ParseNullableInt(value) }),
            "Models.HeadModelPath" => updated with
            {
                Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { HeadModelPath = value },
            },
            "Models.BodyModelPath" => updated with
            {
                Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { BodyModelPath = value },
            },
            "Models.SkinColorRgba" => updated with
            {
                Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { SkinColorRgba = value },
            },
            "Models.UniformModel" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { UniformModel = ParseNullableInt(value) } },
            "Models.ShoesModel" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { ShoesModel = ParseNullableInt(value) } },
            "Models.GloveModel" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { GloveModel = ParseNullableInt(value) } },
            "Models.EquipmentColor" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { EquipmentColor = ParseNullableInt(value) } },
            "Models.UniformCollarOpen" or "Models.EquipmentFlag1" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { UniformCollarOpen = ParseNullableInt(value) } },
            "Models.EquipmentFlag2" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { EquipmentFlag2 = ParseNullableInt(value) } },
            "Models.ChestSize" or "Models.BoobSize" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { ChestSize = ParseNullableInt(value) } },
            "Models.ForceKit" => updated with { Models = (draft.Models ?? new CharacterDraftModels(null, null)) with { ForceKit = ParseNullableInt(value) } },
            "Assets.StandardPortraitPath" => updated with
            {
                Assets = (draft.Assets ?? new CharacterDraftAssets(null, null)) with { StandardPortraitPath = value },
            },
            "Assets.UniformPortraitPath" => updated with
            {
                Assets = (draft.Assets ?? new CharacterDraftAssets(null, null)) with { UniformPortraitPath = value },
            },
            "Assets.UniformFallback" => updated with
            {
                Assets = (draft.Assets ?? new CharacterDraftAssets(null, null)) with
                {
                    UniformFallback = Enum.TryParse<UniformPortraitFallback>(value, out var fallback)
                        ? fallback
                        : UniformPortraitFallback.Transparent,
                },
            },
            "Localization.LocalizedName" => updated with
            {
                Localization = (draft.Localization ?? new CharacterDraftLocalization(null, null)) with { LocalizedName = value },
            },
            "Localization.RomanizedName" => updated with
            {
                Localization = (draft.Localization ?? new CharacterDraftLocalization(null, null)) with { RomanizedName = value },
            },
            "Acquisition.Method" => updated with
            {
                Acquisition = (draft.Acquisition ?? new CharacterDraftAcquisition(null, null)) with { Method = value },
            },
            "Acquisition.Source" => updated with
            {
                Acquisition = (draft.Acquisition ?? new CharacterDraftAcquisition(null, null)) with { Source = value },
            },
            _ => updated,
        };
        if (TryParseLocalizationField(field, out var locale, out var localizationField))
        {
            var localization = draft.Localization ?? new CharacterDraftLocalization(
                null, null, GameLocaleCatalog.CreateEmptyLocalizations());
            var locales = new Dictionary<string, CharacterDraftLocalizedText>(
                localization.Locales, StringComparer.OrdinalIgnoreCase);
            locales.TryGetValue(locale, out var existing);
            existing ??= new CharacterDraftLocalizedText(null, null, null, null);
            locales[locale] = localizationField switch
            {
                "LocalizedName" => existing with { LocalizedName = value },
                "Description" => existing with { Description = value },
                "RomanizedName" => existing with { RomanizedName = value },
                "JapaneseName" => existing with { JapaneseName = value },
                "ShortName" => existing with { ShortName = value },
                "UpperName" => existing with { UpperName = value },
                _ => existing,
            };
            updated = updated with { Localization = localization with { LocaleValues = locales } };
            if (localizationField == "LocalizedName")
            {
                updated = updated with
                {
                    DisplayName = value ?? string.Empty,
                    Identity = (updated.Identity ?? new CharacterDraftIdentity(null, null)) with { DisplayName = value },
                };
            }
        }
        if (TryParseSkillField(field, out var slotNumber, out var skillField))
        {
            var skills = draft.Skills ?? CharacterDraftSkills.Empty;
            var slots = skills.Slots.ToArray();
            var slotIndex = slotNumber - 1;
            var slot = slots[slotIndex];
            slots[slotIndex] = skillField switch
            {
                "SkillId" => slot with { SkillId = ParseNullableInt(value) },
                "UnlockLevel" => slot with { UnlockLevel = ParseNullableInt(value) },
                _ => slot,
            };
            var updatedSlot = slots[slotIndex];
            fields[$"Skills.Slot{slotNumber}"] =
                $"{FormatNullableInt(updatedSlot.SkillId)}:{FormatNullableInt(updatedSlot.UnlockLevel)}";
            updated = updated with { Fields = fields, Skills = new CharacterDraftSkills(slots) };
        }
        return updated with { Diagnostics = Validate(updated) };
    }

    public IReadOnlyList<CharacterDraftDiagnostic> Validate(CharacterDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var diagnostics = new List<CharacterDraftDiagnostic>();
        if (string.IsNullOrWhiteSpace(draft.Identity?.DisplayName)
            && string.IsNullOrWhiteSpace(draft.DisplayName)
            || string.Equals(draft.DisplayName, "Untitled character", StringComparison.Ordinal))
        {
            diagnostics.Add(new CharacterDraftDiagnostic(
                "Identity.DisplayName",
                "draft.display_name_required",
                "A custom character needs a display name before export."));
        }

        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.MainPosition");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.SubPosition");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.PlayStyle");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.Growth");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.Rank");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.AbilityBoardId");
        AddIntegerDiagnostic(draft, diagnostics, "Gameplay.SpecialRarity");
        AddModelPathDiagnostic(draft, diagnostics, "Models.HeadModelPath");
        AddModelPathDiagnostic(draft, diagnostics, "Models.BodyModelPath");
        foreach (var slot in Enumerable.Range(1, 9))
        {
            AddIntegerDiagnostic(draft, diagnostics, $"Skills.Slot{slot}.SkillId");
            AddIntegerDiagnostic(draft, diagnostics, $"Skills.Slot{slot}.UnlockLevel");
        }

        return diagnostics;
    }

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static CharacterDraft UpdateGameplay(
        CharacterDraft updated,
        CharacterDraft source,
        Func<CharacterDraftGameplay, CharacterDraftGameplay> update) =>
        updated with { Gameplay = update(source.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null)) };

    private static string FormatNullableInt(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryParseSkillField(string field, out int slot, out string skillField)
    {
        slot = 0;
        skillField = string.Empty;
        var parts = field.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], "Skills", StringComparison.Ordinal)
            || !parts[1].StartsWith("Slot", StringComparison.Ordinal)
            || !int.TryParse(parts[1].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out slot)
            || slot is < 1 or > 9
            || parts[2] is not ("SkillId" or "UnlockLevel"))
            return false;
        skillField = parts[2];
        return true;
    }

    private static bool TryParseLocalizationField(
        string field,
        out string locale,
        out string localizationField)
    {
        locale = string.Empty;
        localizationField = string.Empty;
        var parts = field.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], "Localization", StringComparison.Ordinal)
            || !GameLocaleCatalog.SupportedCharacterLocales.Contains(parts[1], StringComparer.OrdinalIgnoreCase)
            || parts[2] is not ("LocalizedName" or "Description" or "RomanizedName" or "JapaneseName" or "ShortName" or "UpperName"))
            return false;
        locale = parts[1];
        localizationField = parts[2];
        return true;
    }

    private static void AddIntegerDiagnostic(
        CharacterDraft draft,
        ICollection<CharacterDraftDiagnostic> diagnostics,
        string field)
    {
        if (!draft.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) return;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return;
        diagnostics.Add(new CharacterDraftDiagnostic(
            field,
            "draft.integer_required",
            "Enter a whole-number game value."));
    }

    private static void AddModelPathDiagnostic(
        CharacterDraft draft,
        ICollection<CharacterDraftDiagnostic> diagnostics,
        string field)
    {
        if (!draft.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) return;
        var extension = Path.GetExtension(value);
        if (string.Equals(extension, ".g4md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".g4pkm", StringComparison.OrdinalIgnoreCase)) return;
        diagnostics.Add(new CharacterDraftDiagnostic(
            field,
            "draft.model_extension_unsupported",
            "Model files must use the .g4md or .g4pkm extension."));
    }

    public void RemoveFromProject(ModProjectDocument project, Guid draftId)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.RemoveDraft(draftId);
    }
}
