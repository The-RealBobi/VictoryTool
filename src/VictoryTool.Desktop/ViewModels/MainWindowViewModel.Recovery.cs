using System.Security.Cryptography;
using System.Windows.Input;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Projects;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, SavedDraftOrigin> _savedOrigins = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recoveryWriteGate = new(1, 1);
    private CancellationTokenSource? _recoveryDebounce;
    private bool _restoringRecovery;
    private bool _recoveredProjectAwaitingDump;
    private Action? _pendingRecoveryClose;
    private bool _isRecoveryCloseFailureVisible;
    private ICommand? _retryRecoveryCloseCommand;
    private ICommand? _cancelRecoveryCloseCommand;
    private ICommand? _closeWithoutRecoveryCommand;

    public bool IsRecoveryCloseFailureVisible
    {
        get => _isRecoveryCloseFailureVisible;
        private set { if (SetProperty(ref _isRecoveryCloseFailureVisible, value)) NotifyOperationStateChanged(); }
    }
    public string RecoveryCloseFailureMessage => Workflow("Session recovery could not be saved. Retry, cancel closing, or close without saving the session.");
    public string RetryRecoveryCloseLabel => Workflow("Retry");
    public string CloseWithoutRecoveryLabel => Workflow("Close without saving the session");
    public ICommand RetryRecoveryCloseCommand => _retryRecoveryCloseCommand ??= new AsyncCommand(async () =>
    {
        if (!IsBusy && _pendingRecoveryClose is { } close) await CompleteCloseAsync(close);
    });
    public ICommand CancelRecoveryCloseCommand => _cancelRecoveryCloseCommand ??= new DelegateCommand(() =>
    {
        if (IsBusy) return;
        _pendingRecoveryClose = null;
        IsRecoveryCloseFailureVisible = false;
    });
    public ICommand CloseWithoutRecoveryCommand => _closeWithoutRecoveryCommand ??= new DelegateCommand(() =>
    {
        if (IsBusy) return;
        var close = _pendingRecoveryClose;
        _pendingRecoveryClose = null;
        IsRecoveryCloseFailureVisible = false;
        close?.Invoke();
    });

    private void QueueRecoverySave()
    {
        if (_disposed || _restoringRecovery || _projectStore is null || string.IsNullOrWhiteSpace(_recoveryRoot)) return;
        _recoveryDebounce?.Cancel();
        _recoveryDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _recoveryDebounce = cancellation;
        _ = SaveDelayedRecoveryAsync(cancellation.Token);
    }

    private async Task SaveDelayedRecoveryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            await WriteRecoverySnapshotAsync(CaptureRecoverySnapshot(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task<bool> FlushRecoveryAsync()
    {
        _recoveryDebounce?.Cancel();
        if (_projectStore is null || string.IsNullOrWhiteSpace(_recoveryRoot) || _restoringRecovery) return true;
        return await WriteRecoverySnapshotAsync(CaptureRecoverySnapshot(), CancellationToken.None);
    }

    private ModProjectDocument CaptureRecoverySnapshot()
    {
        var referencedDrafts = Project.Drafts.Select(entry => entry.Draft.SymbolicId).ToHashSet(StringComparer.Ordinal);
        if (ActiveDraft is { } activeDraft) referencedDrafts.Add(activeDraft.SymbolicId);
        var source = ActiveDraft is { } current ? _savedOrigins.GetValueOrDefault(current.SymbolicId) : null;
        var editor = new ProjectEditorState(1, ActiveDraft, _activeDraftId,
            _savedDrafts.Where(pair => referencedDrafts.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            _modificationSourcePath, _lastSavedPackagePath, SelectedCharacter?.Id,
            (WizardStep == WizardStep.Setup ? _resumeWizardStep : WizardStep).ToString(),
            _resumeEditor || ActiveDraft is not null, IsWorkspaceReady ? ActiveGameDumpPath : null,
            source?.Length, source?.WriteTimeUtcTicks)
        {
            SavedOrigins = _savedOrigins.Where(pair => referencedDrafts.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
        return new ModProjectDocument(Project.Id, Project.Name, Project.SchemaVersion,
            Project.Batch.ToArray(), Project.Drafts.ToArray(), Project.FunctionalBankReferenceDataRoot, editor);
    }

    private async Task<bool> WriteRecoverySnapshotAsync(ModProjectDocument snapshot, CancellationToken token)
    {
        var acquired = false;
        try
        {
            await _recoveryWriteGate.WaitAsync(token);
            acquired = true;
            var path = Path.Combine(_recoveryRoot!, $"{snapshot.Id:N}.vrproject");
            await Task.Run(() => _projectStore!.SaveRecoveryAsync(path, snapshot, token), token);
            foreach (var previous in Directory.EnumerateFiles(_recoveryRoot!, "*.vrproject.recovery"))
                if (!string.Equals(previous, path + ".recovery", StringComparison.Ordinal)) File.Delete(previous);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            GlobalLog.Error("project_recovery_save_failed", exception);
            SetWorkflowErrorStatus("Project recovery could not be saved: {0}", exception.Message);
        }
        finally { if (acquired) _recoveryWriteGate.Release(); }
        return false;
    }

    private async Task CompleteCloseAsync(Action close)
    {
        if (_projectStore is null) { close(); return; }
        _pendingRecoveryClose = close;
        IsRecoveryCloseFailureVisible = false;
        SetOperation(DesktopOperation.Saving);
        bool saved;
        try { saved = await FlushRecoveryAsync(); }
        finally { SetOperation(DesktopOperation.None); }
        if (!saved) { IsRecoveryCloseFailureVisible = true; return; }
        _pendingRecoveryClose = null;
        close();
    }

    public async Task RestoreLatestRecoveryAsync()
    {
        if (IsBusy || ActiveDraft is not null || Project.Drafts.Count > 0 || Project.Batch.Count > 0
            || _projectStore is null || string.IsNullOrWhiteSpace(_recoveryRoot) || !Directory.Exists(_recoveryRoot)) return;
        _restoringRecovery = true;
        try
        {
            var path = Directory.EnumerateFiles(_recoveryRoot, "*.vrproject.recovery")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (path is null) return;
            SetOperation(DesktopOperation.LoadingPackage);
            var restored = await Task.Run(() => _projectStore.LoadAsync(path, CancellationToken.None));
            var editor = restored.EditorState;
            if (!IsWorkspaceReady && !string.IsNullOrWhiteSpace(editor?.GameDumpRoot) && Directory.Exists(editor.GameDumpRoot))
            {
                GameDumpInput = editor.GameDumpRoot;
                await LoadGameDumpAsync();
            }
            var sourceChanged = false;
            if (editor is not null && _packageService is not null)
            {
                var saved = editor.SavedDrafts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var origins = editor.SavedOrigins.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (origins.Count == 0 && editor.ActiveDraft is { } legacy && editor.SourcePackagePath is { } legacyPath
                    && editor.SourceLength is { } length && editor.SourceWriteTimeUtcTicks is { } ticks)
                    origins[legacy.SymbolicId] = new SavedDraftOrigin(legacyPath, length, ticks);
                foreach (var pair in origins.ToArray())
                {
                    if (!saved.TryGetValue(pair.Key, out var baseline)) continue;
                    var origin = pair.Value;
                    if (!await Task.Run(() => OriginMatches(origin)))
                    {
                        if (editor.ActiveDraft?.SymbolicId == pair.Key)
                        {
                            sourceChanged = true;
                            editor = editor with { SourcePackagePath = null };
                        }
                        continue;
                    }
                    CharacterDraft hydrated;
                    try
                    {
                        hydrated = await Task.Run(() => _packageService.LoadAsync(origin.PackagePath, CancellationToken.None));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                    {
                        GlobalLog.Error("project_recovery_source_unavailable", exception);
                        if (editor.ActiveDraft?.SymbolicId == pair.Key)
                        {
                            sourceChanged = true;
                            editor = editor with { SourcePackagePath = null };
                        }
                        continue;
                    }
                    saved[pair.Key] = RebaseRecoveredPaths(baseline, baseline, hydrated);
                    if (editor.ActiveDraft is { } active && active.SymbolicId == pair.Key)
                        editor = editor with { ActiveDraft = RebaseRecoveredPaths(active, baseline, hydrated) };
                    foreach (var entry in restored.Drafts.Where(entry => entry.Draft.SymbolicId == pair.Key).ToArray())
                        restored.UpdateDraft(entry.Id, RebaseRecoveredPaths(entry.Draft, baseline, hydrated));
                }
                editor = editor with { SavedDrafts = saved, SavedOrigins = origins };
            }
            Project = restored;
            OnPropertyChanged(nameof(Project));
            _savedOrigins.Clear();
            if (editor is not null)
                foreach (var pair in editor.SavedOrigins) _savedOrigins[pair.Key] = pair.Value;
            _savedDrafts.Clear();
            if (editor is not null)
                foreach (var pair in editor.SavedDrafts) _savedDrafts[pair.Key] = pair.Value;
            ClearRosterPortraits();
            foreach (var item in Characters.Where(item => item.Origin == CharacterOrigin.Custom).ToArray()) Characters.Remove(item);
            _draftIdsByRosterId.Clear();
            foreach (var draft in Project.Drafts) AddDraftToRoster(draft, select: false);
            PackagePaths.ReplaceWith(Project.Batch.Select(entry => entry.PackagePath));
            RefreshBatch();
            ApplyRecoveredEditor(editor);
            _recoveredProjectAwaitingDump = !IsWorkspaceReady;
            SetWorkflowStatus(sourceChanged
                ? "Session recovered. The original package changed or is unavailable; choose a new save destination."
                : "Previous session recovered.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            GlobalLog.Error("project_recovery_restore_failed", exception);
            SetWorkflowErrorStatus("The session could not be recovered: {0}", exception.Message);
        }
        finally
        {
            _restoringRecovery = false;
            SetOperation(DesktopOperation.None);
        }
    }

    private void ApplyRecoveredEditor(ProjectEditorState? editor)
    {
        if (editor is null) return;
        SelectedCharacter = Characters.FirstOrDefault(item => item.Id == editor.SelectedCharacterId);
        ActiveDraft = editor.ActiveDraft;
        _activeDraftId = editor.ActiveDraftId is { } id && Project.Drafts.Any(entry => entry.Id == id) ? id : null;
        _savedDraft = ActiveDraft is { } active ? _savedDrafts.GetValueOrDefault(active.SymbolicId) : null;
        _modificationSourcePath = editor.SourcePackagePath;
        _lastSavedPackagePath = editor.LastSavedPackagePath;
        _resumeEditor = editor.ResumeEditor;
        _resumeWizardStep = Enum.TryParse<WizardStep>(editor.Step, out var step) && Enum.IsDefined(step) ? step : WizardStep.Details;
        SetTechniqueVariantChoicesFromDraft(ActiveDraft);
        SchedulePlayerCardAssetLoad(SelectedCharacter, SelectedVariant);
        OnPropertyChanged(nameof(HasModificationSource));
        NotifyReadinessChanged();
        if (IsWorkspaceReady && ActiveDraft is not null && _resumeEditor)
        {
            ActiveWorkspace = WorkspaceKind.Characters;
            WizardStep = _resumeWizardStep;
        }
        else WizardStep = WizardStep.Setup;
    }

    private static SavedDraftOrigin? ReadSavedOrigin(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        using var stream = File.OpenRead(path);
        return new SavedDraftOrigin(Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool OriginMatches(SavedDraftOrigin origin)
    {
        try
        {
            var current = ReadSavedOrigin(origin.PackagePath);
            return current is not null && current.Length == origin.Length && current.WriteTimeUtcTicks == origin.WriteTimeUtcTicks
                && (origin.Sha256 is null || current.Sha256 == origin.Sha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private CharacterDraft RebaseRecoveredPaths(CharacterDraft draft, CharacterDraft baseline, CharacterDraft hydrated)
    {
        foreach (var field in new[] { "Assets.StandardPortraitPath", "Assets.UniformPortraitPath", "Models.HeadModelPath", "Models.BodyModelPath", "Models.UniformModelPath", "Models.ShoesModelPath" })
        {
            var original = RecoveryResourcePath(baseline, field);
            var replacement = RecoveryResourcePath(hydrated, field);
            if (original is not null && replacement is not null && string.Equals(RecoveryResourcePath(draft, field), original, StringComparison.Ordinal))
            {
                draft = _draftService.Update(draft, field, replacement);
                var sourceField = field switch
                {
                    "Assets.StandardPortraitPath" => "Assets.StandardPortraitSourcePath",
                    "Assets.UniformPortraitPath" => "Assets.UniformPortraitSourcePath",
                    _ => null,
                };
                if (sourceField is not null && string.Equals(draft.Fields.GetValueOrDefault(sourceField), original, StringComparison.Ordinal))
                {
                    var fields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    fields[sourceField] = replacement;
                    draft = draft with { Fields = fields };
                }
            }
        }
        if (!ReferenceEquals(draft.AnimationProfiles, baseline.AnimationProfiles)
            && CharacterDraftContent.Equivalent(baseline with { AnimationProfiles = draft.AnimationProfiles }, baseline))
            draft = draft with { AnimationProfiles = baseline.AnimationProfiles };
        return draft;
    }

    private static string? RecoveryResourcePath(CharacterDraft draft, string field) => field switch
    {
        "Assets.StandardPortraitPath" => draft.Assets?.StandardPortraitPath,
        "Assets.UniformPortraitPath" => draft.Assets?.UniformPortraitPath,
        "Models.HeadModelPath" => draft.Models?.HeadModelPath,
        "Models.BodyModelPath" => draft.Models?.BodyModelPath,
        "Models.UniformModelPath" => draft.Models?.UniformModelPath,
        "Models.ShoesModelPath" => draft.Models?.ShoesModelPath,
        _ => null,
    };
}
