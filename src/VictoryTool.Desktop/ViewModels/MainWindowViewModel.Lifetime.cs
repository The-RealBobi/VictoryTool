namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cancellation in new[] { _portraitCancellation, _draftPortraitCancellation,
                     _playerCardAssetCancellation, _searchCancellation, _indexCancellation,
                     _planningCancellation, _exportCancellation, _recoveryDebounce })
        {
            cancellation?.Cancel();
        }
        ++_portraitGeneration;
        ++_draftPortraitGeneration;
        ++_playerCardAssetGeneration;
        ClearRosterPortraits();
        foreach (var row in BatchRows) row.Dispose();
        BatchRows.Clear();
        _batchRowsById.Clear();
        ReplacePortraitResults(null, null);
        ReplacePlayerCardAssets(null);
        _standardPreviewResult?.Dispose();
        _uniformPreviewResult?.Dispose();
        _standardPreviewResult = null;
        _uniformPreviewResult = null;
        _appearancePortrait = null;
        _isStandardPortraitPreviewLoading = false;
        _isUniformPortraitPreviewLoading = false;
        NotifyPortraitPreviewChanged();
        NotifyOperationStateChanged();
    }
}
