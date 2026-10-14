using VictoryTool.Application.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Packages;
using VictoryTool.Application.Profiles;
using VictoryTool.Application.Projects;
using VictoryTool.CfgBin;
using VictoryTool.G4.Textures;
using VictoryTool.G4.Animations;

namespace VictoryTool.Application.Exporting;

public enum ExportPlatform
{
    Pc,
    Switch,
}

public enum AcquisitionMode
{
    Shop,
    Delivery,
    Constellation,
    Both,
}

public sealed record AnimationGenerationSelection(bool Enabled, bool Chr, bool Event);

public sealed record ExportPlan(
    ExportPlatform Platform,
    AcquisitionMode Acquisition,
    string OutputPath,
    int EnabledPackageCount,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ExportIdAssignment> AssignedIds)
{
    public IReadOnlyList<ExportPackagePlan> Packages { get; init; } = [];
    public IReadOnlyList<ExportResourceOperation> ResourceOperations { get; init; } = [];
    public IReadOnlyList<ExportGameReferenceOperation> GameReferenceOperations { get; init; } = [];
    public IReadOnlyList<ExportPatchOperation> PatchOperations { get; init; } = [];
    public IReadOnlyList<ExportLocalizationOperation> LocalizationOperations { get; init; } = [];
    public IReadOnlyList<ExportFileOperation> FileOperations { get; init; } = [];
    public IReadOnlyList<ExportModelDependencyOperation> ModelDependencyOperations { get; init; } = [];
    public IReadOnlyList<ExportCharacterCoreOperation> CharacterCoreOperations { get; init; } = [];
    public IReadOnlyList<ExportCharacterModelOperation> CharacterModelOperations { get; init; } = [];
    public IReadOnlyList<ExportCharacterBodyOperation> CharacterBodyOperations { get; init; } = [];
    public IReadOnlyList<ExportCharacterClothesOperation> CharacterClothesOperations { get; init; } = [];
    public IReadOnlyList<ExportShopCharacterOperation> ShopCharacterOperations { get; init; } = [];
    public IReadOnlyList<ExportCharacterDeliveryOperation> CharacterDeliveryOperations { get; init; } = [];
    public IReadOnlyList<ExportDeliveryLocalizationOperation> DeliveryLocalizationOperations { get; init; } = [];
    public IReadOnlyList<ExportPortraitOperation> PortraitOperations { get; init; } = [];
    public IReadOnlyList<AnimationGenerationRequest> AnimationGenerationRequests { get; init; } = [];
    public bool CanExport => Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

public sealed record ExportPackagePlan(Guid BatchEntryId, string PackagePath, string SymbolicId);

public sealed record AnimationBankSource(string Path, string Type, string ResourcePath, bool IsPrimaryFamily)
{
    public bool IsEvent { get; init; }
    public string? SourceIdentifier { get; init; }
    public string? DestinationResourcePath { get; init; }
    public string? GeneratedResourcePath { get; init; }
    public string? GeneratedChecksum { get; init; }
    public string? GeneratedPackageResourcePath { get; init; }
    public string? GeneratedPackageChecksum { get; init; }
}

public sealed record AnimationGenerationRequest(Guid BatchEntryId, string PackagePath, string DumpRoot, string Category,
    CharacterAnimationProfile Profile, CharacterMotionFamily Family, IReadOnlyList<AnimationBankSource> Banks,
    string? GeneratedFamilyName = null, uint? GeneratedFamilyCrc32 = null)
{
    public string? GeneratedUniformName { get; init; }
}

public sealed record ExportResourceOperation(
    Guid BatchEntryId,
    string PackagePath,
    string DestinationPath,
    string Checksum,
    long ByteLength,
    string? PackageResourcePath = null);

public sealed record ExportGameReferenceOperation(
    Guid BatchEntryId,
    string VirtualPath,
    bool Exists,
    string? SourcePath = null);

public sealed record ExportPatchOperation(
    Guid BatchEntryId,
    string TablePath,
    string SymbolicKey,
    IReadOnlyDictionary<string, string?> Values);

public sealed record ExportLocalizationOperation(
    Guid BatchEntryId,
    string Locale,
    string NameTablePath,
    string DescriptionTablePath,
    string RomanizedNameTablePath,
    CharacterPackageLocalization Text);

public sealed record ExportFileOperation(string SourcePath, string DestinationPath);

public sealed record ExportModelDependencyOperation(
    Guid BatchEntryId,
    ModelDependencyKind Kind,
    string VirtualPath,
    string SourcePath);

public sealed record ExportCharacterCoreOperation(
    Guid BatchEntryId,
    string BaseTablePath,
    string ParameterTablePath,
    int SourceBaseId,
    int SourceParameterId,
    int Affinity,
    int MainPosition,
    int SubPosition,
    CharacterDraftSkills? Skills = null,
    bool UsesAllocatedModel = false,
    string? TemplateBaseTableSourcePath = null,
    string? TemplateParameterTableSourcePath = null,
    int? PlayStyle = null,
    int? Growth = null,
    int? Rank = null,
    int? AbilityBoardId = null,
    int? SpecialRarity = null,
    int? Gender = null,
    int? AcademicYear = null,
    int? SourceSeries = null,
    int? UniformPortraitVariant = null,
    int? TeamAssociation1 = null,
    int? TeamAssociation2 = null,
    int? TeamAssociation3 = null,
    int? OriginGameAssociationIndex = null,
    bool? SkillsUnlocked = null,
    int VariantIndex = 0,
    bool WritesBaseRow = true);

public sealed record ExportCharacterModelOperation(
    Guid BatchEntryId,
    string ModelTablePath,
    int SourceModelId,
    string FaceModelPath,
    uint? SkinColorRgba = null,
    int? UniformModel = null,
    int? ShoesModel = null,
    int? GloveModel = null,
    int? EquipmentColor = null,
    int? UniformCollarOpen = null,
    int? EquipmentFlag2 = null,
    int? ChestSize = null,
    int? ForceKit = null,
    int? BodyModelId = null);

public sealed record ExportCharacterBodyOperation(
    Guid BatchEntryId,
    string ModelTablePath,
    int SourceBodyModelId,
    int BodyModelId,
    string BodyModelPath,
    string? SkeletonModelPath = null,
    int? MotionFamilyCrc32 = null,
    string? SkeletonTemplatePath = null);

public sealed record ExportCharacterClothesOperation(
    Guid BatchEntryId,
    string ClothesTablePath,
    int SourceUniformModelId,
    uint UniformModelId,
    string SourceResourceKey,
    string ResourceKey,
    string ModelPath,
    string TexturePath,
    int ClothesInfoType,
    bool IsShoes = false,
    string? SourceModelPath = null,
    string? CustomModelPath = null,
    int? SourceInfoIndex = null);

public sealed record ExportShopCharacterOperation(
    Guid BatchEntryId,
    string ShopTablePath,
    int SourceItemId,
    int SourceParameterId,
    int Rarity,
    int SpecialVariant,
    bool IsFree = false,
    int? SourceShopParameterId = null);

public sealed record ExportCharacterDeliveryOperation(Guid BatchEntryId, string DeliveryTablePath, int VariantIndex = 0);

public sealed record ExportDeliveryLocalizationOperation(
    Guid BatchEntryId,
    string Locale,
    string TablePath,
    string Title);

public sealed record ExportPortraitOperation(
    Guid BatchEntryId,
    string PackagePath,
    string StandardPngPath,
    string UniformPngPath,
    string TemplatePath,
    string DestinationPath);

public interface IExportPlanner
{
    ExportPlan CreatePlan(
        ModProjectDocument project,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath);

    Task<ExportPlan> CreatePlanAsync(
        ModProjectDocument project,
        GameDumpProfile profile,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath,
        CancellationToken cancellationToken);

    Task<ExportPlan> CreatePlanAsync(
        ModProjectDocument project,
        GameDumpProfile profile,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath,
        CancellationToken cancellationToken,
        AnimationGenerationSelection? animationSelection);
}

public sealed class ExportPlanner : IExportPlanner
{
    private readonly IVrCharaPackageService _packageService;
    private readonly ICharacterIdInventoryService _inventoryService;
    private readonly IExportIdAllocator _idAllocator;
    private readonly IModelDependencyResolver _modelDependencyResolver;
    private readonly ICharacterSourceTableResolver _characterSourceTableResolver;

    private const string ShopConfigPattern = "common/gamedata/shop/shop_config_*.cfg.bin";
    private const string PlayersUniverseConfigPattern =
        "common/gamedata/players_universe/players_universe_config_*.cfg.bin";
    private const string DeliveryConfigPattern = "common/gamedata/post/delivery_config_*.cfg.bin";
    private const string DeliveryTextPattern = "common/text/*/post_text.cfg.bin";
    private const string CharacterBasePattern = "common/gamedata/character/chara_base_*.cfg.bin";
    private const string CharacterParameterPattern = "common/gamedata/character/chara_param_*.cfg.bin";
    private const string CharacterTextPattern = "common/text/*/chara_text.cfg.bin";
    private const string CharacterDescriptionTextPattern = "common/text/*/chara_description_text.cfg.bin";
    private const string CharacterRomanizedTextPattern = "common/text/*/chara_text_roma.cfg.bin";

    public ExportPlanner(
        IVrCharaPackageService? packageService = null,
        ICharacterIdInventoryService? inventoryService = null,
        IExportIdAllocator? idAllocator = null,
        IModelDependencyResolver? modelDependencyResolver = null,
        ICharacterSourceTableResolver? characterSourceTableResolver = null)
    {
        _packageService = packageService ?? new ZipVrCharaPackageService();
        _inventoryService = inventoryService ?? new FileSystemCharacterIdInventoryService();
        _idAllocator = idAllocator ?? new ExportIdAllocator();
        _modelDependencyResolver = modelDependencyResolver ?? new ModelDependencyResolver();
        _characterSourceTableResolver = characterSourceTableResolver ?? new CharacterSourceTableResolver();
    }

    public ExportPlan CreatePlan(
        ModProjectDocument project,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var operation = GlobalLog.BeginOperation("export_plan_create", new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["acquisition"] = acquisition,
            ["batchCount"] = project.Batch.Count,
        });

        var enabledCount = project.Batch.Count(entry => entry.IsEnabled);
        var affectedFiles = GetAffectedFiles(platform, acquisition);
        var diagnostics = new List<Diagnostic>();
        if (enabledCount == 0)
        {
            diagnostics.Add(new Diagnostic(
                "batch.empty",
                DiagnosticSeverity.Error,
                "The batch does not contain an enabled character package.",
                "Add or enable at least one .vrchara package."));
        }
        else
        {
            diagnostics.Add(new Diagnostic(
                "export.dump_required",
                DiagnosticSeverity.Error,
                "A game dump must be loaded before VictoryTool can prepare an export.",
                "Load the DUMP from the Project screen, then preview the export again."));
            if (acquisition is AcquisitionMode.Constellation or AcquisitionMode.Both)
            {
                diagnostics.Add(new Diagnostic(
                    "cfgbin.rdbnp_writer_missing",
                    DiagnosticSeverity.Error,
                    "Constellation acquisition requires editing the RDBNP players-universe configuration.",
                    "Implement and verify RDBNP writing before enabling constellation export."));
            }
        }

        var result = new ExportPlan(
            platform,
            acquisition,
            Path.GetFullPath(outputPath),
            enabledCount,
            affectedFiles,
            diagnostics,
            []);
        GlobalLog.Debug("export_plan_created", new Dictionary<string, object?>
        {
            ["enabledPackageCount"] = enabledCount,
            ["diagnosticCount"] = diagnostics.Count,
            ["affectedFileCount"] = affectedFiles.Count,
        });
        return result;
    }

    public async Task<ExportPlan> CreatePlanAsync(
        ModProjectDocument project,
        GameDumpProfile profile,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath,
        CancellationToken cancellationToken)
        => await CreatePlanAsync(project, profile, platform, acquisition, outputPath, cancellationToken, null);

    public async Task<ExportPlan> CreatePlanAsync(
        ModProjectDocument project,
        GameDumpProfile profile,
        ExportPlatform platform,
        AcquisitionMode acquisition,
        string outputPath,
        CancellationToken cancellationToken,
        AnimationGenerationSelection? animationSelection)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = GlobalLog.BeginOperation("export_plan_build", new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["acquisition"] = acquisition,
            ["profileId"] = profile.Id,
        });
        var plan = CreatePlan(project, platform, acquisition, outputPath);
        if (plan.EnabledPackageCount == 0)
        {
            GlobalLog.Warn("export_plan_empty");
            return plan;
        }

        // The synchronous overload deliberately reports that no dump is available.
        // This overload has already received a validated profile, so carrying that
        // placeholder diagnostic forward incorrectly blocks every real export.
        plan = plan with
        {
            Diagnostics = plan.Diagnostics
                .Where(diagnostic => diagnostic.Code != "export.dump_required")
                .ToArray(),
        };

        ExportIdInventory inventory;
        try
        {
            inventory = await _inventoryService.LoadAsync(profile, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            GlobalLog.Error("export_id_inventory_failed", exception);
            return AddFailure(
                plan,
                "export.id_assignment_failed",
                $"Numeric character IDs could not be assigned: {exception.Message}",
                "Verify the active dump before exporting.");
        }

        try
        {
            var requests = new List<ExportIdRequest>(plan.EnabledPackageCount * 6);
            var packages = new List<ExportPackagePlan>(plan.EnabledPackageCount);
            var resources = new List<ExportResourceOperation>();
            var gameReferences = new List<ExportGameReferenceOperation>();
            var patches = new List<ExportPatchOperation>();
            var localizations = new List<ExportLocalizationOperation>();
            var modelDependencies = new List<ExportModelDependencyOperation>();
            var modelDiagnostics = new List<Diagnostic>();
            var characterCoreOperations = new List<ExportCharacterCoreOperation>();
            var characterCoreDiagnostics = new List<Diagnostic>();
            var shopCharacterOperations = new List<ExportShopCharacterOperation>();
            var shopCharacterDiagnostics = new List<Diagnostic>();
            var deliveryOperations = new List<ExportCharacterDeliveryOperation>();
            var deliveryLocalizations = new List<ExportDeliveryLocalizationOperation>();
            var deliveryDiagnostics = new List<Diagnostic>();
            var variantDiagnostics = new List<Diagnostic>();
            var characterModelOperations = new List<ExportCharacterModelOperation>();
            var characterBodyOperations = new List<ExportCharacterBodyOperation>();
            var portraitRequests = new List<(Guid BatchEntryId, string PackagePath, string StandardPath, string UniformPath)>();
            var animationRequests = new List<AnimationGenerationRequest>();
            var characterClothesOperations = new List<ExportCharacterClothesOperation>();
            var expectedVariantCount = 0;
            foreach (var entry in project.Batch.Where(entry => entry.IsEnabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = await _packageService.LoadManifestAsync(entry.PackagePath, cancellationToken);
                var models = manifest.Character.Models;
                var uniformPath = models?.UniformModelPath;
                var animationProfiles = manifest.Character.AnimationProfiles ?? [];
                var customFaceMetadataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var customHeadPath = models?.HeadModelPath?.Replace('\\', '/').TrimStart('/');
                if (customHeadPath?.StartsWith("_face/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var authoredMetadata = manifest.Resources
                        .Select(resource => resource.VirtualPath.Replace('\\', '/').TrimStart('/'))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var metadataStem = Path.ChangeExtension(customHeadPath, null);
                    if (!authoredMetadata.Contains($"common/chr/{metadataStem}.objbin")
                        || !authoredMetadata.Contains($"common/chr/{metadataStem}.clobin"))
                    {
                        var metadataResult = _modelDependencyResolver.ResolveCustomFaceMetadata(profile, customHeadPath);
                        modelDependencies.AddRange(metadataResult.Dependencies
                            .Where(dependency => !authoredMetadata.Contains(dependency.VirtualPath))
                            .Select(dependency => new ExportModelDependencyOperation(
                                entry.Id, dependency.Kind, dependency.VirtualPath, dependency.SourcePath)));
                        customFaceMetadataPaths.UnionWith(metadataResult.Dependencies.Select(dependency => dependency.VirtualPath));
                        modelDiagnostics.AddRange(metadataResult.Diagnostics);
                    }
                }
                var animationProfile = animationProfiles.FirstOrDefault(profile =>
                    string.Equals(
                        NormalizeBodyModelPath(profile.ModelPath),
                        NormalizeBodyModelPath(uniformPath),
                        StringComparison.OrdinalIgnoreCase));
                if (animationProfile is null
                    && uniformPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    foreach (var modelPath in new[]
                             {
                                 models?.BodyModelPath,
                                 models?.HeadModelPath,
                                 models?.ShoesModelPath,
                             })
                    {
                        if (modelPath is null) continue;
                        animationProfile = animationProfiles.FirstOrDefault(profile =>
                            profile.AvatarRecipe is not null
                            && string.Equals(
                                NormalizeBodyModelPath(profile.ModelPath),
                                NormalizeBodyModelPath(modelPath),
                                StringComparison.OrdinalIgnoreCase));
                        if (animationProfile is not null) break;
                    }
                }
                if (uniformPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true
                    && animationProfile is { SchemaVersion: >= 2, Joints.Count: > 0 }
                    && (animationProfile.AvatarRecipe is not null || animationProfile.GeneratedPackages.Count > 0 || animationProfile.GeneratedBanks.Count > 0)
                    && (animationProfile.AvatarRecipe is null || animationSelection?.Enabled != false))
                {
                    try
                    {
                        if (animationProfile.AvatarRecipe is not null && animationSelection is { Enabled: true, Chr: false, Event: false })
                            throw new InvalidDataException("Select CHR animations, EVENT animations, or both.");
                        var family = new CharacterMotionFamilyResolver().Resolve(
                            profile.RootPath,
                            animationProfile.SourceModelStem ?? manifest.Character.SourceCharacterId
                                ?? throw new InvalidDataException("The character package has no source character ID for motion-family routing."));
                        ValidateAnimationRigProfile(profile.RootPath, manifest, family, animationProfile);
                        var resolver = new CharacterMotionFamilyResolver();
                        var resolvedBanks = animationProfile.AvatarRecipe is { } recipe
                            ? AvatarAnimationResources.Select(profile.RootPath,
                                animationSelection is { Enabled: true }
                                    ? recipe with { ExportChr = animationSelection.Chr, ExportEvent = animationSelection.Event }
                                    : recipe,
                                resolver, family, animationProfile, cancellationToken)
                            : resolver.ResolveBanks(profile.RootPath, family, animationProfile);
                        var packageByRoute = new Dictionary<string, CharacterAnimationPackage>(StringComparer.OrdinalIgnoreCase);
                        foreach (var generatedPackage in animationProfile.GeneratedPackages)
                        {
                            var routes = resolvedBanks.Where(bank => bank.Type == generatedPackage.Type).ToArray();
                            var primaryRoutes = routes.Where(bank => bank.IsPrimaryFamily).ToArray();
                            var route = primaryRoutes.Length == 1 ? primaryRoutes[0]
                                : primaryRoutes.Length > 1 ? throw new InvalidDataException(
                                    $"Motion family has multiple primary routes for p{generatedPackage.Type}.")
                                : routes.Length == 1 ? routes[0]
                                : routes.Length == 0 ? null
                                : throw new InvalidDataException(
                                    $"Motion family has ambiguous routes for p{generatedPackage.Type}.");
                            if (route is null) continue;
                            if (!packageByRoute.TryAdd(route.ResourcePath, generatedPackage))
                                throw new InvalidDataException($"Multiple generated G4PK banks match '{route.ResourcePath}'.");
                        }

                        var generatedBanks = animationProfile.GeneratedBanks.ToDictionary(
                            bank => bank.SourceResourcePath,
                            StringComparer.OrdinalIgnoreCase);
                        var packageResources = manifest.Resources.ToDictionary(
                            resource => resource.VirtualPath,
                            StringComparer.OrdinalIgnoreCase);
                        var banks = resolvedBanks.Select(bank =>
                        {
                            if (animationProfile.AvatarRecipe is not null) return bank;
                            if (packageByRoute.TryGetValue(bank.ResourcePath, out var generatedPackage))
                            {
                                if (!packageResources.TryGetValue(generatedPackage.ResourcePath, out var resource)
                                    || !resource.Checksum.Equals(generatedPackage.Sha256, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException($"Generated G4PK bank '{generatedPackage.ResourcePath}' is missing or has a mismatched checksum.");
                                return bank with
                                {
                                    GeneratedPackageResourcePath = generatedPackage.ResourcePath,
                                    GeneratedPackageChecksum = generatedPackage.Sha256,
                                };
                            }
                            if (!generatedBanks.TryGetValue(bank.ResourcePath, out var generated)) return null;
                            if (!packageResources.TryGetValue(generated.ResourcePath, out var generatedResource)
                                || !generatedResource.Checksum.Equals(generated.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException($"Generated G4MT bank '{generated.ResourcePath}' is missing or has a mismatched checksum.");
                            return bank with
                            {
                                GeneratedResourcePath = generated.ResourcePath,
                                GeneratedChecksum = generated.Sha256,
                            };
                        }).Where(bank => bank is not null).Cast<AnimationBankSource>().ToArray();
                        if (banks.Length == 0)
                            throw new InvalidDataException("No generated G4_Blender banks match the character's resolved motion routes.");
                        animationRequests.Add(new AnimationGenerationRequest(
                            entry.Id, entry.PackagePath, profile.RootPath, "All", animationProfile, family, banks));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        modelDiagnostics.Add(new Diagnostic(
                            "animation.family_unresolved",
                            DiagnosticSeverity.Error,
                            $"Animation family for '{animationProfile.SourceModelStem}' could not be resolved: {exception.Message}",
                            "Verify that the dump contains matching CHARA_BASE_INFO, CHARA_MODEL_INFO, CHARA_BODY_INFO and CHARA_MOTION_INFO records."));
                    }
                }
                var storedVariants = manifest.Character.Variants is { Count: > 0 }
                    ? manifest.Character.Variants
                    : [new CharacterDraftVariant(
                        TryReadField(manifest.Character.Fields, "Gameplay.ParameterId", out var legacyParameterId)
                            ? legacyParameterId
                            : 0,
                        manifest.Character.Gameplay,
                        manifest.Character.Skills ?? CharacterDraftSkills.FromLegacyFields(manifest.Character.Fields))];
                var variants = CharacterRarityCatalog.OrderForRuntime(
                        CharacterRarityCatalog.EnsureDraftVariants(storedVariants))
                    ?? storedVariants;
                var symbolicId = ResolvePackageSymbolicId(manifest.Character, entry.Id);
                GlobalLog.Debug("export_package_planned", new Dictionary<string, object?>
                {
                    ["variantCount"] = variants.Count,
                    ["resourceCount"] = manifest.Resources.Count,
                    ["gameReferenceCount"] = manifest.GameResources.Count,
                    ["patchSetCount"] = manifest.Patches.Count,
                    ["hasLocalizations"] = manifest.Localizations is not null,
                });
                expectedVariantCount += variants.Count;
                var rarityGroups = variants
                    .GroupBy(variant => variant.Gameplay.SpecialRarity ?? 0)
                    .Where(group => group.Count() > 1)
                    .ToArray();
                foreach (var group in rarityGroups)
                {
                    variantDiagnostics.Add(new Diagnostic(
                        "export.variant_rarity_duplicate",
                        DiagnosticSeverity.Warning,
                        $"Character '{symbolicId}' has {group.Count()} parameter rows with rarity {group.Key}.",
                        group.Key == 0
                            ? "All rows will be exported, but Delivery uses the first rarity-0 row because the game may apply another hidden variant key."
                            : "All rows will be exported; use the rarity selector to edit each row independently."));
                }
                if (acquisition is AcquisitionMode.Delivery &&
                    !variants.Any(variant => (variant.Gameplay.SpecialRarity ?? 0) == 0))
                {
                    variantDiagnostics.Add(new Diagnostic(
                        "export.delivery_primary_rarity_missing",
                        DiagnosticSeverity.Error,
                        $"Character '{symbolicId}' has no rarity-0 parameter row.",
                        "Delivery rewards the first rarity. Add or preserve a rarity-0 variant before exporting."));
                }
                packages.Add(new ExportPackagePlan(entry.Id, entry.PackagePath, symbolicId));
                requests.Add(new ExportIdRequest(
                    entry.Id,
                    "character",
                    ResolveCharacterIdKey(manifest.Character, entry.Id),
                    RequiresExactCrc: true));
                for (var variantIndex = 0; variantIndex < variants.Count; variantIndex++)
                {
                    var domain = variantIndex == 0 ? "parameter" : "parameterVariant";
                    requests.Add(new ExportIdRequest(entry.Id, domain,
                        $"{symbolicId}.variant.{variantIndex}"));
                    if (acquisition is AcquisitionMode.Delivery && variantIndex == 0)
                    {
                        requests.Add(new ExportIdRequest(entry.Id, "delivery",
                            $"{symbolicId}.delivery.variant.0"));
                        requests.Add(new ExportIdRequest(entry.Id, "deliveryReceived",
                            $"{symbolicId}.delivery.received.variant.0"));
                        requests.Add(new ExportIdRequest(entry.Id, "deliveryTitleText",
                            $"{symbolicId}.delivery.title"));
                    }
                }
                requests.Add(new ExportIdRequest(
                    entry.Id, "nameText", $"{symbolicId}.name.full"));
                requests.Add(new ExportIdRequest(
                    entry.Id, "nameText", $"{symbolicId}.name.short"));
                requests.Add(new ExportIdRequest(
                    entry.Id, "nameText", $"{symbolicId}.name.upper"));
                requests.Add(new ExportIdRequest(
                    entry.Id, "descriptionText", $"{symbolicId}.description"));
                if (acquisition is AcquisitionMode.Delivery)
                {
                    if (TryCreateCharacterDeliveryOperation(profile, entry.Id, 0, out var deliveryOperation, out var deliveryDiagnostic))
                        deliveryOperations.Add(deliveryOperation!);
                    else
                        deliveryDiagnostics.Add(deliveryDiagnostic!);
                    deliveryLocalizations.AddRange(CreateDeliveryLocalizationOperations(profile.RootPath, entry.Id));
                }

                var usesAllocatedModel = TryCreateCharacterModelOperation(
                    profile, entry.Id, manifest, out var modelOperation, out var customModelDiagnostic);
                if (modelOperation is not null)
                {
                    if (TryCreateCharacterBodyOperation(
                            profile,
                            entry.Id,
                            manifest,
                            modelOperation,
                            customFaceMetadataPaths,
                            animationProfile,
                            out var bodyOperation,
                            out var bodyDiagnostic))
                    {
                        if (bodyOperation is not null)
                        {
                            characterBodyOperations.Add(bodyOperation);
                            modelOperation = modelOperation with { BodyModelId = bodyOperation.BodyModelId };
                        }
                    }
                    else if (bodyDiagnostic is not null)
                    {
                        modelDiagnostics.Add(bodyDiagnostic);
                    }
                    characterModelOperations.Add(modelOperation);
                    requests.Add(new ExportIdRequest(
                        entry.Id, "model", $"{symbolicId}.model"));
                }
                if (customModelDiagnostic is not null) modelDiagnostics.Add(customModelDiagnostic);

                if (TryCreateCharacterClothesOperation(
                        profile,
                        entry.Id,
                        manifest,
                        out var clothesOperation,
                        out var clothesDiagnostic))
                {
                    if (clothesOperation is not null)
                    {
                        characterClothesOperations.Add(clothesOperation);
                        requests.Add(new ExportIdRequest(entry.Id, "uniformModel", $"{symbolicId}.uniform"));
                    }
                }
                else if (clothesDiagnostic is not null)
                {
                    modelDiagnostics.Add(clothesDiagnostic);
                }
                if (TryCreateCharacterShoesOperation(
                        profile, entry.Id, manifest, out var shoesOperation, out var shoesDiagnostic))
                {
                    if (shoesOperation is not null)
                    {
                        characterClothesOperations.Add(shoesOperation);
                        requests.Add(new ExportIdRequest(entry.Id, "shoesModel", $"{symbolicId}.shoes"));
                    }
                }
                else if (shoesDiagnostic is not null)
                {
                    modelDiagnostics.Add(shoesDiagnostic);
                }

                if (acquisition is AcquisitionMode.Shop or AcquisitionMode.Both)
                {
                    if (TryCreateShopCharacterOperation(
                            profile, entry, manifest.Character, out var shopOperation, out var shopDiagnostic))
                    {
                        shopCharacterOperations.Add(shopOperation!);
                        requests.Add(new ExportIdRequest(
                            entry.Id, "shopItem", $"{symbolicId}.shop.item"));
                    }
                    else
                    {
                        shopCharacterDiagnostics.Add(shopDiagnostic!);
                    }
                }

                for (var variantIndex = 0; variantIndex < variants.Count; variantIndex++)
                {
                    var variant = variants[variantIndex];
                    if (TryCreateCharacterCoreOperation(
                            project, profile, entry.Id, manifest.Character, usesAllocatedModel,
                            out var coreOperation, out var coreDiagnostic,
                            variant.Gameplay, variant.Skills, variant.SourceParameterId,
                            variantIndex, variantIndex == 0))
                        characterCoreOperations.Add(coreOperation!);
                    else
                        characterCoreDiagnostics.Add(coreDiagnostic!);
                }

                resources.AddRange(manifest.Resources.Select(resource => new ExportResourceOperation(
                    entry.Id,
                    entry.PackagePath,
                    resource.VirtualPath.StartsWith("assets/portraits/", StringComparison.OrdinalIgnoreCase)
                        ? $"assets/portraits/{entry.Id:N}/{Path.GetFileName(resource.VirtualPath)}"
                        : resource.VirtualPath,
                    resource.Checksum,
                    resource.ByteLength,
                    resource.VirtualPath)));
                var standardPortrait = manifest.Resources.FirstOrDefault(resource =>
                    resource.VirtualPath.Equals("assets/portraits/standard.png", StringComparison.OrdinalIgnoreCase));
                var uniformPortrait = manifest.Resources.FirstOrDefault(resource =>
                    resource.VirtualPath.Equals("assets/portraits/uniform.png", StringComparison.OrdinalIgnoreCase));
                if (platform == ExportPlatform.Pc && standardPortrait is not null && uniformPortrait is not null)
                {
                    portraitRequests.Add((entry.Id, entry.PackagePath, standardPortrait.VirtualPath, uniformPortrait.VirtualPath));
                    resources.RemoveAll(resource => resource.BatchEntryId == entry.Id
                        && resource.DestinationPath.Contains("/200_icon/", StringComparison.OrdinalIgnoreCase)
                        && resource.DestinationPath.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase));
                }
                foreach (var reference in manifest.GameResources)
                {
                    var relativePath = reference.VirtualPath.Replace('/', Path.DirectorySeparatorChar);
                    gameReferences.Add(new ExportGameReferenceOperation(
                        entry.Id,
                        reference.VirtualPath,
                        File.Exists(Path.Combine(profile.RootPath, relativePath)),
                        Path.Combine(profile.RootPath, relativePath)));
                }
                patches.AddRange(manifest.Patches.SelectMany(patchSet =>
                    patchSet.Records.Select(record => new ExportPatchOperation(
                        entry.Id,
                        patchSet.TablePath,
                        record.SymbolicKey,
                        record.Values))));
                if (manifest.Localizations is not null)
                {
                    localizations.AddRange(manifest.Localizations.Select(localization =>
                        new ExportLocalizationOperation(
                            entry.Id,
                            localization.Key,
                            $"common/text/{localization.Key}/chara_text.cfg.bin",
                            $"common/text/{localization.Key}/chara_description_text.cfg.bin",
                            $"common/text/{localization.Key}/chara_text_roma.cfg.bin",
                            localization.Value)));
                }
                foreach (var modelPath in new[]
                         {
                             manifest.Character.Models?.HeadModelPath,
                             manifest.Character.Models?.BodyModelPath,
                             manifest.Character.Models?.UniformModelPath,
                             manifest.Character.Models?.ShoesModelPath,
                         }.Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var isCustomHead = usesAllocatedModel
                        && manifest.Character.Models?.HeadModelPath?.StartsWith("_face/99_CUSTOM/", StringComparison.Ordinal) == true
                        && string.Equals(modelPath, manifest.Character.Models.HeadModelPath, StringComparison.OrdinalIgnoreCase);
                    var isCustomUniform = manifest.Character.Models?.UniformModelPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.Ordinal) == true
                        && string.Equals(modelPath, manifest.Character.Models.UniformModelPath, StringComparison.OrdinalIgnoreCase);
                    var isCustomBody = manifest.Character.Models?.BodyModelPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true
                        && string.Equals(modelPath, manifest.Character.Models.BodyModelPath, StringComparison.OrdinalIgnoreCase);
                    var isCustomShoes = manifest.Character.Models?.ShoesModelPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true
                        && string.Equals(modelPath, manifest.Character.Models.ShoesModelPath, StringComparison.OrdinalIgnoreCase);
                    if (isCustomHead || isCustomUniform || isCustomBody || isCustomShoes)
                        continue;
                    var result = _modelDependencyResolver.Resolve(profile, platform, modelPath!);
                    modelDependencies.AddRange(result.Dependencies.Select(dependency =>
                        new ExportModelDependencyOperation(
                            entry.Id,
                            dependency.Kind,
                            dependency.VirtualPath,
                            dependency.SourcePath)));
                    modelDiagnostics.AddRange(result.Diagnostics);
                }
                AddNativeCustomUniformObjectMetadata(
                    profile,
                    entry.Id,
                    manifest,
                    modelDependencies);
            }

            var diagnostics = plan.Diagnostics.ToList();
            diagnostics.AddRange(modelDiagnostics);
            diagnostics.AddRange(characterCoreDiagnostics);
            diagnostics.AddRange(shopCharacterDiagnostics);
            diagnostics.AddRange(deliveryDiagnostics);
            diagnostics.AddRange(variantDiagnostics);
            var fileOperations = ResolveFileOperations(profile, plan.AffectedFiles, diagnostics);
            AddOutputDiagnostics(profile, plan.OutputPath, diagnostics);
            AddDependencyDiagnostics(
                profile,
                platform,
                packages,
                resources,
                gameReferences,
                patches,
                localizations,
                diagnostics);
            if (characterCoreOperations.Count == expectedVariantCount && characterCoreDiagnostics.Count == 0)
                diagnostics.RemoveAll(diagnostic =>
                    diagnostic.Code == "cfgbin.character_structural_writer_missing");
            if (packages.All(package => localizations.Any(localization =>
                    localization.BatchEntryId == package.BatchEntryId)))
                diagnostics.RemoveAll(diagnostic =>
                    diagnostic.Code == "cfgbin.localization_structural_writer_missing");
            if (acquisition is AcquisitionMode.Shop or AcquisitionMode.Both
                && shopCharacterOperations.Count == packages.Count
                && shopCharacterDiagnostics.Count == 0)
                diagnostics.RemoveAll(diagnostic =>
                    diagnostic.Code == "cfgbin.t2b_structural_writer_missing");
            if (acquisition is AcquisitionMode.Delivery
                // Delivery unlocks the primary (rarity 0) row only. The other
                // rarity rows are linked through CHARA_PARAM_INFO and must not
                // receive duplicate delivery assignments.
                && deliveryOperations.Count == packages.Count
                && deliveryDiagnostics.Count == 0)
                diagnostics.RemoveAll(diagnostic => diagnostic.Code == "rdbnp.delivery_writer_missing");
            if (patches.Count == 0
                && characterCoreOperations.Count == expectedVariantCount
                && modelDiagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error))
                diagnostics.RemoveAll(diagnostic => diagnostic.Code == "export.dependencies_unresolved");
            var assignedIds = AllocateIds(requests, inventory);
            (characterBodyOperations, characterModelOperations) = AllocateCustomBodyModelIds(
                profile.RootPath, characterBodyOperations, characterModelOperations, assignedIds);
            resources = RewriteCustomResourceNames(resources, assignedIds, characterClothesOperations);
            modelDependencies = RewriteCustomModelDependencyNames(modelDependencies, assignedIds);
            characterModelOperations = RewriteCustomModelNames(characterModelOperations, assignedIds);
            characterBodyOperations = RewriteCustomBodyNames(characterBodyOperations, assignedIds);
            characterClothesOperations = RewriteCustomClothesNames(characterClothesOperations, assignedIds);
            characterModelOperations = RewriteCustomUniformIds(characterModelOperations, characterClothesOperations);
            var resolvedAnimationRequests = animationRequests.Select(request =>
            {
                var familyName = ResolveGeneratedMotionFamilyName(assignedIds.Single(assignment =>
                    assignment.BatchEntryId == request.BatchEntryId && assignment.Domain == "character").ResolvedKey);
                var uniformName = characterClothesOperations.FirstOrDefault(operation =>
                    operation.BatchEntryId == request.BatchEntryId && !operation.IsShoes)?.ResourceKey;
                var banks = request.Banks.Select(bank => bank with
                {
                    DestinationResourcePath = ResolveGeneratedMotionPath(bank, familyName, uniformName),
                }).ToArray();
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var bank in banks)
                {
                    if (!destinations.Add(bank.DestinationResourcePath!))
                        throw new InvalidDataException($"Multiple source motion banks resolve to '{bank.DestinationResourcePath}'.");
                }
                return request with
                {
                    Banks = banks,
                    GeneratedFamilyName = familyName,
                    GeneratedFamilyCrc32 = CharacterMotionInfoT2bWriter.Crc32(familyName),
                    GeneratedUniformName = uniformName,
                };
            }).ToArray();
            foreach (var animation in resolvedAnimationRequests.Where(request => request.Banks.Any(bank => !bank.IsEvent)))
            {
                var tablePath = Path.Combine(profile.RootPath,
                    animation.Family.MotionTablePath.Replace('/', Path.DirectorySeparatorChar));
                var table = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
                if (table.Entries.Any(entry => entry.Name == "CHARA_MOTION_INFO" && entry.Values.Count >= 1
                        && unchecked((uint)GetCfgInteger(entry.Values[0])) == animation.GeneratedFamilyCrc32!.Value))
                    diagnostics.Add(new Diagnostic("animation.family_crc_collision", DiagnosticSeverity.Error,
                        $"Generated animation family '{animation.GeneratedFamilyName}' collides with an existing motion registration.",
                        "Use a character ID whose CRC32 is not registered in CHARA_MOTION_INFO."));
                characterBodyOperations = characterBodyOperations.Select(body => body.BatchEntryId == animation.BatchEntryId
                    ? body with { MotionFamilyCrc32 = unchecked((int)animation.GeneratedFamilyCrc32!.Value) }
                    : body).ToList();
            }
            var portraitOperations = portraitRequests.Select(request =>
            {
                var internalName = assignedIds.Single(assignment =>
                    assignment.BatchEntryId == request.BatchEntryId && assignment.Domain == "character").ResolvedKey;
                return new ExportPortraitOperation(
                    request.BatchEntryId,
                    request.PackagePath,
                    request.StandardPath,
                    request.UniformPath,
                    FindPortraitTemplate(profile, platform),
                    $"{(platform == ExportPlatform.Pc ? "dx11" : "nx")}/menu/200_icon/10_icon_chr/face/{internalName}_l.g4tx");
            }).ToArray();
            var completedPlan = plan with
            {
                AssignedIds = assignedIds,
                Packages = packages,
                ResourceOperations = resources,
                GameReferenceOperations = gameReferences,
                PatchOperations = patches,
                LocalizationOperations = localizations,
                FileOperations = fileOperations,
                ModelDependencyOperations = modelDependencies
                    .DistinctBy(
                        operation => $"{operation.VirtualPath}\0{Path.GetFullPath(operation.SourcePath)}",
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(operation => operation.BatchEntryId)
                    .ThenBy(operation => operation.VirtualPath, StringComparer.Ordinal)
                    .ToArray(),
                CharacterCoreOperations = characterCoreOperations,
                CharacterModelOperations = characterModelOperations,
                CharacterBodyOperations = characterBodyOperations,
                CharacterClothesOperations = characterClothesOperations,
                ShopCharacterOperations = shopCharacterOperations,
                CharacterDeliveryOperations = deliveryOperations,
                DeliveryLocalizationOperations = deliveryLocalizations,
                PortraitOperations = portraitOperations,
                AnimationGenerationRequests = resolvedAnimationRequests,
                AffectedFiles = fileOperations.Select(operation => operation.DestinationPath)
                    .Concat(resources.Select(operation => operation.DestinationPath))
                    .Concat(portraitOperations.Select(operation => operation.DestinationPath))
                    .Concat(gameReferences.Select(operation => operation.VirtualPath))
                    .Concat(modelDependencies.Select(operation => operation.VirtualPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Diagnostics = diagnostics,
            };
            GlobalLog.Info("export_plan_built", new Dictionary<string, object?>
            {
                ["enabledPackageCount"] = completedPlan.EnabledPackageCount,
                ["assignedIdCount"] = completedPlan.AssignedIds.Count,
                ["resourceOperationCount"] = completedPlan.ResourceOperations.Count,
                ["fileOperationCount"] = completedPlan.FileOperations.Count,
                ["modelDependencyCount"] = completedPlan.ModelDependencyOperations.Count,
                ["diagnosticCount"] = completedPlan.Diagnostics.Count,
                ["canExport"] = completedPlan.CanExport,
            });
            return completedPlan;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            GlobalLog.Error("export_plan_build_failed", exception);
            return AddFailure(
                plan,
                "export.package_invalid",
                $"An enabled .vrchara package is invalid, unreadable, or incompatible: {exception.Message}",
                "Upgrade, repair, or disable the incompatible package before exporting.");
        }
    }

    private static string FindPortraitTemplate(GameDumpProfile profile, ExportPlatform platform)
    {
        var root = Path.Combine(
            profile.RootPath,
            platform == ExportPlatform.Pc ? "dx11" : "nx",
            "menu", "200_icon", "10_icon_chr", "face");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The character portrait directory is missing: {root}");
        foreach (var path in Directory.EnumerateFiles(root, "*_l.g4tx"))
        {
            try
            {
                if (G4TxDocument.Read(File.ReadAllBytes(path)).TextureCount == 2)
                    return path;
            }
            catch (InvalidDataException)
            {
                // Ignore unrelated or damaged files and continue looking for a valid template.
            }
        }
        throw new FileNotFoundException("A two-layer character portrait G4TX template could not be found in the dump.");
    }

    private bool TryCreateCharacterCoreOperation(
        ModProjectDocument project,
        GameDumpProfile profile,
        Guid batchEntryId,
        VictoryTool.Application.Characters.CharacterDraft character,
        bool usesAllocatedModel,
        out ExportCharacterCoreOperation? operation,
        out Diagnostic? diagnostic,
        CharacterDraftGameplay? gameplayOverride = null,
        CharacterDraftSkills? skillsOverride = null,
        int? sourceParameterOverride = null,
        int variantIndex = 0,
        bool writesBaseRow = true)
    {
        operation = null;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(character.SourceCharacterId)
            || !TryReadField(character.Fields, "Identity.BaseId", out var sourceBaseId)
            || (!TryReadField(character.Fields, "Gameplay.ParameterId", out var sourceParameterId)
                && sourceParameterOverride is null))
        {
            diagnostic = new Diagnostic(
                "export.character_source_required",
                DiagnosticSeverity.Error,
                $"Character '{character.SymbolicId}' has no exact linked source rows.",
                "Clone an existing character; from-zero core rows remain disabled until every default is evidenced.");
            return false;
        }

        var gameplay = gameplayOverride ?? character.Gameplay;
        if (sourceParameterOverride is { } overriddenParameterId)
            sourceParameterId = overriddenParameterId;
        if (gameplay?.MainPosition is not { } mainPosition
            || gameplay.SubPosition is not { } subPosition
            || !Enum.TryParse<VictoryTool.Application.Characters.CharacterAffinity>(
                gameplay.Affinity, true, out var affinity)
            || affinity is VictoryTool.Application.Characters.CharacterAffinity.Unknown)
        {
            diagnostic = new Diagnostic(
                "export.character_gameplay_incomplete",
                DiagnosticSeverity.Error,
                $"Character '{character.SymbolicId}' has incomplete affinity or position values.",
                "Select an affinity plus main and sub positions before export.");
            return false;
        }

        try
        {
            var sourceRoot = gameplay.RegistrationProfile is CharacterRegistrationProfile.FunctionalBank
                ? project.FunctionalBankReferenceDataRoot
                    ?? throw new InvalidDataException("This project has no FunctionalBank reference data root.")
                : profile.RootPath;
            var templateBaseId = gameplay.RegistrationProfile is CharacterRegistrationProfile.FunctionalBank
                ? 606200768
                : sourceBaseId;
            var templateParameterId = gameplay.RegistrationProfile is CharacterRegistrationProfile.FunctionalBank
                ? unchecked((int)0x8DD5EDB2)
                : sourceParameterId;
            var source = _characterSourceTableResolver.Resolve(sourceRoot, templateBaseId, templateParameterId);
            var destination = _characterSourceTableResolver.Resolve(profile.RootPath, sourceBaseId, sourceParameterId);
            operation = new ExportCharacterCoreOperation(
                batchEntryId,
                destination.BaseTableVirtualPath,
                destination.ParameterTableVirtualPath,
                templateBaseId,
                templateParameterId,
                (int)affinity,
                mainPosition,
                subPosition,
                skillsOverride ?? character.Skills ?? CharacterDraftSkills.FromLegacyFields(character.Fields),
                usesAllocatedModel,
                gameplay.RegistrationProfile is CharacterRegistrationProfile.FunctionalBank ? source.BaseTableSourcePath : null,
                gameplay.RegistrationProfile is CharacterRegistrationProfile.FunctionalBank ? source.ParameterTableSourcePath : null,
                gameplay.PlayStyle, gameplay.Growth, gameplay.Rank, gameplay.AbilityBoardId, gameplay.SpecialRarity,
                Gender: TryReadInt(character.Fields, "Identity.Gender"),
                AcademicYear: TryReadInt(character.Fields, "Identity.AcademicYear"),
                SourceSeries: TryReadInt(character.Fields, "Identity.SourceSeries"),
                UniformPortraitVariant: TryReadInt(character.Fields, "Advanced.UniformPortraitVariant"),
                TeamAssociation1: TryReadInt(character.Fields, "Identity.TeamAssociation1"),
                TeamAssociation2: TryReadInt(character.Fields, "Identity.TeamAssociation2"),
                TeamAssociation3: TryReadInt(character.Fields, "Identity.TeamAssociation3"),
                OriginGameAssociationIndex: TryReadInt(character.Fields, "Identity.OriginGameIndex"),
                // Custom rows must opt into the game's skill list.  Without
                // this flag the row is accepted structurally but the runtime
                // only exposes the first unlocked branch for that rarity.
                SkillsUnlocked: true);
            operation = operation with
            {
                VariantIndex = variantIndex,
                WritesBaseRow = writesBaseRow,
            };
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostic = new Diagnostic(
                "export.character_source_unresolved",
                DiagnosticSeverity.Error,
                $"Character '{character.SymbolicId}' source rows could not be resolved: {exception.Message}",
                "Use the same compatible dump that supplied the cloned character.");
            return false;
        }
    }


    private static bool TryCreateCharacterModelOperation(
        GameDumpProfile profile,
        Guid batchEntryId,
        VrCharaManifest manifest,
        out ExportCharacterModelOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        var path = manifest.Character.Models?.HeadModelPath?.Replace('\\', '/').TrimStart('/');
        var isCustomModel = !string.IsNullOrWhiteSpace(path)
            && path.StartsWith("_face/99_CUSTOM/", StringComparison.Ordinal);
        var uniformPath = manifest.Character.Models?.UniformModelPath?.Replace('\\', '/').TrimStart('/');
        var usesLaytonModelTemplate = isCustomModel
            || uniformPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true;
        var skinColor = ParseSkinColor(manifest.Character.Models?.SkinColorRgba);
        var sourceSkinColor = ParseSkinColor(manifest.Character.Fields.GetValueOrDefault("Advanced.SourceSkinColorRgba"));
        var overridesSkinColor = skinColor is not null && (sourceSkinColor is null || skinColor != sourceSkinColor);
        var hasAppearanceOverride = new[]
        {
            "Models.UniformModel", "Models.ShoesModel", "Models.GloveModel", "Models.EquipmentColor",
            "Models.UniformCollarOpen", "Models.EquipmentFlag1", "Models.EquipmentFlag2", "Models.ChestSize", "Models.BoobSize", "Models.ForceKit",
            "Models.UniformModelPath",
            "Models.BodyModelPath",
            "Identity.BodyType",
        }.Any(key => manifest.Character.Fields.ContainsKey(key));
        if (!isCustomModel && !overridesSkinColor && !hasAppearanceOverride)
            return false;
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostic = new Diagnostic("export.model_path_missing", DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has no head model path.",
                "Select a head model before exporting a skin-colour override.");
            return true;
        }
        if (!TryReadField(manifest.Character.Fields, "Advanced.ModelId", out var sourceModelId))
        {
            diagnostic = new Diagnostic(
                "export.custom_model_source_missing", DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has no source model ID.",
                "Clone an existing character before assigning a custom model family.");
            return true;
        }

        if (isCustomModel)
        {
            var expectedModel = $"common/chr/{path}";
            var extension = Path.GetExtension(expectedModel);
            var stem = expectedModel[..^extension.Length];
            var expectedResources = new[] { expectedModel, $"{stem}.g4mg", $"dx11/{stem["common/".Length..]}.g4tx" };
            var authored = manifest.Resources.Select(resource => resource.VirtualPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (expectedResources.Any(resource => !authored.Contains(resource)))
            {
                diagnostic = new Diagnostic("export.custom_model_family_incomplete", DiagnosticSeverity.Error,
                    $"Character '{manifest.Character.SymbolicId}' has an incomplete custom model family.",
                    "Embed matching G4MD, G4MG and DX11 G4TX resources.");
                return true;
            }
        }

        var tables = ExpandDumpPattern(
                profile.RootPath, "common/gamedata/character/chara_model_*.cfg.bin")
            .Where(candidate => ContainsModel(candidate, sourceModelId))
            .ToArray();
        if (tables.Length != 1)
        {
            diagnostic = new Diagnostic(
                "export.model_table_ambiguous", DiagnosticSeverity.Error,
                $"Expected one compatible character model table, found {tables.Length}.",
                "Select a compatible dump with one active character model table.");
            return true;
        }
        var modelTemplateId = sourceModelId;
        if (usesLaytonModelTemplate
            && !TryFindLaytonModelId(tables[0], out modelTemplateId, out diagnostic))
            return true;
        int? bodyModelId = null;
        var bodyPathWasExplicitlyCleared = manifest.Character.Fields.TryGetValue(
                "Models.BodyModelPath", out var bodyPathField)
            && string.IsNullOrWhiteSpace(bodyPathField);
        if (bodyPathWasExplicitlyCleared)
        {
            bodyModelId = 0;
        }
        else if (!usesLaytonModelTemplate)
        {
            var requestedBodyPath = NormalizeBodyModelPath(manifest.Character.Models?.BodyModelPath);
            var sourceBodyModelId = TryReadInt(manifest.Character.Fields, "Advanced.BodyModelId");
            var sourceBodyPath = sourceBodyModelId is { } sourceBodyId
                ? TryReadBodyModelPath(tables[0], sourceBodyId)
                : null;
            var bodyPathIsExplicit = requestedBodyPath is not null
                && !string.Equals(requestedBodyPath, sourceBodyPath, StringComparison.OrdinalIgnoreCase);
            if (bodyPathIsExplicit)
            {
                if (!TryResolveBodyModelPath(
                        tables[0], requestedBodyPath!, out bodyModelId, out diagnostic))
                    return true;
            }
            else if (manifest.Character.Fields.ContainsKey("Identity.BodyType"))
            {
                if (!TryReadField(manifest.Character.Fields, "Identity.BodyType", out var bodyType))
                {
                    diagnostic = new Diagnostic(
                        "export.body_type_invalid", DiagnosticSeverity.Error,
                        $"Character '{manifest.Character.SymbolicId}' has an invalid body type.",
                        "Choose one of the body types observed in the selected dump.");
                    return true;
                }
                if (!TryResolveBodyModelId(
                        tables[0],
                        bodyType,
                        TryReadInt(manifest.Character.Fields, "Advanced.BodyGroup"),
                        TryReadInt(manifest.Character.Fields, "Advanced.BodyPoseType"),
                        TryReadInt(manifest.Character.Fields, "Advanced.BodyModelId"),
                        out bodyModelId,
                        out diagnostic))
                    return true;
            }
            else if (requestedBodyPath is not null
                     && !TryResolveBodyModelPath(
                         tables[0], requestedBodyPath, out bodyModelId, out diagnostic))
            {
                return true;
            }
        }
        operation = new ExportCharacterModelOperation(
            batchEntryId,
            Path.GetRelativePath(profile.RootPath, tables[0]).Replace(Path.DirectorySeparatorChar, '/'),
            modelTemplateId,
            path,
            skinColor,
            TryReadInt(manifest.Character.Fields, "Models.UniformModel"),
            TryReadInt(manifest.Character.Fields, "Models.ShoesModel"),
            TryReadInt(manifest.Character.Fields, "Models.GloveModel"),
            TryReadInt(manifest.Character.Fields, "Models.EquipmentColor"),
            TryReadModelInt(manifest.Character.Fields, "Models.UniformCollarOpen", "Models.EquipmentFlag1"),
            TryReadInt(manifest.Character.Fields, "Models.EquipmentFlag2"),
            TryReadModelInt(manifest.Character.Fields, "Models.ChestSize", "Models.BoobSize"),
            TryReadInt(manifest.Character.Fields, "Models.ForceKit"),
            bodyModelId);
        return true;
    }

    private static bool TryCreateCharacterBodyOperation(
        GameDumpProfile profile,
        Guid batchEntryId,
        VrCharaManifest manifest,
        ExportCharacterModelOperation modelOperation,
        IReadOnlySet<string> resolvedModelMetadataPaths,
        CharacterAnimationProfile? animationProfile,
        out ExportCharacterBodyOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        if (manifest.Character.Fields.TryGetValue("Models.BodyModelPath", out var bodyPathField)
            && string.IsNullOrWhiteSpace(bodyPathField))
            return true;
        var requestedBodyPath = NormalizeBodyModelPath(manifest.Character.Models?.BodyModelPath);
        var isCustomBody = requestedBodyPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true;
        var uniformPath = manifest.Character.Models?.UniformModelPath?.Replace('\\', '/').TrimStart('/');
        var isCustomUniform = uniformPath?.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true;
        var headPath = manifest.Character.Models?.HeadModelPath?.Replace('\\', '/').TrimStart('/');
        var faceSkeletonPath = isCustomUniform && headPath?.StartsWith("_face/99_CUSTOM/", StringComparison.OrdinalIgnoreCase) == true
            ? Path.ChangeExtension(headPath, ".objbin")
            : null;
        var uniformSkeletonPath = isCustomUniform ? Path.ChangeExtension(uniformPath, ".objbin") : null;
        var authoredPaths = manifest.Resources
            .Select(resource => resource.VirtualPath.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        authoredPaths.UnionWith(resolvedModelMetadataPaths);
        var hasUniformSkeleton = uniformSkeletonPath is not null
            && authoredPaths.Contains($"common/chr/{Path.ChangeExtension(uniformPath, ".g4sk")}");
        var hasUniformObjectConfig = uniformSkeletonPath is not null
            && authoredPaths.Contains($"common/chr/{uniformSkeletonPath}");
        var hasFaceG4sk = faceSkeletonPath is not null
            && authoredPaths.Contains($"common/chr/{Path.ChangeExtension(headPath, ".g4sk")}");
        var hasFaceObjectConfig = faceSkeletonPath is not null
            && authoredPaths.Contains($"common/chr/{faceSkeletonPath}")
            && authoredPaths.Contains($"common/chr/{Path.ChangeExtension(headPath, ".clobin")}");
        var useFaceSkeleton = hasFaceG4sk && hasFaceObjectConfig
            && animationProfile is not null
            && string.Equals(NormalizeBodyModelPath(animationProfile.ModelPath),
                NormalizeBodyModelPath(headPath), StringComparison.OrdinalIgnoreCase);
        var useUniformSkeleton = hasUniformSkeleton && !useFaceSkeleton;
        var skeletonPath = useUniformSkeleton ? uniformSkeletonPath : faceSkeletonPath;
        var hasCustomSkeleton = useUniformSkeleton || (hasFaceG4sk && hasFaceObjectConfig);
        if (isCustomUniform && !useUniformSkeleton && hasFaceG4sk && !hasFaceObjectConfig)
        {
            diagnostic = new Diagnostic(
                "export.custom_skeleton_object_config_missing",
                DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has a custom G4SK but its .objbin/.clobin companions are missing.",
                "Export the character through G4_Blender so its custom skeleton object config is included.");
            return false;
        }
        if (!isCustomBody && !hasCustomSkeleton)
            return true;

        var bodyModelId = TryReadInt(manifest.Character.Fields, "Advanced.BodyModelId")
            ?? TryReadInt(manifest.Character.Fields, "Models.UniformModel");
        if (bodyModelId is null)
        {
            diagnostic = new Diagnostic(
                "export.custom_body_id_missing",
                DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has a custom body model but no original body model ID.",
                "Preserve Advanced.BodyModelId or the original Models.UniformModel ID when authoring a new body model.");
            return false;
        }

        var tablePath = Path.Combine(
            profile.RootPath,
            modelOperation.ModelTablePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
            var existing = document.Entries
                .Where(entry => entry.Name == "CHARA_BODY_INFO" && entry.Values.Count >= 7)
                .Where(entry => GetCfgInteger(entry.Values[0]) == bodyModelId.Value)
                .ToArray();
            if (existing.Length > 1)
            {
                diagnostic = new Diagnostic(
                    "export.custom_body_id_ambiguous",
                    DiagnosticSeverity.Error,
                    $"Custom body model ID {bodyModelId} already resolves to multiple CHARA_BODY_INFO rows.",
                    "Use a unique original body model ID from the source model registry.");
                return false;
            }
            if (existing.Length == 1 && isCustomBody && !hasCustomSkeleton)
            {
                var existingPath = NormalizeBodyModelPath(existing[0].Values[2].Value as string);
                if (string.Equals(existingPath, requestedBodyPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var sourceBodyModelId = hasCustomSkeleton && !isCustomBody
                ? TryReadInt(manifest.Character.Fields, "Advanced.BodyModelId")
                : null;
            var sourceBody = sourceBodyModelId is { } originalBodyModelId
                ? document.Entries.SingleOrDefault(entry =>
                    entry.Name == "CHARA_BODY_INFO"
                    && entry.Values.Count >= 7
                    && GetCfgInteger(entry.Values[0]) == originalBodyModelId)
                : null;
            if (sourceBody is null)
            {
                var sourceModel = document.Entries.SingleOrDefault(entry =>
                    entry.Name == "CHARA_MODEL_INFO"
                    && entry.Values.Count >= 5
                    && GetCfgInteger(entry.Values[0]) == modelOperation.SourceModelId);
                sourceBodyModelId = sourceModel is null ? null : checked((int)GetCfgInteger(sourceModel.Values[4]));
                sourceBody = sourceBodyModelId is { } templateBodyModelId
                    ? document.Entries.SingleOrDefault(entry =>
                        entry.Name == "CHARA_BODY_INFO"
                        && entry.Values.Count >= 7
                        && GetCfgInteger(entry.Values[0]) == templateBodyModelId)
                    : null;
            }
            if (sourceBody is null || sourceBodyModelId is null)
            {
                diagnostic = new Diagnostic(
                    "export.custom_body_template_unresolved",
                    DiagnosticSeverity.Error,
                    $"The source body row for model {modelOperation.SourceModelId} could not be resolved.",
                    "Use a dump with a complete CHARA_MODEL_INFO to CHARA_BODY_INFO chain.");
                return false;
            }
            var sourceBodyId = sourceBodyModelId.Value;
            if (sourceBodyId < int.MinValue || sourceBodyId > int.MaxValue)
            {
                diagnostic = new Diagnostic(
                    "export.custom_body_template_unresolved",
                    DiagnosticSeverity.Error,
                    $"The source body row for model {modelOperation.SourceModelId} could not be resolved.",
                    "Use a dump with a complete CHARA_MODEL_INFO to CHARA_BODY_INFO chain.");
                return false;
            }
            var templateBodyPath = NormalizeBodyModelPath(sourceBody.Values[2].Value as string);
            if (!isCustomBody && templateBodyPath is null)
            {
                diagnostic = new Diagnostic(
                    "export.custom_body_template_unresolved",
                    DiagnosticSeverity.Error,
                    $"The source body path for model {modelOperation.SourceModelId} is invalid.",
                    "Use a dump with a complete CHARA_MODEL_INFO to CHARA_BODY_INFO chain.");
                return false;
            }
            if (hasCustomSkeleton && sourceBody.Values[1].Type != CfgBinValueType.String)
            {
                diagnostic = new Diagnostic(
                    "export.custom_skeleton_template_invalid",
                    DiagnosticSeverity.Error,
                    $"The source skeleton path for model {modelOperation.SourceModelId} is invalid.",
                    "Use a dump with a valid CHARA_BODY_INFO skeleton path.");
                return false;
            }

            if (isCustomBody)
            {
                var commonPath = $"common/chr/{requestedBodyPath}";
                var stem = commonPath[..^Path.GetExtension(commonPath).Length];
                var expectedResources = new[]
                {
                    commonPath,
                    $"{stem}.g4mg",
                    $"dx11/chr/{stem["common/chr/".Length..]}.g4tx",
                };
                if (expectedResources.Any(resource => !authoredPaths.Contains(resource)))
                {
                    diagnostic = new Diagnostic(
                        "export.custom_body_model_family_incomplete",
                        DiagnosticSeverity.Error,
                        $"Character '{manifest.Character.SymbolicId}' has an incomplete custom body model family.",
                        "Embed matching G4MD, G4MG and DX11 G4TX body resources.");
                    return false;
                }
            }

            var bodyModelPath = isCustomBody
                ? requestedBodyPath!
                : isCustomUniform && hasCustomSkeleton
                    ? NormalizeBodyModelPath(uniformPath)
                    : templateBodyPath;
            if (string.IsNullOrWhiteSpace(bodyModelPath))
            {
                diagnostic = new Diagnostic(
                    "export.custom_body_template_unresolved",
                    DiagnosticSeverity.Error,
                    $"The body model path for character '{manifest.Character.SymbolicId}' is invalid.",
                    "Provide a valid custom uniform G4MD or use a dump with a complete character model chain.");
                return false;
            }

            var skeletonTemplatePath = useUniformSkeleton && !hasUniformObjectConfig
                ? Path.Combine(
                    profile.RootPath,
                    "common/chr",
                    ((string)sourceBody.Values[1].Value!).TrimStart('/').Replace('/', Path.DirectorySeparatorChar))
                : null;
            if (useUniformSkeleton && !hasUniformObjectConfig && string.IsNullOrWhiteSpace(skeletonTemplatePath))
            {
                diagnostic = new Diagnostic(
                    "export.custom_uniform_skeleton_template_missing",
                    DiagnosticSeverity.Error,
                    $"The source body row for character '{manifest.Character.SymbolicId}' has no skeleton object-config path.",
                    "Use a dump with a valid source CHARA_BODY_INFO skeleton path.");
                return false;
            }
            operation = new ExportCharacterBodyOperation(
                batchEntryId,
                modelOperation.ModelTablePath,
                checked((int)sourceBodyId),
                bodyModelId.Value,
                bodyModelPath,
                hasCustomSkeleton ? skeletonPath : null,
                SkeletonTemplatePath: skeletonTemplatePath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostic = new Diagnostic(
                "export.custom_body_registry_unreadable",
                DiagnosticSeverity.Error,
                $"The character model table could not be read while registering the custom body: {exception.Message}",
                "Select a compatible, readable dump before exporting.");
            return false;
        }
    }

    private static bool TryCreateCharacterClothesOperation(
        GameDumpProfile profile,
        Guid batchEntryId,
        VrCharaManifest manifest,
        out ExportCharacterClothesOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        var path = manifest.Character.Models?.UniformModelPath?.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase))
            return true;

        var expectedModel = $"common/chr/{path}";
        var stem = expectedModel[..^Path.GetExtension(expectedModel).Length];
        var authored = manifest.Resources.Select(resource => resource.VirtualPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedResources = new[]
        {
            expectedModel,
            $"{stem}.g4mg",
            $"dx11/{stem["common/".Length..]}.g4tx",
        };
        if (expectedResources.Any(resource => !authored.Contains(resource)))
        {
            diagnostic = new Diagnostic(
                "export.custom_uniform_family_incomplete",
                DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has an incomplete custom uniform family.",
                "Embed matching G4MD, G4MG and DX11 G4TX uniform resources.");
            return false;
        }

        const string laytonResourceKey = "u05029710";
        var preferredResourceKey = GetCustomUniformResourceKey(path);
        // Custom uniforms are not shop uniforms.  Start from Layton's special
        // clothes row so character-specific costume metadata from a regular
        // source uniform cannot make the new character depend on the source
        // character's CHARA_COSTUME entry.
        var template = FindUniformTemplate(profile.RootPath, laytonResourceKey);
        if (template is null && !string.Equals(preferredResourceKey, laytonResourceKey, StringComparison.Ordinal))
            template = FindUniformTemplate(profile.RootPath, preferredResourceKey);
        if (template is null)
        {
            diagnostic = new Diagnostic(
                "export.custom_uniform_template_unresolved",
                DiagnosticSeverity.Error,
                $"No unique source uniform template was found for '{preferredResourceKey ?? laytonResourceKey}'.",
                "Select a dump containing the source uniform family or the verified Layton fallback.");
            return false;
        }

        operation = new ExportCharacterClothesOperation(
            batchEntryId,
            Path.GetRelativePath(profile.RootPath, template.Value.TablePath).Replace(Path.DirectorySeparatorChar, '/'),
            template.Value.ModelId,
            0,
            template.Value.ResourceKey,
            template.Value.ResourceKey,
            $"_uniform/{template.Value.ResourceKey}/{template.Value.ResourceKey}.g4md",
            $"_uniform/{template.Value.ResourceKey}/{template.Value.ResourceKey}.g4tx",
            FindUniformInfoType(profile.RootPath, TryReadInt(manifest.Character.Fields, "Models.UniformModel"))
                ?? template.Value.ClothesInfoType);
        return true;
    }

    private static bool TryCreateCharacterShoesOperation(
        GameDumpProfile profile,
        Guid batchEntryId,
        VrCharaManifest manifest,
        out ExportCharacterClothesOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        var path = manifest.Character.Models?.ShoesModelPath?.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase))
            return true;

        var model = $"common/chr/{path}";
        var stem = model[..^Path.GetExtension(model).Length];
        var authored = manifest.Resources.Select(resource => resource.VirtualPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (new[] { model, $"{stem}.g4mg", $"dx11/{stem["common/".Length..]}.g4tx" }.Any(resource => !authored.Contains(resource)))
        {
            diagnostic = new Diagnostic("export.custom_shoes_family_incomplete", DiagnosticSeverity.Error,
                $"Character '{manifest.Character.SymbolicId}' has an incomplete custom shoes family.",
                "Embed matching G4MD, G4MG and DX11 G4TX shoes resources.");
            return false;
        }

        var sourceId = TryReadInt(manifest.Character.Fields, "Models.ShoesModel");
        var template = FindShoesTemplate(profile.RootPath, sourceId);
        if (template is null)
        {
            diagnostic = new Diagnostic("export.custom_shoes_template_unresolved", DiagnosticSeverity.Error,
                "No compatible CHARA_PARTS_SHOES_MODEL template was found in the selected dump.",
                "Select a dump containing a linked shoes model and info row.");
            return false;
        }
        operation = new ExportCharacterClothesOperation(
            batchEntryId,
            Path.GetRelativePath(profile.RootPath, template.Value.TablePath).Replace(Path.DirectorySeparatorChar, '/'),
            template.Value.ModelId,
            0,
            template.Value.ResourceKey,
            template.Value.ResourceKey,
            template.Value.ModelPath,
            template.Value.TexturePath,
            template.Value.InfoType,
            IsShoes: true,
            SourceModelPath: template.Value.ModelPath,
            CustomModelPath: path,
            SourceInfoIndex: template.Value.InfoIndex);
        return true;
    }

    private static (string TablePath, int ModelId, string ResourceKey, string ModelPath, string TexturePath, int InfoType, int InfoIndex)?
        FindShoesTemplate(string rootPath, int? preferredId)
    {
        foreach (var tablePath in ExpandDumpPattern(rootPath, "common/gamedata/character/chara_parts_*.cfg.bin")
                     .OrderByDescending(path => new FileInfo(path).Length))
        {
            try
            {
                var document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
                var infos = document.Entries.Where(entry => entry.Name == "CHARA_PARTS_SHOES_INFO").ToArray();
                var models = document.Entries.Where(entry => entry.Name == "CHARA_PARTS_SHOES_MODEL"
                    && entry.Values.Count >= 1
                    && (preferredId is null || GetCfgInteger(entry.Values[0]) == preferredId.Value));
                foreach (var model in models)
                {
                    var reference = document.Entries.SingleOrDefault(entry => entry.Index == model.Index + 1
                        && entry.Name == "CHARA_PARTS_SHOES_MODEL_REF_INFO" && entry.Values.Count > 0);
                    if (reference is null) continue;
                    var index = checked((int)GetCfgInteger(reference.Values[0]));
                    if ((uint)index >= (uint)infos.Length || infos[index].Values.Count < 3) continue;
                    var info = infos[index];
                    if (info.Values[0].Value is not string modelPath || info.Values[1].Value is not string texturePath) continue;
                    var resourceKey = modelPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)
                        ?? throw new InvalidDataException("The shoe model path has no resource folder.");
                    return (tablePath, checked((int)GetCfgInteger(model.Values[0])),
                        resourceKey,
                        modelPath, texturePath, checked((int)GetCfgInteger(info.Values[2])), index);
                }
            }
            catch (InvalidDataException)
            {
                // Other versioned parts tables may use an unsupported container.
            }
        }
        return preferredId is null ? null : FindShoesTemplate(rootPath, null);
    }

    private static (string TablePath, int ModelId, string ResourceKey, int ClothesInfoType)? FindUniformTemplate(
        string rootPath,
        string? resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey)) return null;
        var matches = new List<(string TablePath, int ModelId, string ResourceKey, int ClothesInfoType)>();
        foreach (var path in ExpandDumpPattern(rootPath, "common/gamedata/character/chara_parts_*.cfg.bin"))
        {
            try
            {
                var document = CfgBinDocument.Read(File.ReadAllBytes(path));
                var modelRows = document.Entries
                    .Where(entry => entry.Name == "CHARA_PARTS_CLOTHES_MODEL"
                        && entry.Values.Count >= 2
                        && string.Equals(entry.Values[1].Value as string, resourceKey, StringComparison.Ordinal))
                    .ToArray();
                var infoPath = $"_uniform/{resourceKey}/{resourceKey}.g4md";
                var info = document.Entries.SingleOrDefault(entry => entry.Name == "CHARA_PARTS_CLOTHES_INFO"
                        && entry.Values.Count >= 22
                        && string.Equals(entry.Values[0].Value as string, infoPath, StringComparison.Ordinal));
                if (info is null)
                    continue;
                foreach (var modelRow in modelRows)
                    matches.Add((path, checked((int)GetCfgInteger(modelRow.Values[0])), resourceKey,
                        checked((int)GetCfgInteger(info.Values[2]))));
            }
            catch (InvalidDataException)
            {
                // Other versioned clothes tables may be RDBNP or malformed;
                // the normal table resolver will report a missing template if
                // no compatible source survives this scan.
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static int? FindUniformInfoType(string rootPath, int? uniformModelId)
    {
        if (uniformModelId is null) return null;
        var matches = new HashSet<int>();
        foreach (var path in ExpandDumpPattern(rootPath, "common/gamedata/character/chara_parts_*.cfg.bin"))
        {
            try
            {
                var document = CfgBinDocument.Read(File.ReadAllBytes(path));
                var infoRows = document.Entries.Where(entry => entry.Name == "CHARA_PARTS_CLOTHES_INFO").ToArray();
                foreach (var model in document.Entries.Where(entry => entry.Name == "CHARA_PARTS_CLOTHES_MODEL"
                             && entry.Values.Count >= 2
                             && GetCfgInteger(entry.Values[0]) == uniformModelId.Value))
                {
                    var reference = document.Entries.SingleOrDefault(entry => entry.Index == model.Index + 1
                        && entry.Name == "CHARA_PARTS_CLOTHES_MODEL_REF_INFO"
                        && entry.Values.Count >= 1);
                    if (reference is null) continue;
                    var infoIndex = checked((int)GetCfgInteger(reference.Values[0]));
                    if ((uint)infoIndex >= (uint)infoRows.Length || infoRows[infoIndex].Values.Count < 3) continue;
                    matches.Add(checked((int)GetCfgInteger(infoRows[infoIndex].Values[2])));
                }
            }
            catch (InvalidDataException)
            {
                // Other versioned clothes tables may be RDBNP or malformed.
            }
        }
        return matches.Count == 1 ? matches.Single() : null;
    }

    private static string? GetCustomUniformResourceKey(string path)
    {
        const string marker = "_uniform/99_CUSTOM/";
        var normalized = path.Replace('\\', '/');
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        var suffix = normalized[(markerIndex + marker.Length)..];
        var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        var key = segments[0];
        return key.StartsWith("c", StringComparison.OrdinalIgnoreCase)
            ? $"u{key[1..]}"
            : key;
    }

    private static void AddNativeCustomUniformObjectMetadata(
        GameDumpProfile profile,
        Guid batchEntryId,
        VrCharaManifest manifest,
        ICollection<ExportModelDependencyOperation> dependencies)
    {
        var path = manifest.Character.Models?.UniformModelPath?.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("_uniform/99_CUSTOM/", StringComparison.OrdinalIgnoreCase))
            return;

        var resourceKey = GetCustomUniformResourceKey(path);
        if (string.IsNullOrWhiteSpace(resourceKey)) return;
        var authored = manifest.Resources
            .Select(resource => resource.VirtualPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceDirectory = Path.Combine(
            profile.RootPath,
            "common",
            "chr",
            "_uniform",
            resourceKey);
        if (!Directory.Exists(sourceDirectory)) return;

        foreach (var sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     $"{resourceKey}_*",
                     SearchOption.TopDirectoryOnly)
                 .Where(path => Path.GetExtension(path).Equals(".objbin", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var packageResourcePath = $"common/chr/_uniform/99_CUSTOM/{resourceKey}/{Path.GetFileName(sourcePath)}";
            if (authored.Contains(packageResourcePath)) continue;
            dependencies.Add(new ExportModelDependencyOperation(
                batchEntryId,
                ModelDependencyKind.PackagePart,
                packageResourcePath,
                sourcePath));
        }
    }

    private static bool TryResolveBodyModelId(
        string tablePath,
        int bodyType,
        int? bodyGroup,
        int? bodyPoseType,
        int? preferredBodyModelId,
        out int? bodyModelId,
        out Diagnostic? diagnostic)
    {
        bodyModelId = null;
        diagnostic = null;
        CfgBinDocument document;
        try
        {
            document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostic = new Diagnostic(
                "export.body_type_table_unreadable", DiagnosticSeverity.Error,
                $"The character model table could not be read while resolving body type {bodyType}: {exception.Message}",
                "Select a compatible, readable dump before exporting.");
            return false;
        }

        var candidatesByType = document.Entries
            .Where(entry => entry.Name == "CHARA_BODY_INFO" && entry.Values.Count >= 7)
            .Where(entry => GetCfgInteger(entry.Values[5]) == bodyType)
            .ToArray();
        var candidates = candidatesByType
            .Where(entry => bodyGroup is null || GetCfgInteger(entry.Values[4]) == bodyGroup.Value)
            .Where(entry => bodyPoseType is null || GetCfgInteger(entry.Values[6]) == bodyPoseType.Value)
            .ToArray();
        if (candidates.Length != 1 && preferredBodyModelId is { } preferred)
        {
            var preferredCandidate = candidatesByType
                .Where(entry => GetCfgInteger(entry.Values[0]) == preferred)
                .ToArray();
            if (preferredCandidate.Length == 1)
                candidates = preferredCandidate;
        }
        if (candidates.Length != 1 && candidatesByType.Length == 1)
            candidates = candidatesByType;
        if (candidates.Length == 0)
        {
            var runtimeCandidates = candidatesByType
                .Where(IsRuntimeBodyRecord)
                .ToArray();
            if (runtimeCandidates.Length > 0)
            {
                var groupedRuntimeCandidates = runtimeCandidates
                    .Where(entry => bodyGroup is null || GetCfgInteger(entry.Values[4]) == bodyGroup.Value)
                    .ToArray();
                candidates = groupedRuntimeCandidates.Length > 0
                    ? groupedRuntimeCandidates
                    : runtimeCandidates;
            }
        }
        if (candidates.Length > 1)
        {
            var runtimeCandidates = candidates
                .Where(IsRuntimeBodyRecord)
                .OrderBy(entry => entry.Index)
                .ToArray();
            if (runtimeCandidates.Length > 0)
                candidates = [runtimeCandidates[0]];
        }
        if (candidates.Length != 1)
        {
            diagnostic = new Diagnostic(
                candidates.Length == 0 ? "export.body_type_unresolved" : "export.body_type_ambiguous",
                DiagnosticSeverity.Error,
                $"Body type {bodyType} does not resolve to exactly one compatible body record ({candidates.Length} found).",
                "Keep the source body group and pose, or choose a body type with one verified record.");
            return false;
        }

        var value = GetCfgInteger(candidates[0].Values[0]);
        if (value == long.MinValue || value < int.MinValue || value > int.MaxValue)
        {
            diagnostic = new Diagnostic(
                "export.body_type_reference_invalid", DiagnosticSeverity.Error,
                $"Body type {bodyType} resolved to an invalid body model reference.",
                "Use a compatible dump whose CHARA_BODY_INFO records have valid IDs.");
            return false;
        }
        bodyModelId = (int)value;
        return true;
    }

    private static bool TryResolveBodyModelPath(
        string tablePath,
        string bodyModelPath,
        out int? bodyModelId,
        out Diagnostic? diagnostic)
    {
        bodyModelId = null;
        diagnostic = null;
        CfgBinDocument document;
        try
        {
            document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostic = new Diagnostic(
                "export.body_model_table_unreadable", DiagnosticSeverity.Error,
                $"The character model table could not be read while resolving body model '{bodyModelPath}': {exception.Message}",
                "Select a compatible, readable dump before exporting.");
            return false;
        }

        var candidates = document.Entries
            .Where(entry => entry.Name == "CHARA_BODY_INFO" && entry.Values.Count >= 7)
            .Where(entry => string.Equals(
                NormalizeBodyModelPath(entry.Values[2].Value as string),
                bodyModelPath,
                StringComparison.OrdinalIgnoreCase))
            .Where(IsRuntimeBodyRecord)
            .ToArray();
        if (candidates.Length == 0)
        {
            diagnostic = new Diagnostic(
                "export.body_model_unresolved", DiagnosticSeverity.Error,
                $"Body model path '{bodyModelPath}' does not resolve to a runtime CHARA_BODY_INFO record.",
                "Choose a body model present in the selected dump or keep the source body model.");
            return false;
        }

        var value = GetCfgInteger(candidates[0].Values[0]);
        if (value == long.MinValue || value < int.MinValue || value > int.MaxValue)
        {
            diagnostic = new Diagnostic(
                "export.body_model_reference_invalid", DiagnosticSeverity.Error,
                $"Body model path '{bodyModelPath}' resolves to an invalid body model reference.",
                "Use a compatible dump whose CHARA_BODY_INFO records have valid IDs.");
            return false;
        }

        bodyModelId = (int)value;
        return true;
    }

    private static string? TryReadBodyModelPath(string tablePath, int bodyModelId)
    {
        CfgBinDocument document;
        try
        {
            document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }

        var entry = document.Entries.FirstOrDefault(candidate =>
            candidate.Name == "CHARA_BODY_INFO"
            && candidate.Values.Count >= 7
            && GetCfgInteger(candidate.Values[0]) == bodyModelId);
        return NormalizeBodyModelPath(entry?.Values[2].Value as string);
    }

    private static string? NormalizeBodyModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["common/chr/".Length..];
        else if (normalized.StartsWith("chr/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["chr/".Length..];
        return normalized;
    }

    private static void ValidateAnimationRigProfile(
        string dumpRoot,
        VrCharaManifest manifest,
        CharacterMotionFamily family,
        CharacterAnimationProfile animationProfile)
    {
        if (string.IsNullOrWhiteSpace(family.BodySkeletonResourcePath))
            throw new InvalidDataException("The source character has no resolved body skeleton resource.");

        var bodySkeletonPath = Path.GetFullPath(Path.Combine(dumpRoot,
            family.BodySkeletonResourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var commonCharacterRoot = Path.GetFullPath(Path.Combine(dumpRoot, "common", "chr"))
            + Path.DirectorySeparatorChar;
        if (!bodySkeletonPath.StartsWith(commonCharacterRoot, StringComparison.Ordinal)
            || !File.Exists(bodySkeletonPath))
            throw new InvalidDataException($"The assigned body skeleton '{family.BodySkeletonResourcePath}' is unavailable.");

        var assignedSkeletonHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bodySkeletonPath)))
            .ToLowerInvariant();
        if (!assignedSkeletonHash.Equals(animationProfile.SourceRig.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Animation source rig hash does not match the body's assigned G4SK '{family.BodySkeletonResourcePath}'.");

        var modelPath = NormalizeBodyModelPath(animationProfile.ModelPath);
        if (modelPath is null)
            throw new InvalidDataException("The animation profile has no custom model path.");
        var targetSkeletonPath = Path.ChangeExtension($"common/chr/{modelPath}", ".g4sk")
            .Replace('\\', '/');
        var targetSkeleton = manifest.Resources.SingleOrDefault(resource =>
            resource.VirtualPath.Equals(targetSkeletonPath, StringComparison.OrdinalIgnoreCase));
        if (targetSkeleton is null)
        {
            if (!animationProfile.TargetRig.Sha256.Equals(assignedSkeletonHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The animation profile changes the target G4SK but the custom model package has no matching G4SK resource.");
            return;
        }

        if (!targetSkeleton.Checksum.Equals(animationProfile.TargetRig.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The animation target rig hash does not match the packaged custom G4SK.");
    }

    private static bool IsRuntimeBodyRecord(CfgBinEntry entry) =>
        entry.Values[1].Value is string path
        && !path.StartsWith("_face/20_EDIT/", StringComparison.OrdinalIgnoreCase);

    private static long GetCfgInteger(CfgBinValue value) => value.Value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };

    private static uint? ParseSkinColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 8
            || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            return null;
        return parsed;
    }

    private static bool TryCreateCharacterDeliveryOperation(
        GameDumpProfile profile,
        Guid batchEntryId,
        int variantIndex,
        out ExportCharacterDeliveryOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        var tables = ExpandDumpPattern(profile.RootPath, DeliveryConfigPattern).ToArray();
        if (tables.Length != 1)
        {
            diagnostic = new Diagnostic(
                "export.delivery_table_ambiguous", DiagnosticSeverity.Error,
                $"Expected one compatible delivery configuration table, found {tables.Length}.",
                "Select a compatible dump with one delivery configuration table.");
            return false;
        }
        operation = new ExportCharacterDeliveryOperation(
            batchEntryId,
            Path.GetRelativePath(profile.RootPath, tables[0]).Replace(Path.DirectorySeparatorChar, '/'),
            variantIndex);
        return true;
    }

    private static IReadOnlyList<ExportDeliveryLocalizationOperation> CreateDeliveryLocalizationOperations(
        string rootPath,
        Guid batchEntryId)
    {
        return ExpandDumpPattern(rootPath, DeliveryTextPattern)
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/');
                var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var locale = segments.Length >= 3 ? segments[^2] : "en";
                return new ExportDeliveryLocalizationOperation(
                    batchEntryId,
                    locale,
                    relativePath,
                    ResolveDeliveryTitle(locale));
            })
            .OrderBy(operation => operation.Locale, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDeliveryTitle(string locale) => locale.ToLowerInvariant() switch
    {
        "de" => "Geschenk von Victory Tool",
        "en" => "Victory Tool Gift",
        "es" => "Regalo de Victory Tool",
        "fr" => "Cadeau de Victory Tool",
        "it" => "Regalo di Victory Tool",
        "ja" => "Victory Toolからの贈り物",
        "pt" => "Presente do Victory Tool",
        "zh_hans" => "Victory Tool赠礼",
        "zh_hant" => "Victory Tool贈禮",
        _ => "Victory Tool Gift",
    };

    private static bool ContainsModel(string path, int modelId)
    {
        try
        {
            return CfgBinDocument.Read(File.ReadAllBytes(path)).Entries.Any(entry =>
                entry.Name == "CHARA_MODEL_INFO"
                && entry.Values.Count >= 34
                && entry.Values[0].Value is int value
                && value == modelId);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryFindLaytonModelId(
        string tablePath,
        out int modelId,
        out Diagnostic? diagnostic)
    {
        modelId = default;
        diagnostic = null;
        try
        {
            var document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
            const string laytonFaceModelPath = "_face/05_GO2/c05029710/c05029710.g4md";
            const int laytonUniformModelId = -1330391849;
            var matches = document.Entries
                .Where(entry => entry.Name == "CHARA_MODEL_INFO"
                    && entry.Values.Count >= 11
                    && string.Equals(entry.Values[10].Value as string, laytonFaceModelPath, StringComparison.Ordinal))
                .Where(entry => GetCfgInteger(entry.Values[5]) == laytonUniformModelId)
                .ToArray();
            if (matches.Length == 1)
            {
                var value = GetCfgInteger(matches[0].Values[0]);
                if (value >= int.MinValue && value <= int.MaxValue)
                {
                    modelId = (int)value;
                    return true;
                }
            }
            diagnostic = new Diagnostic(
                "export.layton_model_template_unresolved",
                DiagnosticSeverity.Error,
                $"The Layton character model template could not be resolved uniquely in '{Path.GetFileName(tablePath)}'.",
                "Use a dump containing the verified Hershel Layton CHARA_MODEL_INFO row.");
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostic = new Diagnostic(
                "export.layton_model_template_unreadable",
                DiagnosticSeverity.Error,
                $"The character model table could not be read while resolving the Layton template: {exception.Message}",
                "Select a compatible, readable dump before exporting.");
            return false;
        }
    }

    private static bool TryReadField(
        IReadOnlyDictionary<string, string?> fields,
        string key,
        out int value)
    {
        value = default;
        return fields.TryGetValue(key, out var text)
               && int.TryParse(text, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryCreateShopCharacterOperation(
        GameDumpProfile profile,
        BatchEntry entry,
        VictoryTool.Application.Characters.CharacterDraft character,
        out ExportShopCharacterOperation? operation,
        out Diagnostic? diagnostic)
    {
        operation = null;
        diagnostic = null;
        var acquisition = entry.Acquisition;
        if (acquisition?.ShopSourceItemId is not { } sourceItemId
            || acquisition.ShopRarity is not { } rarity
            || acquisition.ShopSpecialVariant is not { } specialVariant
            || !TryReadField(character.Fields, "Gameplay.ParameterId", out var sourceParameterId))
        {
            diagnostic = new Diagnostic(
                "export.shop_configuration_required",
                DiagnosticSeverity.Error,
                $"Character '{character.SymbolicId}' has no complete typed shop acquisition configuration.",
                "Select a source shop item, rarity and special-variant value in the project batch settings.");
            return false;
        }
        if (rarity is < 0 or > 8 || specialVariant is < 0 or > 1)
        {
            diagnostic = new Diagnostic(
                "export.shop_configuration_invalid",
                DiagnosticSeverity.Error,
                $"Character '{character.SymbolicId}' has an out-of-range shop rarity or variant value.",
                "Use a rarity from 0 through 8 and a special-variant value of 0 or 1.");
            return false;
        }

        var matches = ExpandDumpPattern(profile.RootPath, ShopConfigPattern).ToArray();
        if (matches.Length != 1)
        {
            diagnostic = new Diagnostic(
                "export.shop_table_ambiguous",
                DiagnosticSeverity.Error,
                $"Expected one compatible shop configuration, found {matches.Length}.",
                "Select a compatible dump containing exactly one active shop_config CFGBIN.");
            return false;
        }
        operation = new ExportShopCharacterOperation(
            entry.Id,
            Path.GetRelativePath(profile.RootPath, matches[0]).Replace(Path.DirectorySeparatorChar, '/'),
            sourceItemId,
            sourceParameterId,
            rarity,
            specialVariant,
            acquisition.IsFree,
            acquisition.ShopSourceParameterId);
        return true;
    }

    private static ExportPlan AddFailure(
        ExportPlan plan,
        string code,
        string message,
        string recoveryAction) =>
        plan with
        {
            Diagnostics = plan.Diagnostics.Append(new Diagnostic(
                code,
                DiagnosticSeverity.Error,
                message,
                recoveryAction)).ToArray(),
        };

    private static void AddDependencyDiagnostics(
        GameDumpProfile profile,
        ExportPlatform platform,
        IReadOnlyList<ExportPackagePlan> packages,
        IReadOnlyList<ExportResourceOperation> resources,
        IReadOnlyList<ExportGameReferenceOperation> gameReferences,
        IReadOnlyList<ExportPatchOperation> patches,
        IReadOnlyList<ExportLocalizationOperation> localizations,
        ICollection<Diagnostic> diagnostics)
    {
        var hasPlatformResources = platform switch
        {
            ExportPlatform.Pc => profile.HasPcResources,
            ExportPlatform.Switch => profile.HasSwitchResources,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
        if (!hasPlatformResources)
        {
            diagnostics.Add(new Diagnostic(
                "export.platform_resources_missing",
                DiagnosticSeverity.Error,
                $"The selected dump does not contain resources for the {platform} export platform.",
                "Choose a platform present in the dump or select a compatible dump."));
        }

        foreach (var conflict in packages
                     .GroupBy(package => package.SymbolicId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(package => package.BatchEntryId).Distinct().Skip(1).Any()))
        {
            diagnostics.Add(new Diagnostic(
                "export.symbolic_id_conflict",
                DiagnosticSeverity.Error,
                $"Multiple enabled packages use the symbolic character ID '{conflict.Key}'.",
                "Give every character package a unique symbolic ID or disable one of the duplicates."));
        }

        foreach (var conflict in resources
                     .GroupBy(resource => resource.DestinationPath, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(resource => resource.BatchEntryId).Distinct().Skip(1).Any()))
        {
            diagnostics.Add(new Diagnostic(
                "export.resource_destination_conflict",
                DiagnosticSeverity.Error,
                $"Multiple packages write the authored resource '{conflict.Key}'.",
                "Remove the duplicate resource or give each authored resource a unique destination path."));
        }

        foreach (var reference in gameReferences.Where(reference => !reference.Exists))
        {
            diagnostics.Add(new Diagnostic(
                "export.game_reference_missing",
                DiagnosticSeverity.Error,
                $"The dump does not contain the referenced game resource '{reference.VirtualPath}'.",
                "Select a compatible dump or replace the missing game resource reference."));
        }

        foreach (var conflict in patches
                     .GroupBy(
                         patch => $"{patch.TablePath}\0{patch.SymbolicKey}",
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(patch => patch.BatchEntryId).Distinct().Skip(1).Any()))
        {
            var operation = conflict.First();
            diagnostics.Add(new Diagnostic(
                "export.patch_symbol_conflict",
                DiagnosticSeverity.Error,
                $"Multiple packages patch symbolic row '{operation.SymbolicKey}' in '{operation.TablePath}'.",
                "Assign a unique symbolic row key or disable one of the conflicting packages."));
        }

        foreach (var patch in patches
                     .DistinctBy(operation => operation.TablePath, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = patch.TablePath.Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(profile.RootPath, relativePath))) continue;
            diagnostics.Add(new Diagnostic(
                "export.patch_table_missing",
                DiagnosticSeverity.Error,
                $"The dump does not contain the requested patch table '{patch.TablePath}'.",
                "Select a compatible dump or correct the package table dependency."));
        }

        var dumpTextPath = Path.Combine(profile.RootPath, "common", "text");
        if (Directory.Exists(dumpTextPath))
        {
            var dumpLocales = Directory.EnumerateDirectories(dumpTextPath)
                .Where(directory => File.Exists(Path.Combine(directory, "chara_text.cfg.bin")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(locale => !string.IsNullOrWhiteSpace(locale))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                var packageLocales = localizations
                    .Where(operation => operation.BatchEntryId == package.BatchEntryId)
                    .Select(operation => operation.Locale)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var missingLocale in dumpLocales.Where(locale => !packageLocales.Contains(locale)))
                {
                    diagnostics.Add(new Diagnostic(
                        "export.localization_locale_missing",
                        DiagnosticSeverity.Error,
                        $"Package '{package.SymbolicId}' has no localization for dump locale '{missingLocale}'.",
                        "Add the missing localized name and description or define an explicit locale fallback."));
                }
            }
        }
    }

    private static IReadOnlyList<ExportFileOperation> ResolveFileOperations(
        GameDumpProfile profile,
        IReadOnlyList<string> patterns,
        ICollection<Diagnostic> diagnostics)
    {
        var operations = new List<ExportFileOperation>();
        foreach (var pattern in patterns.Where(pattern =>
                     pattern.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)))
        {
            var matches = ExpandDumpPattern(profile.RootPath, pattern).ToArray();
            if (matches.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    "export.required_table_missing",
                    DiagnosticSeverity.Error,
                    $"The dump does not contain a file matching required table pattern '{pattern}'.",
                    "Select a compatible dump or remove the package dependency that requires this table."));
                continue;
            }
            operations.AddRange(matches.Select(path => new ExportFileOperation(
                path,
                Path.GetRelativePath(profile.RootPath, path).Replace(Path.DirectorySeparatorChar, '/'))));
        }
        return operations
            .DistinctBy(operation => operation.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(operation => operation.DestinationPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ExpandDumpPattern(string rootPath, string virtualPattern)
    {
        var segments = virtualPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [Path.GetFullPath(rootPath)];
        for (var index = 0; index < segments.Length; index++)
        {
            var isLast = index == segments.Length - 1;
            var segment = segments[index];
            current = current.SelectMany(path => ExpandSegment(path, segment, isLast)).ToArray();
            if (!current.Any()) break;
        }
        return current.Where(File.Exists);
    }

    private static IEnumerable<string> ExpandSegment(string directory, string pattern, bool isLast)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return isLast
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                : Directory.EnumerateDirectories(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddOutputDiagnostics(
        GameDumpProfile profile,
        string outputPath,
        ICollection<Diagnostic> diagnostics)
    {
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
        {
            diagnostics.Add(new Diagnostic(
                "export.output_must_be_new",
                DiagnosticSeverity.Error,
                "The export output path already exists.",
                "Choose a new empty output path; VictoryTool never overwrites an existing folder."));
        }

        var dumpRoot = Path.GetFullPath(profile.RootPath);
        var output = Path.GetFullPath(outputPath);
        var relative = Path.GetRelativePath(dumpRoot, output);
        if (!Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "export.output_inside_dump",
                DiagnosticSeverity.Error,
                "The export output path is inside the immutable game dump.",
                "Choose a new output folder outside the dump root."));
        }
    }

    private static IReadOnlyList<string> GetAffectedFiles(
        ExportPlatform platform,
        AcquisitionMode acquisition)
    {
        var files = new List<string>
        {
            CharacterBasePattern,
            CharacterParameterPattern,
            "common/gamedata/character/chara_model_*.cfg.bin",
            "common/gamedata/character/chara_model_change_*.cfg.bin",
            "common/gamedata/character/chara_change_*.cfg.bin",
            "common/gamedata/character/add_model_config*.cfg.bin",
            "common/gamedata/character/chara_texture_*.cfg.bin",
            "common/gamedata/character/chara_parts_*.cfg.bin",
            "common/gamedata/character/chara_mesh_change_config*.cfg.bin",
            "common/gamedata/character/chara_mesh_type*.cfg.bin",
            "common/gamedata/character/chara_mesh_mask_config*.cfg.bin",
            "common/gamedata/character/chara_parent_bone_sync_*.cfg.bin",
            "common/gamedata/character/chara_motion_*.cfg.bin",
            "common/gamedata/character/chara_expression_*.cfg.bin",
            "common/gamedata/character/chara_face_*.cfg.bin",
            "common/gamedata/character/chara_costume_*.cfg.bin",
            "common/gamedata/character/chara_cloth_change_*.cfg.bin",
            "common/gamedata/character/chara_scale_*.cfg.bin",
            "common/gamedata/character/uniform_config_*.cfg.bin",
            "common/gamedata/character/chara_add_desc*.cfg.bin",
            CharacterTextPattern,
            CharacterDescriptionTextPattern,
            CharacterRomanizedTextPattern,
            platform switch
            {
                ExportPlatform.Pc => "dx11/menu/200_icon/10_icon_chr/face/*.g4tx",
                ExportPlatform.Switch => "nx/menu/200_icon/10_icon_chr/face/*.g4tx",
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
            },
        };
        switch (acquisition)
        {
            case AcquisitionMode.Shop:
                files.Add(ShopConfigPattern);
                break;
            case AcquisitionMode.Delivery:
                files.Add(DeliveryConfigPattern);
                files.Add(DeliveryTextPattern);
                break;
            case AcquisitionMode.Constellation:
                files.Add(PlayersUniverseConfigPattern);
                break;
            case AcquisitionMode.Both:
                files.Add(ShopConfigPattern);
                files.Add(PlayersUniverseConfigPattern);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(acquisition), acquisition, null);
        }
        return files;
    }

    private static string ResolveCharacterIdKey(CharacterDraft character, Guid batchEntryId)
    {
        foreach (var field in new[] { "Advanced.InternalName", "Identity.InternalName" })
        {
            if (character.Fields.TryGetValue(field, out var stored)
                && IsCustomCharacterName(stored))
                return stored!;
        }

        // The batch-entry ID is persisted in the project file, so this fallback
        // remains stable when the same package is incorporated again. The
        // numeric ID remains the CRC32 of this exact generated name.
        var suffix = ExportIdAllocator.ComputeCrc32(batchEntryId.ToString("N")) % 1_000_000u;
        return $"c99{suffix:D6}";
    }

    private static string ResolvePackageSymbolicId(CharacterDraft character, Guid batchEntryId)
    {
        // Older packages created every clone as character.main. Keep them
        // importable in one batch by deriving a stable package key from the
        // persisted batch entry instead of rejecting the whole batch.
        if (!string.Equals(character.SymbolicId, "character.main", StringComparison.OrdinalIgnoreCase))
            return character.SymbolicId;

        return $"character.{batchEntryId:N}";
    }

    private static bool IsCustomCharacterName(string? value) =>
        value is { Length: 9 }
        && value.StartsWith("c99", StringComparison.Ordinal)
        && value.Skip(3).All(char.IsAsciiDigit);

    private static List<ExportResourceOperation> RewriteCustomResourceNames(
        IEnumerable<ExportResourceOperation> resources,
        IReadOnlyList<ExportIdAssignment> assignments,
        IReadOnlyList<ExportCharacterClothesOperation> equipmentOperations)
    {
        var characterIds = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        return resources.Select(resource =>
        {
            if (!characterIds.TryGetValue(resource.BatchEntryId, out var internalName))
                return resource;
            var sourcePath = resource.PackageResourcePath ?? resource.DestinationPath;
            var customShoesPath = equipmentOperations.SingleOrDefault(operation =>
                operation.BatchEntryId == resource.BatchEntryId && operation.IsShoes)?.CustomModelPath;
            var isCustomShoesResource = customShoesPath is not null
                && sourcePath.Contains(GetCustomModelDirectory(customShoesPath), StringComparison.OrdinalIgnoreCase);
            var destination = RewriteCustomResourcePath(sourcePath, internalName, isCustomShoesResource);
            return string.Equals(destination, sourcePath, StringComparison.Ordinal)
                ? resource
                : resource with { DestinationPath = destination, PackageResourcePath = sourcePath };
        }).ToList();
    }

    private static List<ExportCharacterModelOperation> RewriteCustomModelNames(
        IEnumerable<ExportCharacterModelOperation> operations,
        IReadOnlyList<ExportIdAssignment> assignments)
    {
        var characterIds = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        return operations.Select(operation =>
        {
            if (!characterIds.TryGetValue(operation.BatchEntryId, out var internalName))
                return operation;
            var path = RewriteCustomResourcePath(operation.FaceModelPath, internalName);
            return string.Equals(path, operation.FaceModelPath, StringComparison.Ordinal)
                ? operation
                : operation with { FaceModelPath = path };
        }).ToList();
    }

    private static List<ExportCharacterBodyOperation> RewriteCustomBodyNames(
        IEnumerable<ExportCharacterBodyOperation> operations,
        IReadOnlyList<ExportIdAssignment> assignments)
    {
        var characterIds = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        return operations.Select(operation =>
        {
            if (!characterIds.TryGetValue(operation.BatchEntryId, out var internalName))
                return operation;
            var path = RewriteCustomResourcePath(operation.BodyModelPath, internalName);
            var skeletonPath = operation.SkeletonModelPath is null
                ? null
                : RewriteCustomResourcePath(operation.SkeletonModelPath, internalName);
            return operation with { BodyModelPath = path, SkeletonModelPath = skeletonPath };
        }).ToList();
    }

    private static (List<ExportCharacterBodyOperation> Bodies, List<ExportCharacterModelOperation> Models)
        AllocateCustomBodyModelIds(
            string rootPath,
            IReadOnlyList<ExportCharacterBodyOperation> bodies,
            IReadOnlyList<ExportCharacterModelOperation> models,
            IReadOnlyList<ExportIdAssignment> assignments)
    {
        var characterNames = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        var usedByTable = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var resolvedBodies = new List<ExportCharacterBodyOperation>(bodies.Count);
        var resolvedModels = models.ToList();
        foreach (var body in bodies)
        {
            if (!usedByTable.TryGetValue(body.ModelTablePath, out var used))
            {
                var tablePath = Path.Combine(rootPath, body.ModelTablePath.Replace('/', Path.DirectorySeparatorChar));
                used = CfgBinDocument.Read(File.ReadAllBytes(tablePath)).Entries
                    .Where(entry => entry.Name == "CHARA_BODY_INFO" && entry.Values.Count > 0)
                    .Select(entry => checked((int)GetCfgInteger(entry.Values[0])))
                    .ToHashSet();
                usedByTable.Add(body.ModelTablePath, used);
            }

            var characterName = characterNames[body.BatchEntryId];
            var probe = 0u;
            int bodyModelId;
            do
            {
                var key = probe == 0 ? $"{characterName}.body" : $"{characterName}.body#{probe}";
                bodyModelId = unchecked((int)(CharacterMotionInfoT2bWriter.Crc32(key) | 0x80000000u));
                probe++;
            }
            while (!used.Add(bodyModelId));

            resolvedBodies.Add(body with { BodyModelId = bodyModelId });
            for (var index = 0; index < resolvedModels.Count; index++)
            {
                if (resolvedModels[index].BatchEntryId == body.BatchEntryId)
                    resolvedModels[index] = resolvedModels[index] with { BodyModelId = bodyModelId };
            }
        }
        return (resolvedBodies, resolvedModels);
    }

    private static List<ExportModelDependencyOperation> RewriteCustomModelDependencyNames(
        IEnumerable<ExportModelDependencyOperation> operations,
        IReadOnlyList<ExportIdAssignment> assignments)
    {
        var characterIds = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        return operations.Select(operation =>
        {
            if (!characterIds.TryGetValue(operation.BatchEntryId, out var internalName))
                return operation;
            var destination = RewriteCustomResourcePath(operation.VirtualPath, internalName);
            return string.Equals(destination, operation.VirtualPath, StringComparison.Ordinal)
                ? operation
                : operation with { VirtualPath = destination };
        }).ToList();
    }

    private static List<ExportCharacterClothesOperation> RewriteCustomClothesNames(
        IEnumerable<ExportCharacterClothesOperation> operations,
        IReadOnlyList<ExportIdAssignment> assignments)
    {
        var characterIds = assignments
            .Where(assignment => assignment.Domain == "character")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.ResolvedKey);
        var modelIds = assignments
            .Where(assignment => assignment.Domain == "uniformModel")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.NumericId);
        var shoesIds = assignments
            .Where(assignment => assignment.Domain == "shoesModel")
            .ToDictionary(assignment => assignment.BatchEntryId, assignment => assignment.NumericId);
        return operations.Select(operation =>
        {
            if (!characterIds.TryGetValue(operation.BatchEntryId, out var internalName))
                return operation;
            var ids = operation.IsShoes ? shoesIds : modelIds;
            if (!ids.TryGetValue(operation.BatchEntryId, out var assignedModelId)) return operation;
            var resourceKey = operation.IsShoes
                ? $"s{internalName[1..]}"
                : $"u{internalName[1..]}";
            return operation with
            {
                UniformModelId = assignedModelId,
                ResourceKey = resourceKey,
                ModelPath = $"_uniform/{resourceKey}/{resourceKey}.g4md",
                TexturePath = $"_uniform/{resourceKey}/{resourceKey}.g4tx",
            };
        }).ToList();
    }

    private static List<ExportCharacterModelOperation> RewriteCustomUniformIds(
        IEnumerable<ExportCharacterModelOperation> operations,
        IReadOnlyList<ExportCharacterClothesOperation> clothesOperations)
    {
        var uniforms = clothesOperations.Where(operation => !operation.IsShoes)
            .ToDictionary(operation => operation.BatchEntryId, operation => operation.UniformModelId);
        var shoes = clothesOperations.Where(operation => operation.IsShoes)
            .ToDictionary(operation => operation.BatchEntryId, operation => operation.UniformModelId);
        return operations.Select(operation => operation with
        {
            UniformModel = uniforms.TryGetValue(operation.BatchEntryId, out var uniformId)
                ? unchecked((int)uniformId) : operation.UniformModel,
            ShoesModel = shoes.TryGetValue(operation.BatchEntryId, out var shoesId)
                ? unchecked((int)shoesId) : operation.ShoesModel,
        }).ToList();
    }

    private static string RewriteCustomResourcePath(
        string path,
        string internalName,
        bool isCustomShoes = false)
    {
        var normalized = path.Replace('\\', '/');
        const string portraitPrefix = "assets/portraits/";
        if (normalized.StartsWith(portraitPrefix, StringComparison.OrdinalIgnoreCase))
            return $"{portraitPrefix}{internalName}/{Path.GetFileName(normalized)}";

        const string customMarker = "_face/99_CUSTOM/";
        var customIndex = normalized.IndexOf(customMarker, StringComparison.OrdinalIgnoreCase);
        if (customIndex >= 0)
        {
            var prefix = normalized[..(customIndex + customMarker.Length)];
            var suffix = normalized[(customIndex + customMarker.Length)..];
            var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var fileName = segments[^1];
                var extension = Path.GetExtension(fileName);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    var sourceStem = segments[0];
                    var fileStem = Path.GetFileNameWithoutExtension(fileName);
                    var fileSuffix = fileStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase)
                        ? fileStem[sourceStem.Length..]
                        : string.Empty;
                    segments[0] = internalName;
                    segments[^1] = internalName + fileSuffix + extension;
                    return prefix + string.Join('/', segments);
                }
            }
        }

        const string customUniformMarker = "_uniform/99_CUSTOM/";
        var uniformIndex = normalized.IndexOf(customUniformMarker, StringComparison.OrdinalIgnoreCase);
        if (uniformIndex >= 0)
        {
            var prefix = normalized[..(uniformIndex + "_uniform/".Length)];
            var suffix = normalized[(uniformIndex + customUniformMarker.Length)..];
            var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var extension = Path.GetExtension(segments[^1]);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    var sourceStem = segments[0];
                    var partPrefix = isCustomShoes || sourceStem.StartsWith('s') ? "s" : "u";
                    var uniformName = internalName.StartsWith("c", StringComparison.OrdinalIgnoreCase)
                        ? $"{partPrefix}{internalName[1..]}"
                        : $"{partPrefix}{internalName}";
                    var fileStem = Path.GetFileNameWithoutExtension(segments[^1]);
                    var fileSuffix = fileStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase)
                        ? fileStem[sourceStem.Length..]
                        : string.Empty;
                    segments[0] = uniformName;
                    segments[^1] = uniformName + fileSuffix + extension;
                    return prefix + string.Join('/', segments);
                }
            }
        }

        const string iconMarker = "/200_icon/10_icon_chr/face/";
        var iconIndex = normalized.IndexOf(iconMarker, StringComparison.OrdinalIgnoreCase);
        if (iconIndex >= 0 && normalized.EndsWith("_l.g4tx", StringComparison.OrdinalIgnoreCase))
        {
            var fileStart = normalized.LastIndexOf('/') + 1;
            return normalized[..fileStart] + internalName + "_l.g4tx";
        }

        return path;
    }

    private static string GetCustomModelDirectory(string modelPath)
    {
        var normalized = modelPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["common/chr/".Length..];
        return normalized[..(normalized.LastIndexOf('/') + 1)];
    }

    private IReadOnlyList<ExportIdAssignment> AllocateIds(
        IReadOnlyList<ExportIdRequest> requests,
        ExportIdInventory inventory)
    {
        var characterAssignments = _idAllocator.Allocate(
            requests.Where(request => request.Domain == "character"), inventory);
        var characterByBatch = characterAssignments.ToDictionary(assignment => assignment.BatchEntryId);
        var remainingRequests = requests
            .Where(request => request.Domain != "character")
            .Select(request => request.Domain != "parameter"
                ? request
                : request with
                {
                    SymbolicKey = $"pc_para_{ResolveWrittenInternalName(characterByBatch[request.BatchEntryId])}",
                    RequiresExactCrc = true,
                })
            .Select(request => request.Domain != "model"
                ? request
                : request with
                {
                    // Model rows belong to one custom character.  A shared
                    // key made the numeric model ID depend on unrelated dump
                    // collisions and changed it between incorporations.
                    SymbolicKey = $"character.{ResolveWrittenInternalName(characterByBatch[request.BatchEntryId])}.model",
                })
            .Select(request => request.Domain != "uniformModel"
                ? request
                : request with
                {
                    SymbolicKey = $"u{ResolveWrittenInternalName(characterByBatch[request.BatchEntryId])[1..]}",
                    RequiresExactCrc = true,
                })
            .Select(request => request.Domain != "shoesModel"
                ? request
                : request with
                {
                    SymbolicKey = $"s{ResolveWrittenInternalName(characterByBatch[request.BatchEntryId])[1..]}",
                    RequiresExactCrc = true,
                })
            .ToArray();
        var remainingAssignments = _idAllocator.Allocate(remainingRequests, inventory);
        var remainingIndex = 0;
        var assignments = new List<ExportIdAssignment>(requests.Count);
        foreach (var request in requests)
        {
            if (request.Domain == "character")
                assignments.Add(characterByBatch[request.BatchEntryId]);
            else
                assignments.Add(remainingAssignments[remainingIndex++]);
        }
        return assignments;
    }

    private static string ResolveGeneratedMotionPath(
        AnimationBankSource bank,
        string generatedFamilyName,
        string? generatedUniformName)
    {
        if (bank.IsEvent)
            return AvatarAnimationResources.RenameIdentifiers(bank.ResourcePath, bank.SourceIdentifier!, generatedFamilyName);
        if (bank.ResourcePath.StartsWith("_uniform/", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(generatedUniformName))
                throw new InvalidDataException($"Uniform-specific motion bank '{bank.ResourcePath}' has no generated uniform ID.");
            return $"_uniform/{generatedUniformName}/{generatedUniformName}_p{bank.Type}.g4pk";
        }
        if (bank.IsPrimaryFamily)
            return $"{generatedFamilyName}/{generatedFamilyName}_p{bank.Type}.g4pk";
        var sourceStem = Path.GetFileName(Path.GetDirectoryName(bank.ResourcePath));
        if (string.IsNullOrWhiteSpace(sourceStem) || sourceStem.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Alternate motion bank '{bank.ResourcePath}' has no source family directory.");
        return $"{generatedFamilyName}/{generatedFamilyName}_from_{sourceStem}_p{bank.Type}.g4pk";
    }

    private static string ResolveGeneratedMotionFamilyName(string characterId) =>
        characterId.StartsWith("c99", StringComparison.Ordinal)
            ? $"c00{characterId[3..]}"
            : characterId;

    private static string ResolveWrittenInternalName(ExportIdAssignment characterAssignment)
    {
        if (IsCanonicalInternalName(characterAssignment.ResolvedKey))
            return characterAssignment.ResolvedKey;
        throw new InvalidDataException("The character export assignment has no canonical internal name.");
    }

    private static bool IsCanonicalInternalName(string value)
    {
        if (value.Length != 9 || value[0] != 'c') return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index])) return false;
        }
        return true;
    }

    private static int? TryReadInt(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static int? TryReadModelInt(
        IReadOnlyDictionary<string, string?> fields,
        string canonicalKey,
        string legacyKey) =>
        TryReadInt(fields, canonicalKey) ?? TryReadInt(fields, legacyKey);

}
