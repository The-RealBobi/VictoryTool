using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Projects;

public sealed record BatchEntry(
    Guid Id,
    string PackagePath,
    bool IsEnabled,
    DateTimeOffset AddedAtUtc,
    BatchAcquisitionConfiguration? Acquisition = null);

public sealed record BatchAcquisitionConfiguration(
    int? ShopSourceItemId = null,
    int? ShopRarity = null,
    int? ShopSpecialVariant = null,
    bool IsFree = false,
    int? ShopSourceParameterId = null);

public sealed record ProjectDraftEntry(Guid Id, CharacterDraft Draft, string? SourcePackagePath = null);

public sealed class ModProjectDocument
{
    public const int CurrentSchemaVersion = 1;
    private readonly List<BatchEntry> _batch = [];
    private readonly List<ProjectDraftEntry> _drafts = [];

    public ModProjectDocument(
        Guid id,
        string name,
        int schemaVersion,
        IEnumerable<BatchEntry>? batch = null,
        IEnumerable<ProjectDraftEntry>? drafts = null,
        string? functionalBankReferenceDataRoot = null)
    {
        Id = id;
        Name = name;
        SchemaVersion = schemaVersion;
        if (batch is not null) _batch.AddRange(batch);
        if (drafts is not null) _drafts.AddRange(drafts);
        FunctionalBankReferenceDataRoot = NormalizeReferenceRoot(functionalBankReferenceDataRoot);
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public int SchemaVersion { get; }

    public IReadOnlyList<BatchEntry> Batch => _batch;
    public IReadOnlyList<ProjectDraftEntry> Drafts => _drafts;
    public string? FunctionalBankReferenceDataRoot { get; private set; }

    public static ModProjectDocument Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ModProjectDocument(Guid.NewGuid(), name.Trim(), CurrentSchemaVersion);
    }

    public BatchEntry AddPackage(string packagePath)
    {
        ValidatePackagePath(packagePath);
        var entry = new BatchEntry(Guid.NewGuid(), Path.GetFullPath(packagePath), true, DateTimeOffset.UtcNow);
        _batch.Add(entry);
        GlobalLog.Info("project_package_added", new Dictionary<string, object?>
        {
            ["packageId"] = entry.Id,
            ["batchCount"] = _batch.Count,
            ["path"] = entry.PackagePath,
        });
        return entry;
    }

    public bool ContainsPackagePath(string packagePath)
    {
        ValidatePackagePath(packagePath);
        var normalizedPath = Path.GetFullPath(packagePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return _batch.Any(entry => string.Equals(entry.PackagePath, normalizedPath, comparison));
    }

    public void ClearBatch()
    {
        var removedCount = _batch.Count;
        _batch.Clear();
        GlobalLog.Info("project_batch_cleared", new Dictionary<string, object?>
        {
            ["removedCount"] = removedCount,
        });
    }

    public ProjectDraftEntry AddDraft(CharacterDraft draft, string? sourcePackagePath = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var entry = new ProjectDraftEntry(Guid.NewGuid(), draft, sourcePackagePath);
        _drafts.Add(entry);
        GlobalLog.Info("project_draft_added", new Dictionary<string, object?>
        {
            ["draftEntryId"] = entry.Id,
            ["draftCount"] = _drafts.Count,
            ["sourcePackagePath"] = sourcePackagePath,
        });
        return entry;
    }

    public void UpdateDraft(Guid id, CharacterDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var index = _drafts.FindIndex(entry => entry.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Draft {id} was not found.");
        _drafts[index] = _drafts[index] with { Draft = draft };
        GlobalLog.Debug("project_draft_updated", new Dictionary<string, object?>
        {
            ["draftEntryId"] = id,
        });
    }

    public void RemoveDraft(Guid id)
    {
        var removedCount = _drafts.RemoveAll(entry => entry.Id == id);
        GlobalLog.Info("project_draft_removed", new Dictionary<string, object?>
        {
            ["draftEntryId"] = id,
            ["removedCount"] = removedCount,
        });
    }

    public void SetFunctionalBankReferenceDataRoot(string? path)
    {
        FunctionalBankReferenceDataRoot = NormalizeReferenceRoot(path);
        GlobalLog.Debug("project_reference_root_changed", new Dictionary<string, object?>
        {
            ["hasReferenceRoot"] = FunctionalBankReferenceDataRoot is not null,
            ["path"] = FunctionalBankReferenceDataRoot,
        });
    }

    public BatchEntry Duplicate(Guid id)
    {
        var source = Find(id);
        var copy = source with { Id = Guid.NewGuid(), AddedAtUtc = DateTimeOffset.UtcNow };
        _batch.Insert(_batch.IndexOf(source) + 1, copy);
        GlobalLog.Info("project_package_duplicated", new Dictionary<string, object?>
        {
            ["sourcePackageId"] = id,
            ["packageId"] = copy.Id,
            ["batchCount"] = _batch.Count,
        });
        return copy;
    }

    public void Move(Guid id, int targetIndex)
    {
        var entry = Find(id);
        _batch.Remove(entry);
        var normalizedIndex = Math.Clamp(targetIndex, 0, _batch.Count);
        _batch.Insert(normalizedIndex, entry);
        GlobalLog.Debug("project_package_moved", new Dictionary<string, object?>
        {
            ["packageId"] = id,
            ["targetIndex"] = normalizedIndex,
        });
    }

    public void SetEnabled(Guid id, bool isEnabled)
    {
        var index = _batch.FindIndex(entry => entry.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Batch entry {id} was not found.");
        _batch[index] = _batch[index] with { IsEnabled = isEnabled };
        GlobalLog.Debug("project_package_enabled_changed", new Dictionary<string, object?>
        {
            ["packageId"] = id,
            ["isEnabled"] = isEnabled,
        });
    }

    public void SetAcquisition(Guid id, BatchAcquisitionConfiguration? acquisition)
    {
        var index = _batch.FindIndex(entry => entry.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Batch entry {id} was not found.");
        _batch[index] = _batch[index] with { Acquisition = acquisition };
        GlobalLog.Info("project_acquisition_changed", new Dictionary<string, object?>
        {
            ["packageId"] = id,
            ["hasAcquisition"] = acquisition is not null,
            ["isFree"] = acquisition?.IsFree,
        });
    }

    public void Remove(Guid id)
    {
        _batch.Remove(Find(id));
        GlobalLog.Info("project_package_removed", new Dictionary<string, object?>
        {
            ["packageId"] = id,
            ["batchCount"] = _batch.Count,
        });
    }

    private BatchEntry Find(Guid id) =>
        _batch.FirstOrDefault(entry => entry.Id == id)
        ?? throw new KeyNotFoundException($"Batch entry {id} was not found.");

    private static void ValidatePackagePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetExtension(path), ".vrchara", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Character packages must use the .vrchara extension.", nameof(path));
        }
    }

    private static string? NormalizeReferenceRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
