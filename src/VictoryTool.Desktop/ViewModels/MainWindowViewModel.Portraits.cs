using System.Windows.Input;
using Avalonia.Media.Imaging;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Desktop.Assets;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void SchedulePortraitLoad(CharacterCatalogItem? character)
    {
        if (_disposed) return;
        var generation = ++_portraitGeneration;
        _portraitCancellation?.Cancel();
        _portraitCancellation?.Dispose();
        _portraitCancellation = null;
        ReplacePortraitResults(null, null);
        SelectedPortraitDiagnosticCode = null;
        if (character is null || _portraitLoader is null) return;

        var cancellation = new CancellationTokenSource();
        _portraitCancellation = cancellation;
        _ = LoadPortraitsAsync(character, generation, cancellation);
    }

    private async Task LoadPortraitsAsync(
        CharacterCatalogItem character,
        long generation,
        CancellationTokenSource cancellation)
    {
        var standardTask = LoadPortraitSafelyAsync(
            new CharacterPortraitRequest(character, CharacterPortraitKind.Standard), cancellation.Token);
        var uniformTask = LoadPortraitSafelyAsync(
            new CharacterPortraitRequest(character, CharacterPortraitKind.UniformCompatible), cancellation.Token);
        var published = false;
        try
        {
            await Task.WhenAll(standardTask, uniformTask);
            if (cancellation.IsCancellationRequested || generation != _portraitGeneration
                || !ReferenceEquals(SelectedCharacter, character)) return;
            ReplacePortraitResults(standardTask.Result, uniformTask.Result);
            published = true;
            SelectedPortraitDiagnosticCode = standardTask.Result.DiagnosticCode ?? uniformTask.Result.DiagnosticCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (!published)
            {
                if (standardTask.IsCompletedSuccessfully) standardTask.Result.Dispose();
                if (uniformTask.IsCompletedSuccessfully) uniformTask.Result.Dispose();
            }
        }
    }

    private void ReplacePortraitResults(
        CharacterPortraitLoadResult? standard,
        CharacterPortraitLoadResult? uniform)
    {
        var previousStandard = _standardPortraitResult;
        var previousUniform = _uniformPortraitResult;
        _standardPortraitResult = standard;
        _uniformPortraitResult = uniform;
        OnPropertyChanged(nameof(SelectedPortrait));
        OnPropertyChanged(nameof(PlayerCardBackPortrait));
        OnPropertyChanged(nameof(PlayerCardFrontPortrait));
        OnPropertyChanged(nameof(AppearancePortrait));
        OnPropertyChanged(nameof(AppearanceBackPortrait));
        OnPropertyChanged(nameof(AppearanceFrontPortrait));
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
        previousStandard?.Dispose();
        previousUniform?.Dispose();
    }

    private void SchedulePlayerCardAssetLoad(
        CharacterCatalogItem? character,
        CharacterVariantSummary? variant)
    {
        if (_disposed) return;
        _playerCardAssetCancellation?.Cancel();
        _playerCardAssetCancellation?.Dispose();
        _playerCardAssetCancellation = null;
        var generation = ++_playerCardAssetGeneration;
        ReplacePlayerCardAssets(null);
        if (character is null || _playerCardAssetLoader is null || _playerCardAssetProfile is null)
            return;

        var cancellation = new CancellationTokenSource();
        _playerCardAssetCancellation = cancellation;
        _ = LoadPlayerCardAssetsAsync(character, variant, generation, cancellation);
    }

    private async Task LoadPlayerCardAssetsAsync(
        CharacterCatalogItem character,
        CharacterVariantSummary? variant,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            var locale = PlayerCardLocale;
            var source = ActiveDraft?.SourceCharacterId is { } sourceId
                ? Characters.FirstOrDefault(item => item.Id == sourceId) ?? character
                : character;
            if (ActiveDraft is not null && source.PortraitMetadata != character.PortraitMetadata)
                source = source with { PortraitMetadata = character.PortraitMetadata };
            var assets = await _playerCardAssetLoader!.LoadAsync(
                _playerCardAssetProfile!,
                new PlayerCardAssetRequest(
                    source,
                    variant,
                    locale,
                    ActiveDraftBodyType,
                    ParseNullableInt(ActiveDraft?.Fields.GetValueOrDefault("Identity.Gender")),
                    ActiveDraft?.Models?.UniformModel,
                    ActiveDraft?.Models?.ChestSize,
                    uint.TryParse(ActiveDraft?.Models?.SkinColorRgba, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var color) ? color : null),
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || generation != _playerCardAssetGeneration
                || !ReferenceEquals(SelectedCharacter, character)
                || !ReferenceEquals(SelectedVariant, variant))
            {
                assets.Dispose();
                return;
            }
            ReplacePlayerCardAssets(assets);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            GlobalLog.Error("player_card_preview_failed", exception);
            if (!cancellation.IsCancellationRequested && generation == _playerCardAssetGeneration)
                ReplacePlayerCardAssets(new PlayerCardAssetSet(null, null, null, null, ["assets.preview_failed"]));
        }
    }

    private void ReplacePlayerCardAssets(PlayerCardAssetSet? assets)
    {
        var previous = _playerCardAssets;
        _playerCardAssets = assets;
        OnPropertyChanged(nameof(PlayerCardPositionIcon));
        OnPropertyChanged(nameof(PlayerCardGenderIcon));
        OnPropertyChanged(nameof(PlayerCardBodyTypeIcon));
        OnPropertyChanged(nameof(PlayerCardUniform));
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
        OnPropertyChanged(nameof(HasPlayerCardPositionIcon));
        OnPropertyChanged(nameof(HasPlayerCardGenderIcon));
        OnPropertyChanged(nameof(HasPlayerCardBodyTypeIcon));
        OnPropertyChanged(nameof(ShowPlayerCardPositionId));
        OnPropertyChanged(nameof(ShowPlayerCardGenderId));
        OnPropertyChanged(nameof(ShowPlayerCardBodyTypeId));
        OnPropertyChanged(nameof(PlayerCardAssetDiagnosticCodes));
        previous?.Dispose();
    }

    private long _portraitGeneration;
    private long _draftPortraitGeneration;
    private CancellationTokenSource? _draftPortraitCancellation;
    private CharacterPortraitLoadResult? _standardPreviewResult;
    private CharacterPortraitLoadResult? _uniformPreviewResult;
    private string? _standardPreviewPath;
    private string? _uniformPreviewPath;
    private bool _isStandardPortraitPreviewLoading;
    private bool _isUniformPortraitPreviewLoading;
    private ICommand? _reloadPortraitPreviewsCommand;

    public string ReloadPortraitPreviewsLabel => Workflow("Reload previews");
    public Bitmap? StandardPortraitPreview => _standardPreviewResult?.Bitmap;
    public Bitmap? UniformPortraitPreview => _uniformPreviewResult?.Bitmap;
    public bool IsStandardPortraitPreviewLoading => _isStandardPortraitPreviewLoading;
    public bool IsUniformPortraitPreviewLoading => _isUniformPortraitPreviewLoading;
    public string StandardPortraitPreviewStatus => GetPortraitPreviewStatus(
        _standardPreviewPath, _standardPreviewResult, IsStandardPortraitPreviewLoading);
    public string UniformPortraitPreviewStatus => GetPortraitPreviewStatus(
        _uniformPreviewPath, _uniformPreviewResult, IsUniformPortraitPreviewLoading);
    public ICommand ReloadPortraitPreviewsCommand => _reloadPortraitPreviewsCommand ??= new DelegateCommand(() =>
    {
        _standardPreviewPath = null;
        _uniformPreviewPath = null;
        RefreshAppearancePortrait();
        SchedulePortraitLoad(SelectedCharacter);
    });

    private string GetPortraitPreviewStatus(string? path, CharacterPortraitLoadResult? result, bool loading)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (loading) return Workflow("Loading preview…");
        if (result?.Bitmap is not null) return Workflow("Preview loaded.");
        return Workflow("Could not load the preview. Check the file and reload it.");
    }

    private void RefreshAppearancePortrait()
    {
        var standardPath = ActiveDraft?.Assets?.StandardPortraitPath;
        var uniformPath = ActiveDraft?.Assets?.UniformPortraitPath;
        if (string.IsNullOrWhiteSpace(uniformPath) && standardPath?.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase) == true)
            uniformPath = standardPath;
        var standardChanged = !string.Equals(standardPath, _standardPreviewPath, StringComparison.Ordinal);
        var uniformChanged = !string.Equals(uniformPath, _uniformPreviewPath, StringComparison.Ordinal);
        if (!standardChanged && !uniformChanged)
        {
            NotifyPortraitPreviewChanged();
            return;
        }

        _draftPortraitCancellation?.Cancel();
        _draftPortraitCancellation?.Dispose();
        _draftPortraitCancellation = null;
        var generation = ++_draftPortraitGeneration;
        _standardPreviewPath = standardPath;
        _uniformPreviewPath = uniformPath;
        var previousStandard = standardChanged ? _standardPreviewResult : null;
        var previousUniform = uniformChanged ? _uniformPreviewResult : null;
        if (standardChanged) _standardPreviewResult = null;
        if (uniformChanged) _uniformPreviewResult = null;
        _appearancePortrait = _standardPreviewPath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
            ? StandardPortraitPreview
            : _uniformPreviewPath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ? UniformPortraitPreview : null;
        _isStandardPortraitPreviewLoading = (standardChanged || _isStandardPortraitPreviewLoading)
            && IsOperationalPath(standardPath) && _portraitLoader is not null;
        _isUniformPortraitPreviewLoading = (uniformChanged || _isUniformPortraitPreviewLoading)
            && IsOperationalPath(uniformPath) && _portraitLoader is not null;
        NotifyPortraitPreviewChanged();
        previousStandard?.Dispose();
        previousUniform?.Dispose();
        if (!_isStandardPortraitPreviewLoading && !_isUniformPortraitPreviewLoading) return;

        var cancellation = new CancellationTokenSource();
        _draftPortraitCancellation = cancellation;
        var character = new CharacterCatalogItem(
            ActiveDraft!.SymbolicId, ActiveDraft.DisplayName, CharacterDataConfidence.Parsed, null,
            StandardPortraitResourcePath: standardPath,
            UniformPortraitResourcePath: uniformPath);
        if (_isStandardPortraitPreviewLoading)
            _ = LoadDraftPortraitPreviewAsync(character, CharacterPortraitKind.Standard, generation, cancellation);
        if (_isUniformPortraitPreviewLoading)
            _ = LoadDraftPortraitPreviewAsync(character, CharacterPortraitKind.UniformCompatible, generation, cancellation);
    }

    private async Task LoadDraftPortraitPreviewAsync(
        CharacterCatalogItem character,
        CharacterPortraitKind kind,
        long generation,
        CancellationTokenSource cancellation)
    {
        var result = await LoadPortraitSafelyAsync(new CharacterPortraitRequest(character, kind), cancellation.Token);
        if (cancellation.IsCancellationRequested || generation != _draftPortraitGeneration)
        {
            result.Dispose();
            return;
        }
        if (kind == CharacterPortraitKind.Standard)
        {
            _standardPreviewResult = result;
            _isStandardPortraitPreviewLoading = false;
        }
        else
        {
            _uniformPreviewResult = result;
            _isUniformPortraitPreviewLoading = false;
        }
        _appearancePortrait = _standardPreviewPath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
            ? StandardPortraitPreview
            : _uniformPreviewPath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ? UniformPortraitPreview : null;
        NotifyPortraitPreviewChanged();
    }

    private async Task<CharacterPortraitLoadResult> LoadPortraitSafelyAsync(
        CharacterPortraitRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _portraitLoader!.LoadAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CharacterPortraitLoadResult(null, "assets.preview_cancelled");
        }
        catch (Exception exception)
        {
            GlobalLog.Error("portrait_preview_failed", exception);
            return new CharacterPortraitLoadResult(null, "assets.preview_failed");
        }
    }

    private void NotifyPortraitPreviewChanged()
    {
        OnPropertyChanged(nameof(ReloadPortraitPreviewsLabel));
        OnPropertyChanged(nameof(StandardPortraitPreview));
        OnPropertyChanged(nameof(UniformPortraitPreview));
        OnPropertyChanged(nameof(StandardPortraitPreviewStatus));
        OnPropertyChanged(nameof(UniformPortraitPreviewStatus));
        OnPropertyChanged(nameof(IsStandardPortraitPreviewLoading));
        OnPropertyChanged(nameof(IsUniformPortraitPreviewLoading));
        OnPropertyChanged(nameof(AppearancePortrait));
        OnPropertyChanged(nameof(AppearanceBackPortrait));
        OnPropertyChanged(nameof(AppearanceFrontPortrait));
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
        NotifyReadinessChanged();
    }
}
