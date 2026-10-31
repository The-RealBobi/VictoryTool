using VictoryTool.Application.Characters;

namespace VictoryTool.Application.Projects;

public sealed record SavedDraftOrigin(string PackagePath, long Length, long WriteTimeUtcTicks, string? Sha256 = null);

public sealed record ProjectEditorState(
    int Version,
    CharacterDraft? ActiveDraft,
    Guid? ActiveDraftId,
    IReadOnlyDictionary<string, CharacterDraft> SavedDrafts,
    string? SourcePackagePath,
    string? LastSavedPackagePath,
    string? SelectedCharacterId,
    string Step,
    bool ResumeEditor,
    string? GameDumpRoot,
    long? SourceLength,
    long? SourceWriteTimeUtcTicks)
{
    public IReadOnlyDictionary<string, SavedDraftOrigin> SavedOrigins { get; init; } = new Dictionary<string, SavedDraftOrigin>();
}
