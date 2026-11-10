using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Packages;
using VictoryTool.G4.Textures;
using VictoryTool.G4.Animations;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record ExportExecutionResult(
    string OutputPath,
    string ReportPath,
    int PublishedFileCount);

public sealed record ExportProgress(string Stage, string? Item);

public interface IExportExecutor
{
    Task<ExportExecutionResult> ExecuteAsync(ExportPlan plan, CancellationToken cancellationToken);

    Task<ExportExecutionResult> ExecuteAsync(
        ExportPlan plan,
        CancellationToken cancellationToken,
        IProgress<ExportProgress> progress) => ExecuteAsync(plan, cancellationToken);
}

public sealed class ExportExecutor : IExportExecutor
{
    private readonly IVrCharaPackageService _packageService;
    private readonly ICharacterLocalizationT2bWriter _localizationWriter;
    private readonly ICharacterCoreT2bWriter _characterCoreWriter;
    private readonly IShopCharacterT2bWriter _shopCharacterWriter;
    private readonly ICharacterDeliveryWriter _characterDeliveryWriter;
    private readonly ICharacterModelT2bWriter _characterModelWriter;
    private readonly ICharacterBodyT2bWriter _characterBodyWriter;
    private readonly ICharacterClothesT2bWriter _characterClothesWriter;

    public ExportExecutor(
        IVrCharaPackageService? packageService = null,
        ICharacterLocalizationT2bWriter? localizationWriter = null,
        ICharacterCoreT2bWriter? characterCoreWriter = null,
        IShopCharacterT2bWriter? shopCharacterWriter = null,
        ICharacterModelT2bWriter? characterModelWriter = null,
        ICharacterBodyT2bWriter? characterBodyWriter = null,
        ICharacterClothesT2bWriter? characterClothesWriter = null,
        ICharacterDeliveryWriter? characterDeliveryWriter = null)
    {
        _packageService = packageService ?? new ZipVrCharaPackageService();
        _localizationWriter = localizationWriter ?? new CharacterLocalizationT2bWriter();
        _characterCoreWriter = characterCoreWriter ?? new CharacterCoreT2bWriter();
        _shopCharacterWriter = shopCharacterWriter ?? new ShopCharacterT2bWriter();
        _characterDeliveryWriter = characterDeliveryWriter ?? new CharacterDeliveryWriter();
        _characterModelWriter = characterModelWriter ?? new CharacterModelT2bWriter();
        _characterBodyWriter = characterBodyWriter ?? new CharacterBodyT2bWriter();
        _characterClothesWriter = characterClothesWriter ?? new CharacterClothesT2bWriter();
    }

    public async Task<ExportExecutionResult> ExecuteAsync(
        ExportPlan plan,
        CancellationToken cancellationToken)
        => await ExecuteAsync(plan, cancellationToken, null);

    public async Task<ExportExecutionResult> ExecuteAsync(
        ExportPlan plan,
        CancellationToken cancellationToken,
        IProgress<ExportProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var operationScope = GlobalLog.BeginOperation("export_execute", new Dictionary<string, object?>
        {
            ["platform"] = plan.Platform,
            ["acquisition"] = plan.Acquisition,
            ["packageCount"] = plan.EnabledPackageCount,
            ["affectedFileCount"] = plan.AffectedFiles.Count,
        });
        if (!plan.CanExport)
        {
            GlobalLog.Error("export_blocked", data: new Dictionary<string, object?>
            {
                ["diagnosticCount"] = plan.Diagnostics.Count,
            });
            throw new InvalidOperationException("A blocked export plan cannot be executed.");
        }
        if (plan.PatchOperations.Count > 0)
        {
            GlobalLog.Error("export_patch_gate_blocked", data: new Dictionary<string, object?>
            {
                ["patchCount"] = plan.PatchOperations.Count,
            });
            throw new NotSupportedException(
                "The export plan contains CFGBIN patch operations that have not passed the table writer gate.");
        }

        var targetNames = AvatarAnimationTargetNames.Prepare(plan);
        plan = targetNames.Plan;
        var outputPath = Path.GetFullPath(plan.OutputPath);
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
            throw new IOException("The export output path already exists.");
        var parentPath = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The export output path has no parent directory.", nameof(plan));
        Directory.CreateDirectory(parentPath);
        var stagingPath = Path.Combine(
            parentPath,
            $".{Path.GetFileName(outputPath)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);

        var publishedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var changedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<byte[]> ReadTableAsync(string path)
        {
            if (!tables.TryGetValue(path, out var bytes))
            {
                if (path.StartsWith(stagingPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    ReportProgress(progress, "config_read", Path.GetRelativePath(stagingPath, path));
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                tables.Add(path, bytes);
            }
            return bytes;
        }
        void UpdateTable(string path, byte[] bytes)
        {
            tables[path] = bytes;
            changedTables.Add(path);
        }
        try
        {
            ReportProgress(progress, "starting", null);
            GlobalLog.Debug("export_file_operations_started", new Dictionary<string, object?>
            {
                ["count"] = plan.FileOperations.Count,
            });
            foreach (var operation in plan.FileOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "files", operation.DestinationPath);
                await CopyAsync(
                    operation.SourcePath,
                    GetSafeDestination(stagingPath, operation.DestinationPath),
                    publishedPaths,
                    cancellationToken);
            }

            GlobalLog.Debug("export_model_dependencies_started", new Dictionary<string, object?>
            {
                ["count"] = plan.ModelDependencyOperations.Count,
            });
            foreach (var operation in plan.ModelDependencyOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "model_dependencies", operation.VirtualPath);
                var destination = GetSafeDestination(stagingPath, operation.VirtualPath);
                if (operation.VirtualPath.Contains("/_face/99_CUSTOM/", StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(operation.VirtualPath).Equals(".objbin", StringComparison.OrdinalIgnoreCase))
                {
                    ReserveDestination(destination, publishedPaths);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var sourceStem = Path.GetFileNameWithoutExtension(operation.SourcePath);
                    var destinationStem = Path.GetFileNameWithoutExtension(operation.VirtualPath);
                    var content = FixedWidthResourceIdRewriter.Replace(
                        await File.ReadAllBytesAsync(operation.SourcePath, cancellationToken),
                        sourceStem,
                        destinationStem);
                    content = RebaseCharacterObjectConfig(content, destinationStem);
                    await File.WriteAllBytesAsync(destination, content, cancellationToken);
                }
                else
                {
                    await CopyAsync(operation.SourcePath, destination, publishedPaths, cancellationToken);
                }
            }

            if (plan.AnimationGenerationRequests.Count > 0)
            {
                var total = plan.AnimationGenerationRequests.Sum(request => request.Banks.Count);
                var completed = 0;
                var progressLogTimer = System.Diagnostics.Stopwatch.StartNew();
                GlobalLog.Info("animation_family_registration_started", new Dictionary<string, object?> { ["count"] = total });
                completed = await GenerateAvatarAnimationsAsync(plan, stagingPath, publishedPaths, progress, cancellationToken);
                foreach (var request in plan.AnimationGenerationRequests.Where(request => request.Profile.AvatarRecipe is null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(progress, "animation_package", request.PackagePath);
                    var authoredPackage = await _packageService.LoadPackageAsync(request.PackagePath, cancellationToken);
                    var authoredBanks = authoredPackage.Resources.ToDictionary(
                        resource => resource.VirtualPath,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var bankSource in request.Banks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ReportProgress(progress, "animation", $"{completed + 1}/{total} · {bankSource.ResourcePath}");
                        byte[] generatedBankBytes;
                        byte[] destinationPackageBytes;
                        var sourcePackageBytes = await File.ReadAllBytesAsync(bankSource.Path, cancellationToken);
                        if (bankSource.GeneratedPackageResourcePath is { } generatedPackagePath)
                        {
                            if (!authoredBanks.TryGetValue(generatedPackagePath, out var generatedPackageResource))
                                throw new InvalidDataException($"Generated G4PK bank '{generatedPackagePath}' is absent from '{request.PackagePath}'.");
                            var packageBytes = generatedPackageResource.Content.ToArray();
                            var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
                            if (!packageHash.Equals(bankSource.GeneratedPackageChecksum, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException($"Generated G4PK bank '{generatedPackagePath}' changed after package validation.");
                            var generatedPackage = G4pkAnimationPackage.Read(packageBytes);
                            var generatedResource = generatedPackage.Resources.Single();
                            generatedBankBytes = generatedResource.Content.ToArray();
                            destinationPackageBytes = packageBytes;
                        }
                        else
                        {
                            var generatedResourcePath = bankSource.GeneratedResourcePath
                                ?? throw new InvalidDataException($"Animation bank '{bankSource.ResourcePath}' has no G4_Blender output.");
                            if (!authoredBanks.TryGetValue(generatedResourcePath, out var generatedResource))
                                throw new InvalidDataException($"Generated G4MT bank '{generatedResourcePath}' is absent from '{request.PackagePath}'.");
                            generatedBankBytes = generatedResource.Content.ToArray();
                            var generatedHash = Convert.ToHexString(SHA256.HashData(generatedBankBytes)).ToLowerInvariant();
                            if (!generatedHash.Equals(bankSource.GeneratedChecksum, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException($"Generated G4MT bank '{generatedResourcePath}' changed after package validation.");
                            destinationPackageBytes = sourcePackageBytes;
                        }
                        var package = G4pkAnimationPackage.Read(sourcePackageBytes);
                        var resource = package.Resources.Single();
                        ValidateGeneratedMotionBank(
                            resource.Content.Span,
                            generatedBankBytes,
                            bankSource.Type,
                            request.Profile);

                        var destinationResourcePath = bankSource.DestinationResourcePath
                            ?? throw new InvalidDataException($"Animation bank '{bankSource.ResourcePath}' has no output route.");
                        var resourceName = $"{Path.GetFileNameWithoutExtension(destinationResourcePath)}.g4mt";
                        var outputPackage = G4pkAnimationPackage.Read(destinationPackageBytes);
                        var outputResource = outputPackage.Resources.Single();
                        var generated = outputPackage.ReplaceResource(outputResource.PackageIndex, resourceName, generatedBankBytes);
                        var writtenPackage = G4pkAnimationPackage.Read(generated);
                        var writtenResource = writtenPackage.Resources.Single();
                        if (!string.Equals(writtenResource.Name, resourceName, StringComparison.Ordinal)
                            || !writtenResource.Content.Span.SequenceEqual(generatedBankBytes))
                            throw new InvalidDataException($"Generated animation package '{resourceName}' failed read-back validation.");
                        var destination = GetSafeDestination(stagingPath, $"common/chr/{destinationResourcePath}");
                        ReserveDestination(destination, publishedPaths);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await File.WriteAllBytesAsync(destination, generated, cancellationToken);
                        completed++;
                        if (completed == total || progressLogTimer.Elapsed >= TimeSpan.FromSeconds(5))
                        {
                            GlobalLog.Info("animation_family_registration_progress", new Dictionary<string, object?>
                            {
                                ["completed"] = completed, ["remaining"] = total - completed,
                                ["total"] = total, ["family"] = request.Family.Name,
                            });
                            progressLogTimer.Restart();
                        }
                    }
                }
                foreach (var request in plan.AnimationGenerationRequests.Where(request => request.Banks.Any(bank => !bank.IsEvent)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                        var tablePath = GetSafeDestination(stagingPath, request.Family.MotionTablePath);
                    var table = await ReadTableAsync(tablePath);
                    table = new CharacterMotionInfoT2bWriter().Append(table, request.Family.Crc32,
                        request.GeneratedFamilyName ?? throw new InvalidDataException("The generated motion family name was not assigned."),
                        request.Banks.Where(bank => !bank.IsEvent).ToArray());
                    UpdateTable(tablePath, table);
                }
            }

            GlobalLog.Debug("export_game_references_started", new Dictionary<string, object?>
            {
                ["count"] = plan.GameReferenceOperations.Count,
            });
            foreach (var operation in plan.GameReferenceOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "references", operation.VirtualPath);
                if (!operation.Exists || string.IsNullOrWhiteSpace(operation.SourcePath))
                    throw new InvalidDataException($"Game resource '{operation.VirtualPath}' is unresolved.");
                await CopyAsync(
                    operation.SourcePath,
                    GetSafeDestination(stagingPath, operation.VirtualPath),
                    publishedPaths,
                    cancellationToken);
            }

            GlobalLog.Debug("export_package_resources_started", new Dictionary<string, object?>
            {
                ["count"] = plan.ResourceOperations.Count,
            });
            var generatedAnimationResources = plan.AnimationGenerationRequests
                .SelectMany(request => request.Profile.GeneratedBanks.Select(bank => bank.ResourcePath)
                    .Concat(request.Profile.GeneratedPackages.Select(package => package.ResourcePath)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var packageGroup in plan.ResourceOperations.GroupBy(operation => operation.PackagePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "loading_package", packageGroup.Key);
                var package = await _packageService.LoadPackageAsync(packageGroup.Key, cancellationToken);
                var resources = package.Resources.ToDictionary(
                    resource => resource.VirtualPath,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var operation in packageGroup)
                {
                    ReportProgress(progress, "package_resources", operation.DestinationPath);
                    var packageResourcePath = operation.PackageResourcePath ?? operation.DestinationPath;
                    if (generatedAnimationResources.Contains(packageResourcePath))
                        continue;
                    if (!resources.TryGetValue(packageResourcePath, out var resource))
                        throw new InvalidDataException(
                            $"Package resource '{packageResourcePath}' is absent after validation.");
                    var destination = GetSafeDestination(stagingPath, operation.DestinationPath);
                    ReserveDestination(destination, publishedPaths);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var content = resource.Content.ToArray();
                    if (targetNames.Names.TryGetValue(operation.BatchEntryId, out var boneNames))
                        content = AvatarAnimationTargetNames.RenameResource(content, boneNames);
                    if (operation.DestinationPath.Contains("_face/99_CUSTOM/", StringComparison.OrdinalIgnoreCase)
                        && Path.GetExtension(packageResourcePath).Equals(".objbin", StringComparison.OrdinalIgnoreCase))
                    {
                        var sourceStem = Path.GetFileNameWithoutExtension(packageResourcePath);
                        var internalName = ResolveInternalName(plan, operation.BatchEntryId);
                        content = FixedWidthResourceIdRewriter.Replace(content, sourceStem, internalName);
                        content = RebaseCharacterObjectConfig(content, internalName);
                    }
                    if (operation.DestinationPath.Contains("/200_icon/", StringComparison.OrdinalIgnoreCase)
                        && operation.DestinationPath.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase))
                    {
                        var internalName = ResolveInternalName(plan, operation.BatchEntryId);
                        content = RebasePortraitIdentifier(content, internalName);
                        ValidatePortraitContent(content, internalName);
                    }
                    await File.WriteAllBytesAsync(destination, content, cancellationToken);
                }
            }

            GlobalLog.Debug("export_portraits_started", new Dictionary<string, object?>
            {
                ["count"] = plan.PortraitOperations.Count,
            });
            foreach (var operation in plan.PortraitOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "portraits", operation.DestinationPath);
                var package = await _packageService.LoadPackageAsync(operation.PackagePath, cancellationToken);
                var resources = package.Resources.ToDictionary(
                    resource => resource.VirtualPath,
                    StringComparer.OrdinalIgnoreCase);
                if (!resources.TryGetValue(operation.StandardPngPath, out var standard)
                    || !resources.TryGetValue(operation.UniformPngPath, out var uniform))
                    throw new InvalidDataException($"Portrait PNG resources are missing from '{operation.PackagePath}'.");
                var content = PngPortraitG4TxConverter.Convert(
                    standard.Content,
                    uniform.Content,
                    operation.TemplatePath,
                    FindPortraitTemplateStem(operation.TemplatePath),
                    ResolveInternalName(plan, operation.BatchEntryId));
                ValidatePortraitContent(content, ResolveInternalName(plan, operation.BatchEntryId));
                var destination = GetSafeDestination(stagingPath, operation.DestinationPath);
                ReserveDestination(destination, publishedPaths);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, content, cancellationToken);
            }

            ValidateCharacterModelResources(stagingPath, plan);

            GlobalLog.Debug("export_localizations_started", new Dictionary<string, object?>
            {
                ["count"] = plan.LocalizationOperations.Count,
            });
            foreach (var operation in plan.LocalizationOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = new CharacterTextIds(
                    FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.full"),
                    FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.short"),
                    FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.upper"),
                    FindAssignedId(plan, operation.BatchEntryId, "descriptionText", ".description"));
                var namePath = GetSafeDestination(stagingPath, operation.NameTablePath);
                var descriptionPath = GetSafeDestination(stagingPath, operation.DescriptionTablePath);
                var romanizedPath = GetSafeDestination(stagingPath, operation.RomanizedNameTablePath);
                var nameTable = await ReadTableAsync(namePath);
                var descriptionTable = await ReadTableAsync(descriptionPath);
                var romanizedNameTable = await ReadTableAsync(romanizedPath);
                var result = _localizationWriter.Append(
                    nameTable,
                    descriptionTable,
                    romanizedNameTable,
                    ids,
                    operation.Text);
                UpdateTable(namePath, result.NameTable);
                UpdateTable(descriptionPath, result.DescriptionTable);
                if (result.RomanizedNameTable is not null)
                    UpdateTable(romanizedPath, result.RomanizedNameTable);
            }

            GlobalLog.Debug("export_models_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterModelOperations.Count,
            });
            foreach (var operation in plan.CharacterBodyOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.SkeletonTemplatePath is { } skeletonTemplatePath
                    && operation.SkeletonModelPath is { } skeletonModelPath)
                {
                    var skeletonPath = Path.ChangeExtension(skeletonModelPath, ".g4sk");
                    var targetPath = $"common/chr/{skeletonModelPath}";
                    var generatedConfig = CreateUniformSkeletonObjectConfig(
                        await File.ReadAllBytesAsync(skeletonTemplatePath, cancellationToken),
                        Path.GetFileNameWithoutExtension(skeletonModelPath),
                        $"common/chr/{skeletonPath}");
                    var destination = GetSafeDestination(stagingPath, targetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllBytesAsync(destination, generatedConfig, cancellationToken);
                    publishedPaths.Add(destination);
                }
                var bodyPath = GetSafeDestination(stagingPath, operation.ModelTablePath);
                var modelTable = await ReadTableAsync(bodyPath);
                var result = _characterBodyWriter.Append(
                    modelTable,
                    new CharacterBodyWriteRequest(
                        operation.SourceBodyModelId,
                        operation.BodyModelId,
                        operation.BodyModelPath,
                        operation.SkeletonModelPath,
                        operation.MotionFamilyCrc32));
                UpdateTable(bodyPath, result);
            }
            foreach (var operation in plan.CharacterClothesOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clothesPath = GetSafeDestination(stagingPath, operation.ClothesTablePath);
                var clothesTable = await ReadTableAsync(clothesPath);
                var result = _characterClothesWriter.Append(
                    clothesTable,
                    new CharacterClothesWriteRequest(
                        operation.SourceUniformModelId,
                        operation.UniformModelId,
                        operation.ResourceKey,
                        operation.ModelPath,
                        operation.TexturePath,
                        operation.SourceResourceKey,
                        operation.ClothesInfoType,
                        operation.IsShoes,
                        operation.SourceModelPath,
                        operation.SourceInfoIndex));
                UpdateTable(clothesPath, result);
            }
            foreach (var operation in plan.CharacterModelOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modelPath = GetSafeDestination(stagingPath, operation.ModelTablePath);
                var modelTable = await ReadTableAsync(modelPath);
                var result = _characterModelWriter.Append(
                    modelTable,
                    new CharacterModelWriteRequest(
                        operation.SourceModelId,
                        FindAssignedId(plan, operation.BatchEntryId, "model"),
                        operation.FaceModelPath,
                        operation.SkinColorRgba,
                        operation.UniformModel,
                        operation.ShoesModel,
                        operation.GloveModel,
                        operation.EquipmentColor,
                        operation.UniformCollarOpen,
                        operation.EquipmentFlag2,
                        operation.ChestSize,
                        operation.ForceKit,
                        operation.BodyModelId));
                UpdateTable(modelPath, result);
            }


            GlobalLog.Debug("export_character_core_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterCoreOperations.Count,
            });
            foreach (var group in plan.CharacterCoreOperations.GroupBy(operation =>
                         (operation.BaseTablePath, operation.ParameterTablePath)))
            {
                var basePath = GetSafeDestination(stagingPath, group.Key.BaseTablePath);
                var parameterPath = GetSafeDestination(stagingPath, group.Key.ParameterTablePath);
                var baseTable = await ReadTableAsync(basePath);
                var parameterTable = await ReadTableAsync(parameterPath);
                var sources = new List<CharacterCoreWriteSource>();
                foreach (var operation in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var characterId = FindAssignedId(plan, operation.BatchEntryId, "character");
                    var request = new CharacterCoreWriteRequest(
                        operation.SourceBaseId,
                        operation.SourceParameterId,
                        characterId,
                        FindAssignedParameterId(plan, operation.BatchEntryId, operation.VariantIndex),
                        ResolveInternalName(plan, operation.BatchEntryId),
                        FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.full"),
                        FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.short"),
                        FindAssignedId(plan, operation.BatchEntryId, "nameText", ".name.upper"),
                        FindAssignedId(plan, operation.BatchEntryId, "descriptionText", ".description"),
                        operation.Affinity,
                        operation.MainPosition,
                        operation.SubPosition,
                        operation.Skills,
                        operation.UsesAllocatedModel
                            ? FindAssignedId(plan, operation.BatchEntryId, "model")
                            : null,
                        EnforceIdentityHash: operation.VariantIndex == 0,
                        operation.PlayStyle,
                        operation.Growth,
                        operation.Rank,
                        operation.AbilityBoardId,
                        operation.SpecialRarity,
                        operation.Gender,
                        operation.AcademicYear,
                        operation.SourceSeries,
                        operation.UniformPortraitVariant,
                        operation.TeamAssociation1,
                        operation.TeamAssociation2,
                        operation.TeamAssociation3,
                        operation.OriginGameAssociationIndex,
                        operation.SkillsUnlocked,
                        operation.WritesBaseRow);
                    var sourceBaseTable = operation.TemplateBaseTableSourcePath is null
                        ? baseTable
                        : await ReadTableAsync(operation.TemplateBaseTableSourcePath);
                    var sourceParameterTable = operation.TemplateParameterTableSourcePath is null
                        ? parameterTable
                        : await ReadTableAsync(operation.TemplateParameterTableSourcePath);
                    sources.Add(new CharacterCoreWriteSource(request, sourceBaseTable, sourceParameterTable));
                }
                var result = _characterCoreWriter.AppendBatch(baseTable, parameterTable, sources, cancellationToken);
                UpdateTable(basePath, result.BaseTable);
                UpdateTable(parameterPath, result.ParameterTable);
            }

            GlobalLog.Debug("export_shop_started", new Dictionary<string, object?>
            {
                ["count"] = plan.ShopCharacterOperations.Count,
            });
            foreach (var operation in plan.ShopCharacterOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shopPath = GetSafeDestination(stagingPath, operation.ShopTablePath);
                var shopTable = await ReadTableAsync(shopPath);
                var result = _shopCharacterWriter.Append(
                    shopTable,
                    new ShopCharacterWriteRequest(
                        operation.SourceItemId,
                        operation.SourceParameterId,
                        unchecked((int)FindAssignedId(plan, operation.BatchEntryId, "shopItem")),
                        unchecked((int)FindAssignedId(plan, operation.BatchEntryId, "parameter")),
                        operation.Rarity,
                        operation.SpecialVariant,
                        operation.IsFree,
                        operation.SourceShopParameterId));
                UpdateTable(shopPath, result);
            }

            GlobalLog.Debug("export_delivery_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterDeliveryOperations.Count,
            });
            foreach (var group in plan.CharacterDeliveryOperations.GroupBy(operation => operation.DeliveryTablePath, StringComparer.OrdinalIgnoreCase))
            {
                var operation = group.First();
                cancellationToken.ThrowIfCancellationRequested();
                var deliveryPath = GetSafeDestination(stagingPath, operation.DeliveryTablePath);
                var deliveryTable = await ReadTableAsync(deliveryPath);
                var result = _characterDeliveryWriter.Append(
                    deliveryTable,
                    new VictoryTool.CfgBin.CharacterDeliveryWriteRequest(
                        FindAssignedId(plan, operation.BatchEntryId, operation.VariantIndex == 0 ? "delivery" : "deliveryVariant",
                            operation.VariantIndex == 0 ? null : $".variant.{operation.VariantIndex}"),
                        FindAssignedId(plan, operation.BatchEntryId, operation.VariantIndex == 0 ? "deliveryReceived" : "deliveryReceivedVariant",
                            operation.VariantIndex == 0 ? null : $".variant.{operation.VariantIndex}"),
                        FindAssignedParameterId(plan, operation.BatchEntryId, operation.VariantIndex),
                        operation.VariantIndex == 0 && plan.DeliveryLocalizationOperations.Count > 0
                            ? FindAssignedId(plan, operation.BatchEntryId, "deliveryTitleText", ".delivery.title")
                            : null)
                    {
                        AdditionalCharacterParameterIds = group.Skip(1)
                            .Select(item => FindAssignedParameterId(plan, item.BatchEntryId, item.VariantIndex)).ToArray(),
                    });
                UpdateTable(deliveryPath, result);
            }

            GlobalLog.Debug("export_delivery_localizations_started", new Dictionary<string, object?>
            {
                ["count"] = plan.DeliveryLocalizationOperations.Count,
            });
            var deliveryTextWriter = new DeliveryTextWriter();
            foreach (var group in plan.DeliveryLocalizationOperations.GroupBy(operation => operation.TablePath, StringComparer.OrdinalIgnoreCase))
            {
                var operation = group.First();
                cancellationToken.ThrowIfCancellationRequested();
                var tablePath = GetSafeDestination(stagingPath, operation.TablePath);
                var table = await ReadTableAsync(tablePath);
                var titleId = FindAssignedId(plan, operation.BatchEntryId, "deliveryTitleText", ".delivery.title");
                var result = deliveryTextWriter.AppendTitle(table, titleId, operation.Title);
                UpdateTable(tablePath, result);
            }

            foreach (var path in changedTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "config_write", Path.GetRelativePath(stagingPath, path));
                ExportProvenance.MarkT2b(tables[path]);
                await File.WriteAllBytesAsync(path, tables[path], cancellationToken);
            }
            tables.Clear();
            SynchronizeCustomMeshCapacity(stagingPath, plan);
            var characterResources = ValidateCharacterSkeletonResources(stagingPath, plan);

            cancellationToken.ThrowIfCancellationRequested();
            const string integrityManifestName = "victorytool-export-manifest.sha256";
            var integrityEntries = new List<string>(publishedPaths.Count);
            var integrityHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in publishedPaths
                         .OrderBy(path => Path.GetRelativePath(stagingPath, path), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "integrity", Path.GetRelativePath(stagingPath, path));
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
                var relativePath = Path.GetRelativePath(stagingPath, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                integrityHashes.Add(relativePath, hash);
                integrityEntries.Add($"{hash}  {relativePath}");
            }
            var integrityManifestPath = GetSafeDestination(stagingPath, integrityManifestName);
            var reportPath = GetSafeDestination(stagingPath, "victorytool-export-report.json");
            var reservedPaths = new HashSet<string>(publishedPaths, StringComparer.OrdinalIgnoreCase);
            ReserveDestination(integrityManifestPath, reservedPaths);
            ReserveDestination(reportPath, reservedPaths);
            await File.WriteAllLinesAsync(integrityManifestPath, integrityEntries,
                new UTF8Encoding(false), cancellationToken);

            var report = new
            {
                formatVersion = 1,
                toolVersion = ApplicationVersion.Current,
                applicationBuildId = typeof(ExportExecutor).Assembly.ManifestModule.ModuleVersionId,
                createdUtc = DateTimeOffset.UtcNow,
                platform = plan.Platform.ToString(),
                acquisition = plan.Acquisition.ToString(),
                packageCount = plan.EnabledPackageCount,
                integrityManifest = integrityManifestName,
                animationFamilyRegistrations = plan.AnimationGenerationRequests.Select(request => new
                {
                    request.BatchEntryId,
                    request.Category,
                    sourceModelStem = request.Profile.SourceModelStem,
                    sourceFamily = request.Family.Name,
                    eventSkeleton = (request.EventRecipe ?? request.Profile.AvatarRecipe)?.EventSkeleton,
                    generatedFamily = request.GeneratedFamilyName,
                    generatedFamilyCrc32 = request.GeneratedFamilyCrc32?.ToString("X8"),
                    banks = request.Banks.Select(bank => new
                    {
                        sourceResourcePath = bank.ResourcePath,
                        registrationMode = bank.IsEvent ? "replace_native" : "custom_family",
                        type = bank.Type,
                        outputPath = bank.IsEvent ? bank.DestinationResourcePath : $"common/chr/{bank.DestinationResourcePath}",
                        sha256 = integrityHashes[bank.IsEvent ? bank.DestinationResourcePath! : $"common/chr/{bank.DestinationResourcePath}"],
                        g4mtSha256 = bank.GeneratedChecksum,
                    }).OrderBy(bank => bank.outputPath, StringComparer.Ordinal).ToArray(),
                }).ToArray(),
                resourceValidation = new { status = "passed", characters = characterResources },
                assignedIds = plan.AssignedIds,
                deliveryTitles = plan.DeliveryLocalizationOperations
                    .Select(operation => new
                    {
                        operation.BatchEntryId,
                        operation.Locale,
                        TitleId = FindAssignedId(
                            plan,
                            operation.BatchEntryId,
                            "deliveryTitleText",
                            ".delivery.title"),
                        operation.Title,
                    })
                    .ToArray(),
                files = publishedPaths
                    .Select(path => Path.GetRelativePath(stagingPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray(),
            };
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "publishing", Path.GetFileName(outputPath));
            Directory.Move(stagingPath, outputPath);
            GlobalLog.Info("export_published", new Dictionary<string, object?>
            {
                ["outputPath"] = outputPath,
                ["publishedFileCount"] = publishedPaths.Count,
            });
            return new ExportExecutionResult(
                outputPath,
                Path.Combine(outputPath, Path.GetFileName(reportPath)),
                publishedPaths.Count);
        }
        catch (Exception exception)
        {
            GlobalLog.Error("export_publish_failed", exception, new Dictionary<string, object?>
            {
                ["publishedFileCount"] = publishedPaths.Count,
            });
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    private static void ReportProgress(IProgress<ExportProgress>? progress, string stage, string? item) =>
        progress?.Report(new ExportProgress(stage, item));

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        ISet<string> publishedPaths,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("An export source file is missing.", sourcePath);
        ReserveDestination(destinationPath, publishedPaths);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static uint FindAssignedId(
        ExportPlan plan,
        Guid batchEntryId,
        string domain,
        string? symbolicSuffix = null)
    {
        var matches = plan.AssignedIds.Where(assignment =>
            assignment.BatchEntryId == batchEntryId
            && assignment.Domain == domain
            && (symbolicSuffix is null
                || assignment.SymbolicKey.EndsWith(symbolicSuffix, StringComparison.Ordinal))).ToArray();
        return matches.Length == 1
            ? matches[0].NumericId
            : throw new InvalidDataException(
                symbolicSuffix is null
                    ? $"The export plan must contain exactly one {domain} assignment for the batch entry."
                    : $"The export plan must contain exactly one {domain} assignment ending in '{symbolicSuffix}'.");
    }

    private static uint FindAssignedParameterId(ExportPlan plan, Guid batchEntryId, int variantIndex) =>
        FindAssignedId(plan, batchEntryId, variantIndex == 0 ? "parameter" : "parameterVariant",
            variantIndex == 0 ? null : $".variant.{variantIndex}");

    private static string ResolveInternalName(ExportPlan plan, Guid batchEntryId)
    {
        var matches = plan.AssignedIds.Where(assignment =>
            assignment.BatchEntryId == batchEntryId && assignment.Domain == "character").ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                "The export plan must contain exactly one character assignment for the batch entry.");
        var internalName = matches[0].ResolvedKey;
        if (internalName.Length != 9 || internalName[0] != 'c'
            || internalName.Skip(1).Any(character => !char.IsAsciiDigit(character)))
        {
            throw new InvalidDataException("The character export assignment has no canonical internal name.");
        }
        return internalName;
    }

    private static void ReserveDestination(string destinationPath, ISet<string> publishedPaths)
    {
        if (!publishedPaths.Add(destinationPath))
            throw new InvalidDataException($"Multiple export operations target '{destinationPath}'.");
    }

    private static byte[] RebasePortraitIdentifier(byte[] content, string destinationStem)
    {
        var document = G4TxDocument.Read(content);
        var sourceName = document.Textures.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(sourceName)) return content;
        var marker = sourceName.IndexOf("_1_", StringComparison.Ordinal);
        var sourceStem = marker > 0 ? sourceName[..marker] : sourceName.Split('_')[0];
        return document.RenameIdentifier(sourceStem, destinationStem);
    }

    private static byte[] RebaseCharacterObjectConfig(byte[] content, string characterName)
    {
        var document = CfgBinDocument.Read(content);
        var edits = new List<CfgBinValueEdit>();
        foreach (var entry in document.Entries)
        for (var valueIndex = 0; valueIndex < entry.Values.Count; valueIndex++)
        {
            if (entry.Values[valueIndex].Value is not string value
                || !value.Contains("common/chr/_face/", StringComparison.OrdinalIgnoreCase))
                continue;
            var extension = Path.GetExtension(value);
            if (!extension.Equals(".g4sk", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".clobin", StringComparison.OrdinalIgnoreCase))
                continue;
            edits.Add(new CfgBinValueEdit(
                entry.Index,
                valueIndex,
                $"common/chr/_face/99_CUSTOM/{characterName}/{characterName}{extension.ToLowerInvariant()}"));
        }
        if (edits.Count != 2)
            throw new InvalidDataException(
                $"The custom character object config must reference one G4SK and one CLOBIN for '{characterName}'.");

        var rebased = document.WriteWithStringEdits(edits);
        foreach (var extension in new[] { ".g4sk", ".clobin" })
        {
            var expectedPath = System.Text.Encoding.ASCII.GetBytes(
                $"common/chr/_face/99_CUSTOM/{characterName}/{characterName}{extension}");
            if (rebased.AsSpan().IndexOf(expectedPath) < 0)
                throw new InvalidDataException(
                    $"The custom character object config did not retain its {extension} reference for '{characterName}'.");
        }
        return rebased;
    }

    private static byte[] CreateUniformSkeletonObjectConfig(byte[] template, string objectName, string skeletonPath)
    {
        var document = CfgBinDocument.Read(template);
        var objectRows = document.Entries.Where(entry => entry.Name == "OBJ_BGN"
            && entry.Values.Count >= 1
            && entry.Values[0].Value is string).ToArray();
        var matches = document.Entries.Where(entry => entry.Name == "SETUP_PARAM"
            && entry.Values.Count >= 2
            && string.Equals(entry.Values[0].Value as string, "Skeleton", StringComparison.Ordinal)).ToArray();
        if (objectRows.Length != 1 || matches.Length != 1 || matches[0].Values[1].Value is not string)
            throw new InvalidDataException("The source body object config has no unique Skeleton parameter.");
        var result = document.WriteWithStringEdits(
        [
            new CfgBinValueEdit(objectRows[0].Index, 0, objectName),
            new CfgBinValueEdit(matches[0].Index, 1, skeletonPath),
        ]);
        var restored = CfgBinDocument.Read(result);
        var writtenObjectName = restored.Entries.Single(entry => entry.Name == "OBJ_BGN");
        var written = restored.Entries.Single(entry => entry.Name == "SETUP_PARAM"
            && entry.Values.Count >= 2
            && string.Equals(entry.Values[0].Value as string, "Skeleton", StringComparison.Ordinal));
        if (!string.Equals(writtenObjectName.Values[0].Value as string, objectName, StringComparison.Ordinal)
            || !string.Equals(written.Values[1].Value as string, skeletonPath, StringComparison.Ordinal))
            throw new InvalidDataException("The generated body object config does not point to its custom G4SK.");
        return result;
    }

    private static void ValidatePortraitContent(ReadOnlySpan<byte> content, string expectedStem)
    {
        var document = G4TxDocument.Read(content);
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{expectedStem}_1_l00",
            $"{expectedStem}_2_l00",
        };
        if (document.TextureCount != expectedNames.Count
            || document.Textures.Any(texture => !expectedNames.Contains(texture.Name)))
        {
            throw new InvalidDataException(
                $"The exported portrait does not contain the expected two layers for '{expectedStem}'.");
        }
        var hashes = document.Textures.Select(texture => texture.StoredHash).ToArray();
        var ids = document.Textures.Select(texture => (int)texture.Id).ToArray();
        if (document.TotalCount != expectedNames.Count
            || !ids.Order().SequenceEqual(Enumerable.Range(0, hashes.Length))
            || !ids.Select(index => hashes[index]).SequenceEqual(hashes.Order()))
            throw new InvalidDataException($"The exported portrait lookup is not ordered by texture hash for '{expectedStem}'.");
    }

    private static void ValidateCharacterModelResources(string stagingPath, ExportPlan plan)
    {
        foreach (var operation in plan.CharacterModelOperations)
            ValidateModelFamily(stagingPath, operation.FaceModelPath, plan.Platform);
    }

    private static void ValidateModelFamily(string stagingPath, string modelPath, ExportPlatform platform)
    {
        var commonPath = NormalizeCommonResourcePath(modelPath);
        if (!Path.GetExtension(commonPath).Equals(".g4md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The character model '{modelPath}' is not a G4MD resource.");
        var platformRoot = platform == ExportPlatform.Pc ? "dx11" : "nx";
        var stem = Path.ChangeExtension(commonPath, null);
        var texturePath = $"{platformRoot}/{stem["common/".Length..]}.g4tx";
        foreach (var path in new[] { commonPath, $"{stem}.g4mg", texturePath })
        {
            if (!File.Exists(GetSafeDestination(stagingPath, path)))
                throw new InvalidDataException($"The exported model family '{modelPath}' is incomplete; missing '{path}'.");
        }
    }

    private sealed record CharacterResourceValidation(
        Guid BatchEntryId, uint ModelId, int BodyModelId, string ModelPath,
        string ObjectConfigPath, string SkeletonPath, string SkeletonSha256, int JointCount,
        int MeshCapacity);

    private static void SynchronizeCustomMeshCapacity(string stagingPath, ExportPlan plan)
    {
        Span<byte> header = stackalloc byte[0x24];
        foreach (var body in plan.CharacterBodyOperations)
        {
            if (body.SkeletonModelPath is not { } skeletonConfig)
                continue;
            var heads = plan.CharacterModelOperations.Where(model => model.BatchEntryId == body.BatchEntryId)
                .Select(model => model.FaceModelPath).ToArray();
            var paths = heads.Append(body.BodyModelPath)
                .Concat(plan.CharacterClothesOperations.Where(part => part.BatchEntryId == body.BatchEntryId)
                    .Select(part => part.ModelPath))
                .Select(NormalizeCommonResourcePath).Distinct(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var path in paths)
            {
                var file = GetSafeDestination(stagingPath, path);
                // Native equipment may remain in the game archives; only staged geometry contributes here.
                if (!File.Exists(file))
                    continue;
                using var stream = File.OpenRead(file);
                stream.ReadExactly(header);
                if (!header[..4].SequenceEqual("G4MD"u8))
                    throw new InvalidDataException($"The custom model '{path}' is not a G4MD resource.");
                count = checked(count + BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]));
            }
            var configs = heads.Select(path => Path.ChangeExtension(path, ".objbin")).Append(skeletonConfig)
                .Select(NormalizeCommonResourcePath).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in configs)
            {
                var file = GetSafeDestination(stagingPath, path);
                if (!File.Exists(file))
                    continue;
                var config = CfgBinDocument.Read(File.ReadAllBytes(file));
                var capacity = config.Entries.SingleOrDefault(entry => entry.Name == "SETUP_PARAM"
                    && entry.Values.Count >= 2 && Equals(entry.Values[0].Value, "MeshNum"))
                    ?? throw new InvalidDataException($"The custom object config '{path}' has no MeshNum parameter.");
                if (Convert.ToInt32(capacity.Values[1].Value) < count)
                    File.WriteAllBytes(file, config.WriteWithValueEdits([new CfgBinValueEdit(capacity.Index, 1, count)]));
            }
        }
    }

    private static IReadOnlyList<CharacterResourceValidation> ValidateCharacterSkeletonResources(string stagingPath, ExportPlan plan)
    {
        foreach (var request in plan.AnimationGenerationRequests)
        {
            if (!plan.CharacterBodyOperations.Any(body => body.BatchEntryId == request.BatchEntryId
                    && body.SkeletonModelPath is not null))
                throw new InvalidDataException("The custom animation rig has no character body registration. Re-export the character package with its skeleton references.");
        }
        var validations = new List<CharacterResourceValidation>();
        foreach (var operation in plan.CharacterBodyOperations)
        {
            if (operation.SkeletonModelPath is not { } skeletonModelPath)
                continue;

            var tablePath = GetSafeDestination(stagingPath, operation.ModelTablePath);
            var table = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
            var bodyRows = table.Entries.Where(entry => entry.Name == "CHARA_BODY_INFO"
                && entry.Values.Count >= 7
                && entry.Values[0].Value switch
                {
                    int value => value == operation.BodyModelId,
                    long value => value == operation.BodyModelId,
                    _ => false,
                }).ToArray();
            if (bodyRows.Length != 1
                || !string.Equals(bodyRows[0].Values[1].Value as string, skeletonModelPath, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The exported body model {operation.BodyModelId} resolves to '{(bodyRows.Length == 1 ? bodyRows[0].Values[1].Value : "no unique row")}', not '{skeletonModelPath}'.");

            if (!string.Equals(bodyRows[0].Values[2].Value as string, operation.BodyModelPath, StringComparison.Ordinal))
                throw new InvalidDataException($"The exported body model {operation.BodyModelId} does not reference its geometry '{operation.BodyModelPath}'.");
            ValidateModelFamily(stagingPath, operation.BodyModelPath, plan.Platform);
            var modelId = FindAssignedId(plan, operation.BatchEntryId, "model");
            var modelRows = table.Entries.Where(entry => entry.Name == "CHARA_MODEL_INFO"
                && entry.Values.Count >= 5
                && Convert.ToInt64(entry.Values[0].Value) == unchecked((int)modelId)).ToArray();
            if (modelRows.Length != 1 || Convert.ToInt64(modelRows[0].Values[4].Value) != operation.BodyModelId)
                throw new InvalidDataException($"The exported character model {modelId} is not linked to custom body {operation.BodyModelId}.");
            if (operation.MotionFamilyCrc32 is { } familyCrc
                && Convert.ToInt64(bodyRows[0].Values[3].Value) != familyCrc)
                throw new InvalidDataException($"The exported body model {operation.BodyModelId} is not linked to its generated CHR animation family.");

            var objectConfigPath = NormalizeCommonResourcePath(skeletonModelPath);
            if (!Path.GetExtension(objectConfigPath).Equals(".objbin", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The custom skeleton object config path '{skeletonModelPath}' is not an OBJBIN resource.");
            var objectConfigFile = GetSafeDestination(stagingPath, objectConfigPath);
            if (!File.Exists(objectConfigFile))
                throw new InvalidDataException($"The custom skeleton object config '{objectConfigPath}' is missing.");

            var config = CfgBinDocument.Read(File.ReadAllBytes(objectConfigFile));
            var meshCapacity = config.Entries.Single(entry => entry.Name == "SETUP_PARAM"
                && entry.Values.Count >= 2 && Equals(entry.Values[0].Value, "MeshNum"));
            var skeletonRows = config.Entries.Where(entry => entry.Name == "SETUP_PARAM"
                && entry.Values.Count >= 2
                && string.Equals(entry.Values[0].Value as string, "Skeleton", StringComparison.Ordinal)).ToArray();
            if (skeletonRows.Length != 1 || skeletonRows[0].Values[1].Value is not string skeletonPath)
                throw new InvalidDataException(
                    $"The custom skeleton object config '{objectConfigPath}' has no unique Skeleton reference.");

            var commonSkeletonPath = NormalizeCommonResourcePath(skeletonPath);
            if (!Path.GetExtension(commonSkeletonPath).Equals(".g4sk", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(GetSafeDestination(stagingPath, commonSkeletonPath)))
                throw new InvalidDataException(
                    $"The custom skeleton object config '{objectConfigPath}' references a missing G4SK resource '{skeletonPath}'.");
            var skeletonBytes = File.ReadAllBytes(GetSafeDestination(stagingPath, commonSkeletonPath));
            var skeleton = G4skSkeletonReader.Read(skeletonBytes);
            var skeletonHash = Convert.ToHexString(SHA256.HashData(skeletonBytes));
            var requests = plan.AnimationGenerationRequests.Where(request => request.BatchEntryId == operation.BatchEntryId).ToArray();
            if (requests.Length > 0 && !requests.Any(request =>
                    string.Equals(request.Profile.TargetRig.Sha256, skeletonHash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The exported skeleton '{commonSkeletonPath}' does not match the rig used for this character's animations.");
            validations.Add(new CharacterResourceValidation(operation.BatchEntryId, modelId, operation.BodyModelId,
                NormalizeCommonResourcePath(operation.BodyModelPath), objectConfigPath, commonSkeletonPath,
                skeletonHash, skeleton.Nodes.Count, Convert.ToInt32(meshCapacity.Values[1].Value)));
        }
        return validations;
    }

    private static string NormalizeCommonResourcePath(string resourcePath)
    {
        var normalized = resourcePath.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("common/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"common/chr/{normalized}";
    }

    private static string FindPortraitTemplateStem(string templatePath)
    {
        var texture = G4TxDocument.Read(File.ReadAllBytes(templatePath)).Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait template has no textures.");
        var marker = texture.Name.IndexOf("_1_", StringComparison.Ordinal);
        return marker > 0 ? texture.Name[..marker] : texture.Name.Split('_')[0];
    }

    private static void ValidateGeneratedMotionBank(
        ReadOnlySpan<byte> sourceBytes,
        ReadOnlySpan<byte> generatedBytes,
        string bankType,
        CharacterAnimationProfile profile)
    {
        var source = G4mtAnimationBank.Read(sourceBytes);
        var generated = G4mtAnimationBank.Read(generatedBytes);
        if (generated.Clips.Count > source.Clips.Count)
            throw new InvalidDataException($"Generated G4MT p{bankType} contains more clips than its source bank.");

        var expectedTargets = profile.Joints.Select(joint => joint.NameCrc32).ToHashSet();
        var matchedSourceClips = new HashSet<int>();
        foreach (var generatedClip in generated.Clips)
        {
            G4mtClip? sourceClip;
            if (source.Clips.Count == generated.Clips.Count)
            {
                sourceClip = source.Clips[generatedClip.Index];
            }
            else
            {
                var matches = source.Clips.Where(clip => clip.NameHash == generatedClip.NameHash
                    && string.Equals(clip.Name, generatedClip.Name, StringComparison.Ordinal)).Take(2).ToArray();
                sourceClip = matches.Length == 1 ? matches[0] : null;
            }
            if (sourceClip is null || !matchedSourceClips.Add(sourceClip.Index))
                throw new InvalidDataException($"Generated G4MT p{bankType} clip {generatedClip.Index} does not uniquely match a source clip.");
            if (sourceClip.NameHash != generatedClip.NameHash
                || sourceClip.Name != generatedClip.Name
                || sourceClip.StartFrame != generatedClip.StartFrame
                || sourceClip.EndFrame != generatedClip.EndFrame
                || sourceClip.Flags != generatedClip.Flags
                || sourceClip.FramesPerSecond != generatedClip.FramesPerSecond)
                throw new InvalidDataException($"Generated G4MT p{bankType} changed clip metadata at index {generatedClip.Index}.");

            var sourceInfos = source.TargetInfos.Skip(sourceClip.TargetInfoStart).Take(sourceClip.TargetInfoCount).ToArray();
            var generatedInfos = generated.TargetInfos.Skip(generatedClip.TargetInfoStart).Take(generatedClip.TargetInfoCount).ToArray();
            if ((generatedClip.Flags & 1) != 0)
            {
                if (sourceInfos.Length != generatedInfos.Length)
                    throw new InvalidDataException($"Generated additive G4MT clip {generatedClip.Index} changed its target count.");
                for (var infoIndex = 0; infoIndex < sourceInfos.Length; infoIndex++)
                {
                    var sourceInfo = sourceInfos[infoIndex];
                    var generatedInfo = generatedInfos[infoIndex];
                    if (source.TargetHashes[sourceInfo.TargetIndex] != generated.TargetHashes[generatedInfo.TargetIndex]
                        || sourceInfo.ChannelCount != generatedInfo.ChannelCount)
                        throw new InvalidDataException($"Generated additive G4MT clip {generatedClip.Index} changed its target channels.");
                    for (var channelOffset = 0; channelOffset < sourceInfo.ChannelCount; channelOffset++)
                    {
                        var sourceChannel = source.Channels[sourceInfo.ChannelStart + channelOffset];
                        var generatedChannel = generated.Channels[generatedInfo.ChannelStart + channelOffset];
                        if (sourceChannel.Type != generatedChannel.Type
                            || sourceChannel.Codec != generatedChannel.Codec
                            || sourceChannel.Interpolation != generatedChannel.Interpolation
                            || sourceChannel.Variant != generatedChannel.Variant
                            || sourceChannel.ComponentCount != generatedChannel.ComponentCount
                            || sourceChannel.BytesPerKey != generatedChannel.BytesPerKey
                            || !sourceChannel.Keys.SequenceEqual(generatedChannel.Keys))
                            throw new InvalidDataException($"Generated additive G4MT clip {generatedClip.Index} changed its channel encoding.");
                        for (var keyIndex = 0; keyIndex < sourceChannel.Keys.Count; keyIndex++)
                            if (!source.DecodeKey(sourceChannel.Index, keyIndex)
                                .SequenceEqual(generated.DecodeKey(generatedChannel.Index, keyIndex)))
                                throw new InvalidDataException($"Generated additive G4MT clip {generatedClip.Index} changed key values.");
                    }
                }
                continue;
            }

            var actualTargets = generatedInfos
                .Select(info => generated.TargetHashes[info.TargetIndex])
                .ToHashSet();
            if (!actualTargets.SetEquals(expectedTargets) || actualTargets.Count != generatedInfos.Length)
                throw new InvalidDataException($"Generated G4MT p{bankType} clip {generatedClip.Index} does not target exactly the supplied G4SK joints.");
        }
    }

    private async Task<int> GenerateAvatarAnimationsAsync(
        ExportPlan plan, string stagingPath, HashSet<string> publishedPaths,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var requests = plan.AnimationGenerationRequests.Where(request => request.Profile.AvatarRecipe is not null).ToArray();
        foreach (var request in requests)
        {
            ReportProgress(progress, "animation_package", request.PackagePath);
            await _packageService.LoadPackageAsync(request.PackagePath, cancellationToken);
        }
        var rigKeys = requests.SelectMany(request => new[] { request.Profile.AvatarRecipe!, request.EventRecipe ?? request.Profile.AvatarRecipe! }).Distinct()
            .ToDictionary(recipe => recipe, recipe => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(recipe))));
        var groups = requests.SelectMany(request => request.Banks.Select(bank => (Request: request, Bank: bank)))
            .GroupBy(job => job.Bank.IsEvent ? job.Bank.DestinationResourcePath : $"common/chr/{job.Bank.DestinationResourcePath}",
                StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0) return 0;
        var estimatedWorkerMemory = Math.Max(256L * 1024 * 1024,
            groups.SelectMany(group => group).Max(job => new FileInfo(job.Bank.Path).Length) * 32);
        var memoryBudget = Math.Max(estimatedWorkerMemory, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
        var workers = (int)Math.Max(1, Math.Min(Environment.ProcessorCount, Math.Min(groups.Length, memoryBudget / estimatedWorkerMemory)));
        var clipWorkers = groups.Length == 1
            ? (int)Math.Max(1, Math.Min(Environment.ProcessorCount, memoryBudget / estimatedWorkerMemory)) : 1;
        var total = plan.AnimationGenerationRequests.Sum(request => request.Banks.Count);
        var completed = 0;
        var progressLock = new object();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        GlobalLog.Info("animation_workers_started", new Dictionary<string, object?> { ["workers"] = workers, ["clipWorkers"] = clipWorkers, ["destinations"] = groups.Length });
        await Parallel.ForEachAsync(groups, new ParallelOptions
        {
            MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken,
        }, async (group, token) =>
        {
            byte[]? merged = null;
            var rigs = new HashSet<string>(StringComparer.Ordinal);
            var bones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (request, bank) in group)
            {
                token.ThrowIfCancellationRequested();
                lock (progressLock)
                    ReportProgress(progress, "animation", $"{completed}/{total} · {bank.ResourcePath}");
                var recipe = bank.IsEvent ? request.EventRecipe ?? request.Profile.AvatarRecipe! : request.Profile.AvatarRecipe!;
                var route = bank.DestinationResourcePath
                    ?? throw new InvalidDataException($"Animation bank '{bank.ResourcePath}' has no output route.");
                if (bank.IsEvent && !string.Equals(route, bank.ResourcePath, StringComparison.Ordinal))
                    throw new InvalidDataException($"EVENT bank '{bank.ResourcePath}' must replace its native route instead of '{route}'.");
                var destination = GetSafeDestination(stagingPath, bank.IsEvent ? route : $"common/chr/{route}");
                var effect = AvatarAnimationResources.IsNativeEventEffect(bank);
                var rigKey = rigKeys[recipe];
                if (merged is not null && !bank.IsEvent)
                    throw new InvalidDataException($"Multiple animation banks target '{route}'.");
                if (merged is null || effect || !rigs.Contains(rigKey))
                {
                    var customBones = recipe.TargetNodes.Select(node => node.Name)
                        .Except(recipe.SourceNodes.Select(node => node.Name), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                    if (merged is not null && !effect && bones.Overlaps(customBones))
                        throw new InvalidDataException($"Shared EVENT bank '{route}' has incompatible custom rigs with overlapping bone names.");
                    var source = effect || merged is null ? await File.ReadAllBytesAsync(bank.Path, token) : merged;
                    byte[] output;
                    try { output = AvatarAnimationResources.Export(source, bank, recipe, clipWorkers, token); }
                    catch (InvalidDataException exception)
                    { throw new InvalidDataException($"Animation bank '{bank.ResourcePath}' failed: {exception.Message}", exception); }
                    if (effect && merged is not null && !merged.AsSpan().SequenceEqual(output))
                        throw new InvalidDataException($"Native event effects target '{route}' with different content.");
                    if (merged is null)
                        lock (publishedPaths) ReserveDestination(destination, publishedPaths);
                    merged = output;
                    rigs.Add(rigKey);
                    bones.UnionWith(customBones);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllBytesAsync(destination, ExportProvenance.MarkG4pk(output), token);
                }
                lock (progressLock)
                {
                    completed++;
                    ReportProgress(progress, "animation", $"{completed}/{total} · {bank.ResourcePath}");
                    if (completed == total || timer.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        GlobalLog.Info("animation_family_registration_progress", new Dictionary<string, object?>
                        { ["completed"] = completed, ["total"] = total, ["remaining"] = total - completed });
                        timer.Restart();
                    }
                }
            }
        });
        return completed;
    }

    private static string GetSafeDestination(string stagingPath, string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)
            || virtualPath.Any(char.IsControl)
            || Path.IsPathRooted(virtualPath)
            || virtualPath.Contains("..", StringComparison.Ordinal)
            || virtualPath.Contains('\\'))
            throw new InvalidDataException($"Unsafe export destination '{virtualPath}'.");
        var destination = Path.GetFullPath(
            Path.Combine(stagingPath, virtualPath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(stagingPath, destination);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Export destination '{virtualPath}' escapes staging.");
        return destination;
    }
}
