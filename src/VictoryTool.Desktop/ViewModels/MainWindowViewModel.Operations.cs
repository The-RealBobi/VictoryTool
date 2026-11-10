using System.Windows.Input;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Exporting;
using VictoryTool.Application.Projects;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private enum DesktopOperation { None, ChoosingPackage, LoadingPackage, Saving, Planning }
    private DesktopOperation _operation;
    private long _exportPlanRevision;
    private CancellationTokenSource? _planningCancellation;
    private CharacterDraft? _savedDraft;
    private readonly Dictionary<string, CharacterDraft> _savedDrafts = new(StringComparer.Ordinal);
    private Action? _pendingNavigation;
    private bool _isUnsavedChangesPromptVisible;
    private bool _isApplyingEditorSelection;
    private bool _resumeEditor;
    private WizardStep _resumeWizardStep = WizardStep.Details;
    private ICommand? _discardPendingChangesCommand;
    private ICommand? _cancelPendingNavigationCommand;

    public bool IsBusy => _operation != DesktopOperation.None || IsIndexing || IsExporting;
    public bool CanInteract => !_disposed && !IsBusy && !IsUnsavedChangesPromptVisible && !IsPostSavePromptVisible && !IsRecoveryCloseFailureVisible;
    public bool CanSaveDraft => !IsBusy && !IsPostSavePromptVisible && string.IsNullOrEmpty(SaveBlockingReason);
    public bool CanStartExport => !IsBusy && !IsUnsavedChangesPromptVisible && !IsPostSavePromptVisible && string.IsNullOrEmpty(ExportBlockingReason);
    public bool CanCancelOperation => IsExporting || IsIndexing || _planningCancellation is not null;
    public bool HasUnsavedChanges => ActiveDraft is not null && !CharacterDraftContent.Equivalent(ActiveDraft, _savedDraft);
    public string CreateCharacterActionLabel => _resumeEditor && ActiveDraft is not null
        ? Workflow("Continue editing") : WizardText.CreateNewCharacter;
    public string LastSavedPackagePath => _lastSavedPackagePath ?? string.Empty;
    public string BusyMessage => IsExporting ? ExportProgressMessage : IsIndexing ? IndexProgressMessage
        : _operation switch
        {
            DesktopOperation.Saving => Workflow("Saving character…"),
            DesktopOperation.Planning => Workflow("Preparing export…"),
            DesktopOperation.LoadingPackage => Workflow("Opening character…"),
            DesktopOperation.ChoosingPackage => Workflow("Choose a character…"),
            _ => string.Empty,
        };
    public string SaveBlockingReason
    {
        get
        {
            if (ActiveDraft is null) return Workflow("Select or create a character.");
            if (_packageService is null) return Workflow("The .vrchara package service is not configured.");
            if (_customShoesSelected && string.IsNullOrWhiteSpace(ActiveDraftShoesModelPath))
                return Workflow("Select the custom shoes model file.");
            if (_customUniformSelected && string.IsNullOrWhiteSpace(ActiveDraftUniformModelPath))
                return Workflow("Select the custom uniform model file.");
            if (!string.IsNullOrWhiteSpace(_localesJsonPath) && !File.Exists(_localesJsonPath))
                return Workflow("The locales JSON cannot be found. Open it again before saving.");
            foreach (var (path, label) in new[]
                     {
                         (ActiveDraftStandardPortraitPath, Workflow("standard portrait")),
                         (ActiveDraftUniformPortraitPath, Workflow("uniform portrait")),
                         (ActiveDraftHeadModelPath, Workflow("character model")),
                         (ActiveDraftUniformModelPath, Workflow("uniform")),
                         (ActiveDraftShoesModelPath, Workflow("shoes")),
                     })
            {
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && !File.Exists(path))
                    return Workflow("The {0} file cannot be found. Select it again.", label);
            }
            var diagnostic = ActiveDraft.Diagnostics?.FirstOrDefault(item => item.Code != "draft.display_name_required");
            return diagnostic is null ? string.Empty : DraftDiagnosticMessage(diagnostic);
        }
    }
    public string ExportBlockingReason => !IsWorkspaceReady
        ? Workflow("Load the game data.")
        : !Project.Batch.Any(entry => entry.IsEnabled)
            ? Workflow("Add at least one character to incorporate.")
            : GenerateAnimationsEnabled && !GenerateChrAnimations && !GenerateEventAnimations
                ? Workflow("Select CHR animations, EVENT animations, or both.")
                : string.Empty;
    public string UnsavedChangesMessage => Workflow("You have unsaved changes. Save them before continuing?");
    public string DiscardChangesLabel => Workflow("Discard changes");
    public string CancelNavigationLabel => Workflow("Cancel");
    public bool IsUnsavedChangesPromptVisible
    {
        get => _isUnsavedChangesPromptVisible;
        private set { if (SetProperty(ref _isUnsavedChangesPromptVisible, value)) NotifyOperationStateChanged(); }
    }
    public ICommand DiscardPendingChangesCommand => _discardPendingChangesCommand ??= new DelegateCommand(DiscardPendingChanges);
    public ICommand CancelPendingNavigationCommand => _cancelPendingNavigationCommand ??= new DelegateCommand(() =>
    {
        if (IsBusy) return;
        _pendingNavigation = null;
        IsUnsavedChangesPromptVisible = false;
    });

    private void SetOperation(DesktopOperation operation)
    {
        _operation = operation;
        NotifyOperationStateChanged();
    }
    private void NotifyOperationStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanCancelOperation));
        OnPropertyChanged(nameof(BusyMessage));
        NotifyReadinessChanged();
    }
    private void NotifyReadinessChanged()
    {
        OnPropertyChanged(nameof(CreateCharacterActionLabel));
        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(SaveBlockingReason));
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(ExportBlockingReason));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanAdvanceWorkflow));
        OnPropertyChanged(nameof(ForwardActionLabel));
        OnPropertyChanged(nameof(ShowWorkflowNavigation));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(LastSavedPackagePath));
    }

    private bool ConfirmEditorReplacement(Action replace)
    {
        if (_isApplyingEditorSelection || IsBusy || _activeDraftId is not null || !HasUnsavedChanges || !HasModificationSource)
            return true;
        RequestAbandonEditor(() =>
        {
            _isApplyingEditorSelection = true;
            try { replace(); }
            finally { _isApplyingEditorSelection = false; }
        });
        return false;
    }

    private void ResetModificationContext()
    {
        _modificationSourcePath = null;
        _lastSavedPackagePath = null;
        OnPropertyChanged(nameof(HasModificationSource));
        NotifyReadinessChanged();
    }

    public void RequestClose(Action close)
    {
        if (IsBusy) return;
        RequestAbandonEditor(() => _ = CompleteCloseAsync(close));
    }
    private void RequestAbandonEditor(Action next)
    {
        if (!HasUnsavedChanges) { next(); return; }
        _pendingNavigation = next;
        IsPostSavePromptVisible = false;
        IsUnsavedChangesPromptVisible = true;
    }
    private void DiscardPendingChanges()
    {
        if (IsBusy) return;
        var next = _pendingNavigation;
        _pendingNavigation = null;
        if (_savedDraft is { } saved)
        {
            ActiveDraft = saved;
            PersistActiveDraft();
            if (_activeDraftId is { } draftId) UpdateDraftRosterItem(draftId, saved);
        }
        else if (_activeDraftId is not null)
            RemoveActiveDraft();
        _activeDraftId = null;
        ActiveDraft = null;
        SelectedCharacter = null;
        ResetModificationContext();
        _resumeEditor = false;
        IsUnsavedChangesPromptVisible = false;
        QueueRecoverySave();
        next?.Invoke();
    }

    public async Task SaveDraftWithPickerAsync(Func<CharacterDraft, Task<string?>> picker)
    {
        if (IsBusy) return;
        if (!CanSaveDraft) { SetLocalizedWorkflowErrorStatus(() => SaveBlockingReason); return; }
        var draft = ActiveDraft!;
        var modificationPath = _modificationSourcePath;
        Action? afterSave = null;
        SetOperation(DesktopOperation.Saving);
        try
        {
            if (!await ApplyEditedLocalesAsync()) return;
            draft = ActiveDraft!;
            var errors = _draftService.Validate(draft).Where(item => item.Code != "draft.display_name_required").ToArray();
            if (errors.Length > 0) { SetLocalizedWorkflowErrorStatus(() => DraftDiagnosticMessage(errors[0])); return; }
            if (modificationPath is not null && _savedOrigins.TryGetValue(draft.SymbolicId, out var origin) && !await Task.Run(() => OriginMatches(origin)))
            {
                modificationPath = null;
                _modificationSourcePath = null;
                OnPropertyChanged(nameof(HasModificationSource));
                SetWorkflowStatus("The source package changed. Choose a destination for your edited character.");
            }
            var target = modificationPath ?? await picker(draft);
            if (string.IsNullOrWhiteSpace(target)) return;
            if (!ReferenceEquals(draft, ActiveDraft))
            {
                SetWorkflowStatus("The character changed. Save again.");
                return;
            }
            if (string.IsNullOrWhiteSpace(ActiveDraft!.Acquisition?.Method))
            {
                ActiveDraft = ActiveDraft with
                {
                    Acquisition = (ActiveDraft.Acquisition ?? new CharacterDraftAcquisition(null, null)) with { Method = "Delivery" },
                };
                PersistActiveDraft();
            }
            draft = ActiveDraft;
            await Task.Run(() => _packageService!.SaveAsync(target, draft, CancellationToken.None));
            _savedDrafts[draft.SymbolicId] = draft;
            if (await Task.Run(() => ReadSavedOrigin(target)) is { } savedOrigin) _savedOrigins[draft.SymbolicId] = savedOrigin;
            _lastSavedPackagePath = target;
            if (_activeDraftId is null)
            {
                _modificationSourcePath = target;
                OnPropertyChanged(nameof(HasModificationSource));
            }
            if (ReferenceEquals(ActiveDraft, draft))
            {
                _savedDraft = draft;
                afterSave = _pendingNavigation;
                _pendingNavigation = null;
                IsUnsavedChangesPromptVisible = false;
                IsPostSavePromptVisible = afterSave is null;
            }
            SetWorkflowStatus("Character saved to {0}.", target);
            QueueRecoverySave();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            GlobalLog.Error("draft_save_failed", exception);
            SetWorkflowErrorStatus("The character could not be saved: {0}", exception.Message);
        }
        finally { SetOperation(DesktopOperation.None); }
        afterSave?.Invoke();
    }

    public async Task LoadModificationWithPickerAsync(Func<Task<string?>> picker)
    {
        if (IsBusy) return;
        if (HasUnsavedChanges && _activeDraftId is null)
        {
            RequestAbandonEditor(() => _ = LoadModificationWithPickerAsync(picker));
            return;
        }
        SetOperation(DesktopOperation.ChoosingPackage);
        try
        {
            var path = await picker();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_packageService is null) { SetWorkflowErrorStatus("The .vrchara package service is not configured."); return; }
            SetOperation(DesktopOperation.LoadingPackage);
            var loadedOrigin = await Task.Run(() => ReadSavedOrigin(path));
            var loaded = await Task.Run(() => _packageService.LoadAsync(path, CancellationToken.None));
            var source = Characters.FirstOrDefault(character => character.Id == loaded.SourceCharacterId);
            var draft = source is null ? loaded : loaded with
            {
                Localization = CharacterLocalizationMapper.FillJapaneseNameFallback(loaded.Localization, source),
            };
            draft = draft with { Diagnostics = _draftService.Validate(draft) };
            _activeDraftId = null;
            SelectedCharacter = Characters.FirstOrDefault(item => item.Id == draft.SourceCharacterId);
            _savedDrafts[draft.SymbolicId] = draft;
            if (loadedOrigin is not null) _savedOrigins[draft.SymbolicId] = loadedOrigin;
            ActiveDraft = draft;
            _savedDraft = draft;
            SetTechniqueVariantChoicesFromDraft(draft);
            _modificationSourcePath = path;
            _lastSavedPackagePath = path;
            OnPropertyChanged(nameof(HasModificationSource));
            SchedulePlayerCardAssetLoad(SelectedCharacter, SelectedVariant);
            ActiveWorkspace = WorkspaceKind.Characters;
            WizardStep = WizardStep.Details;
            SetWorkflowStatus("Character opened for modification.");
            QueueRecoverySave();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            GlobalLog.Error("modification_load_failed", exception);
            SetUserErrorStatusMessage(exception.Message);
        }
        finally { SetOperation(DesktopOperation.None); }
    }

    public async Task ExportBatchAsync(Func<Task<string?>> destinationPicker)
    {
        if (!CanStartExport) { if (!IsBusy) SetLocalizedWorkflowErrorStatus(() => ExportBlockingReason); return; }
        SetOperation(DesktopOperation.Planning);
        var prepared = false;
        try
        {
            if (string.IsNullOrWhiteSpace(ExportOutputPath))
            {
                var destination = await destinationPicker();
                if (string.IsNullOrWhiteSpace(destination)) return;
                SetGeneratedExportOutputPath(destination);
            }
            prepared = await PrepareExportPlanAsync();
        }
        catch (Exception exception)
        {
            ReportExportFailure(exception, preview: true);
        }
        finally { SetOperation(DesktopOperation.None); }
        if (prepared && CurrentExportPlan?.CanExport == true) await ExecuteExportAsync();
    }

    private async Task<bool> PrepareExportPlanAsync()
    {
        using var operation = GlobalLog.BeginOperation("ui_export_preview");
        GlobalLog.Info("export_preview_started", new Dictionary<string, object?>
        {
            ["batchCount"] = Batch.Count,
            ["acquisitionMode"] = AcquisitionMode,
        });
        using var cancellation = new CancellationTokenSource();
        InvalidateExportPlan();
        long revision = -1;
        try
        {
            if (_exportOutputPathGenerated
                && (Directory.Exists(ExportOutputPath) || File.Exists(ExportOutputPath)))
                SetGeneratedExportOutputPath(GetNextExportPath(ExportOutputPath));
            if (AcquisitionMode is AcquisitionMode.Shop or AcquisitionMode.Both)
                EnsureShopAcquisitionDefaults();
            revision = _exportPlanRevision;
            _planningCancellation = cancellation;
            NotifyOperationStateChanged();
            var plan = _playerCardAssetProfile is { } profile
                ? await _exportPlanner.CreatePlanAsync(
                    Project,
                    profile,
                    ExportPlatform,
                    AcquisitionMode,
                    ExportOutputPath,
                    cancellation.Token,
                    new AnimationGenerationSelection(
                        GenerateAnimationsEnabled,
                        GenerateChrAnimations,
                        GenerateEventAnimations))
                : _exportPlanner.CreatePlan(
                    Project,
                    ExportPlatform,
                    AcquisitionMode,
                    ExportOutputPath);
            if (cancellation.IsCancellationRequested || revision != _exportPlanRevision) return false;
            CurrentExportPlan = plan;
            Diagnostics.ReplaceWith(plan.Diagnostics);
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(DiagnosticReport));
            if (plan.CanExport) SetWorkflowStatus("Export plan is ready.");
            else SetLocalizedWorkflowErrorStatus(() => BuildDiagnosticStatus(plan.Diagnostics));
            GlobalLog.Info("export_preview_completed", new Dictionary<string, object?>
            {
                ["canExport"] = CurrentExportPlan.CanExport,
                ["diagnosticCount"] = CurrentExportPlan.Diagnostics.Count,
            });
            return plan.CanExport;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetWorkflowStatus("Preparation cancelled; prepare the export again.");
        }
        catch (Exception exception)
        {
            if (revision < 0 || revision == _exportPlanRevision) ReportExportFailure(exception, preview: true);
        }
        finally
        {
            _planningCancellation = null;
            NotifyOperationStateChanged();
        }
        return false;
    }
}
