using System.Globalization;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Desktop.Localization;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Func<string>? _localizedWorkflowStatus;

    private string Workflow(string english, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, WorkflowText.ForLocale(SelectedLanguageCode, english), arguments);

    private void SetWorkflowStatus(string english, params object?[] arguments) =>
        SetLocalizedWorkflowStatus(() => Workflow(english, arguments));

    private void SetWorkflowErrorStatus(string english, params object?[] arguments)
    {
        SetLocalizedWorkflowErrorStatus(() => Workflow(english, arguments));
    }

    private void SetLocalizedWorkflowErrorStatus(Func<string> text)
    {
        SetLocalizedWorkflowStatus(text);
        _isErrorStatus = true;
        OnPropertyChanged(nameof(ErrorStatusMessage));
    }

    private void SetLocalizedWorkflowStatus(Func<string> text)
    {
        StatusMessage = text();
        _localizedWorkflowStatus = text;
    }

    private void RefreshWorkflowLocalization()
    {
        if (_localizedWorkflowStatus is { } text)
        {
            var wasError = _isErrorStatus;
            SetLocalizedWorkflowStatus(text);
            _isErrorStatus = wasError;
            OnPropertyChanged(nameof(ErrorStatusMessage));
        }
        OnPropertyChanged(nameof(DiagnosticReport));
        OnPropertyChanged(nameof(RefreshBatchPreviewsLabel));
        OnPropertyChanged(nameof(EmptyRosterMessage));
        OnPropertyChanged(nameof(ResetCharacterSearchLabel));
        OnPropertyChanged(nameof(RecoveryCloseFailureMessage));
        OnPropertyChanged(nameof(RetryRecoveryCloseLabel));
        OnPropertyChanged(nameof(CloseWithoutRecoveryLabel));
    }

    private string DraftDiagnosticMessage(CharacterDraftDiagnostic diagnostic) => diagnostic.Code switch
    {
        "draft.display_name_required" or "draft.integer_required" or "draft.model_extension_unsupported" => Workflow(diagnostic.Message),
        _ => diagnostic.Message,
    };

    private string DiagnosticMessage(Diagnostic diagnostic) => diagnostic.Code switch
    {
        "export.package_invalid" when diagnostic.MessageArguments.Count == 1 => Workflow(
            "An enabled .vrchara package is invalid, unreadable, or incompatible: {0}", diagnostic.MessageArguments[0]),
        "export.id_assignment_failed" when diagnostic.MessageArguments.Count == 1 => Workflow(
            "Numeric character IDs could not be assigned: {0}", diagnostic.MessageArguments[0]),
        "batch.empty" or "export.dump_required" => Workflow(diagnostic.Message),
        _ => diagnostic.Message,
    };

    private string DiagnosticRecoveryAction(Diagnostic diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.RecoveryAction)) return string.Empty;
        var action = diagnostic.Code is "batch.empty" or "export.dump_required" or "export.package_invalid" or "export.id_assignment_failed"
            ? Workflow(diagnostic.RecoveryAction)
            : diagnostic.RecoveryAction;
        return Workflow(" Action: {0}", action);
    }
}
