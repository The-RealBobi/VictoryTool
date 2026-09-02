using System.Text.Json;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Packages;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Exporting;

public sealed record ExportExecutionResult(
    string OutputPath,
    string ReportPath,
    int PublishedFileCount);

public interface IExportExecutor
{
    Task<ExportExecutionResult> ExecuteAsync(ExportPlan plan, CancellationToken cancellationToken);
}

public sealed class ExportExecutor : IExportExecutor
{
    private readonly IVrCharaPackageService _packageService;
    private readonly ICharacterLocalizationT2bWriter _localizationWriter;
    private readonly ICharacterCoreT2bWriter _characterCoreWriter;
    private readonly IShopCharacterT2bWriter _shopCharacterWriter;
    private readonly ICharacterDeliveryWriter _characterDeliveryWriter;
    private readonly ICharacterModelT2bWriter _characterModelWriter;

    public ExportExecutor(
        IVrCharaPackageService? packageService = null,
        ICharacterLocalizationT2bWriter? localizationWriter = null,
        ICharacterCoreT2bWriter? characterCoreWriter = null,
        IShopCharacterT2bWriter? shopCharacterWriter = null,
        ICharacterModelT2bWriter? characterModelWriter = null,
        ICharacterDeliveryWriter? characterDeliveryWriter = null)
    {
        _packageService = packageService ?? new ZipVrCharaPackageService();
        _localizationWriter = localizationWriter ?? new CharacterLocalizationT2bWriter();
        _characterCoreWriter = characterCoreWriter ?? new CharacterCoreT2bWriter();
        _shopCharacterWriter = shopCharacterWriter ?? new ShopCharacterT2bWriter();
        _characterDeliveryWriter = characterDeliveryWriter ?? new CharacterDeliveryWriter();
        _characterModelWriter = characterModelWriter ?? new CharacterModelT2bWriter();
    }

    public async Task<ExportExecutionResult> ExecuteAsync(
        ExportPlan plan,
        CancellationToken cancellationToken)
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
        try
        {
            GlobalLog.Debug("export_file_operations_started", new Dictionary<string, object?>
            {
                ["count"] = plan.FileOperations.Count,
            });
            foreach (var operation in plan.FileOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                await CopyAsync(
                    operation.SourcePath,
                    GetSafeDestination(stagingPath, operation.VirtualPath),
                    publishedPaths,
                    cancellationToken);
            }

            GlobalLog.Debug("export_game_references_started", new Dictionary<string, object?>
            {
                ["count"] = plan.GameReferenceOperations.Count,
            });
            foreach (var operation in plan.GameReferenceOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            foreach (var packageGroup in plan.ResourceOperations.GroupBy(operation => operation.PackagePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = await _packageService.LoadPackageAsync(packageGroup.Key, cancellationToken);
                var resources = package.Resources.ToDictionary(
                    resource => resource.VirtualPath,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var operation in packageGroup)
                {
                    var packageResourcePath = operation.PackageResourcePath ?? operation.DestinationPath;
                    if (!resources.TryGetValue(packageResourcePath, out var resource))
                        throw new InvalidDataException(
                            $"Package resource '{packageResourcePath}' is absent after validation.");
                    var destination = GetSafeDestination(stagingPath, operation.DestinationPath);
                    ReserveDestination(destination, publishedPaths);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var content = resource.Content.ToArray();
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
                var nameTable = await File.ReadAllBytesAsync(namePath, cancellationToken);
                var descriptionTable = await File.ReadAllBytesAsync(descriptionPath, cancellationToken);
                var romanizedNameTable = await File.ReadAllBytesAsync(romanizedPath, cancellationToken);
                var result = _localizationWriter.Append(
                    nameTable,
                    descriptionTable,
                    romanizedNameTable,
                    ids,
                    operation.Text);
                await File.WriteAllBytesAsync(namePath, result.NameTable, cancellationToken);
                await File.WriteAllBytesAsync(descriptionPath, result.DescriptionTable, cancellationToken);
                if (result.RomanizedNameTable is not null)
                    await File.WriteAllBytesAsync(romanizedPath, result.RomanizedNameTable, cancellationToken);
            }

            GlobalLog.Debug("export_models_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterModelOperations.Count,
            });
            foreach (var operation in plan.CharacterModelOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modelPath = GetSafeDestination(stagingPath, operation.ModelTablePath);
                var modelTable = await File.ReadAllBytesAsync(modelPath, cancellationToken);
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
                await File.WriteAllBytesAsync(modelPath, result, cancellationToken);
            }

            GlobalLog.Debug("export_character_core_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterCoreOperations.Count,
            });
            foreach (var operation in plan.CharacterCoreOperations)
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
                var basePath = GetSafeDestination(stagingPath, operation.BaseTablePath);
                var parameterPath = GetSafeDestination(stagingPath, operation.ParameterTablePath);
                var baseTable = await File.ReadAllBytesAsync(basePath, cancellationToken);
                var parameterTable = await File.ReadAllBytesAsync(parameterPath, cancellationToken);
                var sourceBaseTable = operation.TemplateBaseTableSourcePath is null
                    ? baseTable
                    : await File.ReadAllBytesAsync(operation.TemplateBaseTableSourcePath, cancellationToken);
                var sourceParameterTable = operation.TemplateParameterTableSourcePath is null
                    ? parameterTable
                    : await File.ReadAllBytesAsync(operation.TemplateParameterTableSourcePath, cancellationToken);
                var result = _characterCoreWriter.Append(
                    baseTable,
                    parameterTable,
                    sourceBaseTable,
                    sourceParameterTable,
                    request);
                await File.WriteAllBytesAsync(basePath, result.BaseTable, cancellationToken);
                await File.WriteAllBytesAsync(parameterPath, result.ParameterTable, cancellationToken);
            }

            GlobalLog.Debug("export_shop_started", new Dictionary<string, object?>
            {
                ["count"] = plan.ShopCharacterOperations.Count,
            });
            foreach (var operation in plan.ShopCharacterOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shopPath = GetSafeDestination(stagingPath, operation.ShopTablePath);
                var shopTable = await File.ReadAllBytesAsync(shopPath, cancellationToken);
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
                await File.WriteAllBytesAsync(shopPath, result, cancellationToken);
            }

            GlobalLog.Debug("export_delivery_started", new Dictionary<string, object?>
            {
                ["count"] = plan.CharacterDeliveryOperations.Count,
            });
            foreach (var operation in plan.CharacterDeliveryOperations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deliveryPath = GetSafeDestination(stagingPath, operation.DeliveryTablePath);
                var deliveryTable = await File.ReadAllBytesAsync(deliveryPath, cancellationToken);
                var result = _characterDeliveryWriter.Append(
                    deliveryTable,
                    new VictoryTool.CfgBin.CharacterDeliveryWriteRequest(
                        FindAssignedId(plan, operation.BatchEntryId, operation.VariantIndex == 0 ? "delivery" : "deliveryVariant",
                            operation.VariantIndex == 0 ? null : $".variant.{operation.VariantIndex}"),
                        FindAssignedId(plan, operation.BatchEntryId, operation.VariantIndex == 0 ? "deliveryReceived" : "deliveryReceivedVariant",
                            operation.VariantIndex == 0 ? null : $".variant.{operation.VariantIndex}"),
                        FindAssignedParameterId(plan, operation.BatchEntryId, operation.VariantIndex)));
                await File.WriteAllBytesAsync(deliveryPath, result, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var reportPath = Path.Combine(stagingPath, "victorytool-export-report.json");
            var report = new
            {
                formatVersion = 1,
                toolVersion = ApplicationVersion.Current,
                createdUtc = DateTimeOffset.UtcNow,
                platform = plan.Platform.ToString(),
                acquisition = plan.Acquisition.ToString(),
                packageCount = plan.EnabledPackageCount,
                assignedIds = plan.AssignedIds,
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
        return string.Equals(sourceStem, destinationStem, StringComparison.Ordinal)
            ? content
            : document.RenameIdentifier(sourceStem, destinationStem);
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
    }

    private static string FindPortraitTemplateStem(string templatePath)
    {
        var texture = G4TxDocument.Read(File.ReadAllBytes(templatePath)).Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait template has no textures.");
        var marker = texture.Name.IndexOf("_1_", StringComparison.Ordinal);
        return marker > 0 ? texture.Name[..marker] : texture.Name.Split('_')[0];
    }

    private static string GetSafeDestination(string stagingPath, string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)
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
