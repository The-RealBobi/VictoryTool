using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Exporting;
using VictoryTool.Application.Profiles;
using VictoryTool.Application.Projects;
using VictoryTool.Application.Packages;
using VictoryTool.Application.Workspaces;
using VictoryTool.Application.Settings;
using VictoryTool.Application.Statistics;
using VictoryTool.Application.Text;
using VictoryTool.Desktop.Localization;
using VictoryTool.Desktop.Assets;
using VictoryTool.Desktop.Storage;

namespace VictoryTool.Desktop.ViewModels;

public enum WorkspaceKind
{
    Project,
    Characters,
    Batch,
    Export,
    Research,
}

public enum WizardStep
{
    Setup,
    Character,
    Details,
    Attributes,
    Skills,
    Appearance,
    Acquisition,
    Review,
}

public enum LayoutDensity
{
    Compact,
    Regular,
    Wide,
}

public enum CharacterEditorSection
{
    Identity,
    Gameplay,
    Statistics,
    Skills,
    Models,
    Assets,
    Localization,
    Acquisition,
    Advanced,
}

public sealed record NavigationItem(WorkspaceKind Workspace, string Label, string Glyph);
public sealed record IdentityChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PlayerCardSkillRow(int Slot, int SkillId, string Name, int UnlockLevel);
public sealed record PlayerCardStatRow(CharacterStatKind Kind, string Name, int Value);
public sealed record CharacterVariantChoice(CharacterVariantSummary Variant, string Label);

public sealed class CharacterLocalizationEditorRow : ObservableObject
{
    private readonly Action<string, string, string?> _update;
    private string _localizedName;
    private string _description;
    private string _romanizedName;
    private string _japaneseName;
    private string _shortName;
    private string _upperName;
    private string _applicationLocale = "en";

    public CharacterLocalizationEditorRow(
        string locale,
        CharacterDraftLocalizedText text,
        Action<string, string, string?> update)
    {
        Locale = locale;
        _localizedName = text.LocalizedName ?? string.Empty;
        _description = text.Description ?? string.Empty;
        _romanizedName = text.RomanizedName ?? string.Empty;
        _japaneseName = text.JapaneseName ?? string.Empty;
        _shortName = text.ShortName ?? string.Empty;
        _upperName = text.UpperName ?? string.Empty;
        _update = update;
    }

    public string Locale { get; }
    public string Label => LanguageLabel(Locale, _applicationLocale);

    public void SetApplicationLanguage(string locale)
    {
        if (string.Equals(_applicationLocale, locale, StringComparison.OrdinalIgnoreCase)) return;
        _applicationLocale = locale;
        OnPropertyChanged(nameof(Label));
    }

    private static string LanguageLabel(string locale, string applicationLocale)
    {
        var spanish = string.Equals(applicationLocale, "es", StringComparison.OrdinalIgnoreCase);
        return locale.ToLowerInvariant() switch
        {
            "de" => spanish ? "Alemán" : "German",
            "en" => spanish ? "Inglés" : "English",
            "es" => spanish ? "Español" : "Spanish",
            "fr" => spanish ? "Francés" : "French",
            "it" => spanish ? "Italiano" : "Italian",
            "ja" => spanish ? "Japonés" : "Japanese",
            "pt" => spanish ? "Portugués" : "Portuguese",
            "zh_hans" => spanish ? "Chino simplificado" : "Chinese (Simplified)",
            "zh_hant" => spanish ? "Chino tradicional" : "Chinese (Traditional)",
            _ => locale.Replace('_', '-').ToUpperInvariant(),
        };
    }

    public string LocalizedName
    {
        get => _localizedName;
        set { if (SetProperty(ref _localizedName, value)) _update(Locale, nameof(LocalizedName), value); }
    }

    public string Description
    {
        get => _description;
        set { if (SetProperty(ref _description, value)) _update(Locale, nameof(Description), value); }
    }

    public string RomanizedName
    {
        get => _romanizedName;
        set { if (SetProperty(ref _romanizedName, value)) _update(Locale, nameof(RomanizedName), value); }
    }

    public string JapaneseName
    {
        get => _japaneseName;
        set { if (SetProperty(ref _japaneseName, value)) _update(Locale, nameof(JapaneseName), value); }
    }

    public string ShortName
    {
        get => _shortName;
        set { if (SetProperty(ref _shortName, value)) _update(Locale, nameof(ShortName), value); }
    }

    public string UpperName
    {
        get => _upperName;
        set { if (SetProperty(ref _upperName, value)) _update(Locale, nameof(UpperName), value); }
    }
}

public sealed class CharacterSkillEditorRow : ObservableObject
{
    private readonly Action<int, string, string?> _update;
    private readonly IReadOnlyList<LocalizedSkill> _skillChoices;
    private readonly string _unlockLevelLabel;
    private string _skillId;
    private string _unlockLevel;

    public CharacterSkillEditorRow(
        CharacterDraftSkillSlot slot,
        IReadOnlyList<LocalizedSkill> skillChoices,
        string unlockLevelLabel,
        Action<int, string, string?> update)
    {
        Slot = slot.Slot;
        Path = slot.Path;
        _skillId = slot.SkillId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _unlockLevel = slot.UnlockLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _skillChoices = skillChoices;
        _unlockLevelLabel = unlockLevelLabel;
        _update = update;
    }

    public int Slot { get; }
    public CharacterSkillPath Path { get; }
    public string PathLabel => Path.ToString();
    public IReadOnlyList<LocalizedSkill> SkillChoices => _skillChoices;
    public string UnlockLevelLabel => _unlockLevelLabel;

    public string SkillId
    {
        get => _skillId;
        set
        {
            if (!SetProperty(ref _skillId, value)) return;
            OnPropertyChanged(nameof(SelectedSkill));
            _update(Slot, nameof(SkillId), value);
        }
    }

    public LocalizedSkill? SelectedSkill
    {
        get => int.TryParse(_skillId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? _skillChoices.FirstOrDefault(choice => choice.Id == unchecked((uint)id))
            : null;
        set => SkillId = value is null ? string.Empty : unchecked((int)value.Id).ToString(CultureInfo.InvariantCulture);
    }

    public string UnlockLevel
    {
        get => _unlockLevel;
        set { if (SetProperty(ref _unlockLevel, value)) _update(Slot, nameof(UnlockLevel), value); }
    }

    public decimal? UnlockLevelValue
    {
        get => decimal.TryParse(_unlockLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
        set => UnlockLevel = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

public sealed class CharacterRosterRow : ObservableObject, IDisposable
{
    private Bitmap? _uniformPortrait;
    private CharacterCatalogItem _character;
    private bool _isPortraitLoading;

    public CharacterRosterRow(CharacterCatalogItem character) => _character = character;

    public CharacterCatalogItem Character
    {
        get => _character;
        set => SetProperty(ref _character, value);
    }
    public Bitmap? UniformPortrait
    {
        get => _uniformPortrait;
        set
        {
            if (ReferenceEquals(_uniformPortrait, value)) return;
            var previous = _uniformPortrait;
            _uniformPortrait = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUniformPortrait));
            previous?.Dispose();
        }
    }
    public bool HasUniformPortrait => UniformPortrait is not null;
    public bool IsPortraitLoading
    {
        get => _isPortraitLoading;
        set => SetProperty(ref _isPortraitLoading, value);
    }
    public void Dispose() => UniformPortrait?.Dispose();
}

public sealed class MainWindowViewModel : ObservableObject
{
    private const int RosterPortraitConcurrency = 2;
    private readonly ICharacterCatalogService _catalogService;
    private readonly ICharacterCloneService _cloneService;
    private readonly ICharacterDraftService _draftService;
    private readonly ICharacterPortraitLoader? _portraitLoader;
    private CancellationTokenSource? _rosterPortraitCancellation;
    private readonly IPlayerCardAssetLoader? _playerCardAssetLoader;
    private ICharacterStatCalculator _characterStatCalculator;
    private readonly bool _loadDumpStatistics;
    private readonly Dictionary<string, Guid> _draftIdsByRosterId = new(StringComparer.Ordinal);
    private readonly IExportPlanner _exportPlanner;
    private readonly IExportExecutor _exportExecutor;
    private readonly IApplicationSettingsStore? _settingsStore;
    private readonly IModProjectStore? _projectStore;
    private readonly IVrCharaPackageService? _packageService;
    private readonly string? _recoveryRoot;
    private string _gameDumpInput = string.Empty;
    private string _activeGameDumpPath = string.Empty;
    private string _indexProgressMessage = string.Empty;
    private string _packageInput = string.Empty;
    private string _searchText = string.Empty;
    private CharacterAffinity? _selectedAffinity;
    private CharacterOrigin? _selectedOrigin;
    private string? _selectedSeries;
    private int? _selectedAcademicYear;
    private int? _selectedGender;
    private int? _selectedBodyType;
    private string? _selectedPosition;
    private int? _selectedPlayStyle;
    private int? _selectedRank;
    private int? _selectedSpecialRarity;
    private CharacterSort _selectedCharacterSort = CharacterSort.DisplayName;
    private string _statusMessage = "Select a game dump to begin.";
    private bool _isWorkspaceReady;
    private bool _isIndexing;
    private CharacterCatalogItem? _selectedCharacter;
    private CharacterRosterRow? _selectedRosterRow;
    private bool _isUpdatingRosterRows;
    private CharacterVariantSummary? _selectedVariant;
    private IReadOnlyList<CharacterVariantChoice> _techniqueVariantChoices = [];
    private CharacterVariantChoice? _selectedTechniqueVariantChoice;
    private int _playerCardLevel = 1;
    private CharacterDraft? _activeDraft;
    private Guid? _activeDraftId;
    private bool _isUpdatingActiveDraftField;
    private bool _isUpdatingBodyTypeChoice;
    private bool _isRemoveDraftConfirmationVisible;
    private WorkspaceKind _activeWorkspace = WorkspaceKind.Project;
    private WizardStep _wizardStep = WizardStep.Setup;
    private ExportPlatform _exportPlatform = ExportPlatform.Pc;
    // Delivery is the safe default for newly imported character packages. Shop
    // remains available as an explicit batch choice with its own requirements.
    private AcquisitionMode _acquisitionMode = AcquisitionMode.Delivery;
    private string _exportOutputPath = string.Empty;
    private bool _exportOutputPathGenerated;
    private ExportPlan? _currentExportPlan;
    private UiText _text = UiText.English;
    private WizardText _wizardText = WizardText.English;
    private string _selectedLanguageCode = "en";
    private LayoutDensity _layoutDensity = LayoutDensity.Regular;
    private double _rosterPaneWidth = 350;
    private double _previewPaneWidth = 300;
    private CharacterEditorSection _activeEditorSection = CharacterEditorSection.Identity;
    private CharacterLocalizationEditorRow? _selectedLocalizationEditorRow;
    private BatchEntry? _selectedBatchEntry;
    private string _draftPackagePath = string.Empty;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _portraitCancellation;
    private CharacterPortraitLoadResult? _standardPortraitResult;
    private CharacterPortraitLoadResult? _uniformPortraitResult;
    private Bitmap? _appearancePortrait;
    private string? _appearancePortraitPath;
    private GameDumpProfile? _playerCardAssetProfile;
    private GameTaxonomyIndex? _gameTaxonomy;
    private readonly Dictionary<(EquipmentCategory Category, string Locale), IReadOnlyList<LocalizedEquipment>>
        _appearanceEquipmentOptions = [];
    private readonly Dictionary<string, IReadOnlyList<LocalizedTeam>> _teamAssociationOptions =
        new(StringComparer.OrdinalIgnoreCase);
    private CharacterTextReferenceIndex? _gameTextReferences;
    private CancellationTokenSource? _playerCardAssetCancellation;
    private long _playerCardAssetGeneration;
    private PlayerCardAssetSet? _playerCardAssets;
    private string? _selectedPortraitDiagnosticCode;
    private IReadOnlyList<Diagnostic> _catalogDiagnostics = [];
    private IReadOnlyList<PlayerCardStatRow> _playerCardStatistics = [];
    private string _playerCardStatisticsStatus = "statistics.formula_profile_unavailable";
    private bool _arePlayerCardStatisticsAvailable;
    private bool _isUpdatingLocalizationRow;
    private bool _isUpdatingSkillRow;
    private string? _modificationSourcePath;
    private string? _lastSavedPackagePath;
    private bool _isPostSavePromptVisible;
    private string? _localesJsonPath;

    public MainWindowViewModel()
        : this(new FileSystemCharacterCatalogService(), new CharacterCloneService())
    {
    }

    public static MainWindowViewModel CreateDesktopDefault()
    {
        var previewService = new GameAssetPreviewService(new BcnG4TextureDecoder(), capacity: 2);
        return new MainWindowViewModel(
            new FileSystemCharacterCatalogService(),
            new CharacterCloneService(),
            packageService: new ZipVrCharaPackageService(),
            settingsStore: new JsonApplicationSettingsStore(DesktopStoragePaths.Settings),
            projectStore: new JsonModProjectStore(),
            recoveryRoot: DesktopStoragePaths.Recovery,
            portraitLoader: new CharacterPortraitLoader(previewService),
            playerCardAssetLoader: new PlayerCardAssetLoader(
                previewService,
                new UniformPreviewResolver(previewService)));
    }

    public MainWindowViewModel(
        ICharacterCatalogService catalogService,
        ICharacterCloneService cloneService,
        IExportPlanner? exportPlanner = null,
        IVrCharaPackageService? packageService = null,
        IApplicationSettingsStore? settingsStore = null,
        IModProjectStore? projectStore = null,
        string? recoveryRoot = null,
        ICharacterDraftService? draftService = null,
        ICharacterPortraitLoader? portraitLoader = null,
        IPlayerCardAssetLoader? playerCardAssetLoader = null,
        GameDumpProfile? playerCardAssetProfile = null,
        ICharacterStatCalculator? characterStatCalculator = null,
        IExportExecutor? exportExecutor = null)
    {
        _catalogService = catalogService;
        _cloneService = cloneService;
        _draftService = draftService ?? new CharacterDraftService(cloneService);
        _portraitLoader = portraitLoader;
        _playerCardAssetLoader = playerCardAssetLoader;
        _playerCardAssetProfile = playerCardAssetProfile;
        _loadDumpStatistics = characterStatCalculator is null;
        _characterStatCalculator = characterStatCalculator
            ?? new CharacterStatCalculator(new DocumentedStatFormulaProvider([]));
        _exportPlanner = exportPlanner ?? new ExportPlanner();
        _packageService = packageService;
        _exportExecutor = exportExecutor ?? new ExportExecutor(packageService);
        _settingsStore = settingsStore;
        _projectStore = projectStore;
        _recoveryRoot = recoveryRoot;
        Project = ModProjectDocument.Create("Untitled Mod");

        LoadGameDumpCommand = new AsyncCommand(LoadGameDumpAsync);
        AddPackageCommand = new DelegateCommand(AddPackage);
        CloneSelectedCommand = new DelegateCommand(CloneSelectedCharacter);
        CreateBlankDraftCommand = new DelegateCommand(CreateBlankDraft);
        BeginCreateCommand = new DelegateCommand(BeginCreate);
        BeginModifyCommand = new DelegateCommand(() => { });
        BeginIncorporationCommand = new DelegateCommand(BeginIncorporationFlow);
        ReturnToMainMenuCommand = new DelegateCommand(ReturnToMainMenu);
        ConfirmAddSavedPackageCommand = new DelegateCommand(AddLastSavedPackageToBatch);
        RemoveActiveDraftCommand = new DelegateCommand(RequestRemoveActiveDraft);
        ConfirmRemoveActiveDraftCommand = new DelegateCommand(RemoveActiveDraft);
        CancelRemoveActiveDraftCommand = new DelegateCommand(CancelRemoveActiveDraft);
        NavigateCommand = new DelegateCommand(parameter =>
        {
            if (parameter is WorkspaceKind workspace) ActiveWorkspace = workspace;
            else if (parameter is string text && Enum.TryParse<WorkspaceKind>(text, out var parsed))
                ActiveWorkspace = parsed;
        });
        PreviewExportCommand = new AsyncCommand(PreviewExportAsync);
        ExecuteExportCommand = new AsyncCommand(ExecuteExportAsync);
        ToggleLanguageCommand = new DelegateCommand(ToggleLanguage);
        NextWizardStepCommand = new DelegateCommand(NextWizardStep);
        PreviousWizardStepCommand = new DelegateCommand(PreviousWizardStep);
        GoToWizardStepCommand = new DelegateCommand(GoToWizardStep);
        ApplyTechniqueTemplateCommand = new DelegateCommand(ApplyTechniqueTemplateToAllVariants);
        DuplicateBatchEntryCommand = new DelegateCommand(DuplicateSelectedBatchEntry);
        ToggleBatchEntryCommand = new DelegateCommand(ToggleSelectedBatchEntry);
        RemoveBatchEntryCommand = new DelegateCommand(RemoveSelectedBatchEntry);
        MoveBatchEntryUpCommand = new DelegateCommand(() => MoveSelectedBatchEntry(-1));
        MoveBatchEntryDownCommand = new DelegateCommand(() => MoveSelectedBatchEntry(1));
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);
        OpenLocalesJsonCommand = new AsyncCommand(OpenLocalesJsonAsync);
        CancelIndexingCommand = new DelegateCommand(CancelIndexing);
        ClearCharacterFiltersCommand = new DelegateCommand(ClearCharacterFilters);
        DuplicateSelectedCharacterCommand = new DelegateCommand(DuplicateSelectedCharacter);
        DeleteSelectedCharacterCommand = new DelegateCommand(RequestDeleteSelectedCharacter);
        NavigateEditorSectionCommand = new DelegateCommand(parameter =>
        {
            if (parameter is CharacterEditorSection section) NavigateEditorSection(section);
            else if (parameter is string text
                     && Enum.TryParse<CharacterEditorSection>(text, out var parsed))
                NavigateEditorSection(parsed);
        });
        SetVerifiedLevelCommand = new DelegateCommand(parameter =>
        {
            if (parameter is string text
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                && level is 1 or 30 or 50 or 99)
            {
                PlayerCardLevel = level;
            }
        });
    }

    public ModProjectDocument Project { get; }

    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new(WorkspaceKind.Project, "Project", "⌂"),
        new(WorkspaceKind.Characters, "Characters", "◉"),
        new(WorkspaceKind.Batch, "Batch", "▤"),
        new(WorkspaceKind.Export, "Export", "⇧"),
    ];

    public IReadOnlyList<ExportPlatform> ExportPlatforms { get; } = Enum.GetValues<ExportPlatform>();
    public IReadOnlyList<AcquisitionMode> AcquisitionModes { get; } =
        [AcquisitionMode.Shop, AcquisitionMode.Delivery];

    public IReadOnlyList<string> DraftAcquisitionMethods { get; } = ["Shop", "Delivery"];
    public IReadOnlyList<CharacterAffinity> CharacterAffinities { get; } =
        Enum.GetValues<CharacterAffinity>().Where(affinity => affinity != CharacterAffinity.Unknown).ToArray();
    public IReadOnlyList<CharacterOrigin> CharacterOrigins { get; } = Enum.GetValues<CharacterOrigin>();
    public IReadOnlyList<CharacterSort> CharacterSorts { get; } = Enum.GetValues<CharacterSort>();
    public IReadOnlyList<CharacterPosition> CharacterPositions { get; } =
        Enum.GetValues<CharacterPosition>().Where(position => position != CharacterPosition.Unknown).ToArray();

    public string GameDumpInput
    {
        get => _gameDumpInput;
        set => SetProperty(ref _gameDumpInput, value);
    }

    public string ActiveGameDumpPath
    {
        get => _activeGameDumpPath;
        private set => SetProperty(ref _activeGameDumpPath, value);
    }

    public string IndexProgressMessage
    {
        get => _indexProgressMessage;
        private set => SetProperty(ref _indexProgressMessage, value);
    }

    public string PackageInput
    {
        get => _packageInput;
        set => SetProperty(ref _packageInput, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ScheduleSearch();
        }
    }

    public CharacterAffinity? SelectedAffinity
    {
        get => _selectedAffinity;
        set
        {
            if (SetProperty(ref _selectedAffinity, value)) ScheduleSearch();
        }
    }

    public CharacterOrigin? SelectedOrigin
    {
        get => _selectedOrigin;
        set { if (SetProperty(ref _selectedOrigin, value)) ScheduleSearch(); }
    }

    public string? SelectedSeries
    {
        get => _selectedSeries;
        set { if (SetProperty(ref _selectedSeries, value)) ScheduleSearch(); }
    }

    public int? SelectedAcademicYear
    {
        get => _selectedAcademicYear;
        set { if (SetProperty(ref _selectedAcademicYear, value)) ScheduleSearch(); }
    }

    public int? SelectedGender
    {
        get => _selectedGender;
        set { if (SetProperty(ref _selectedGender, value)) ScheduleSearch(); }
    }

    public int? SelectedBodyType
    {
        get => _selectedBodyType;
        set { if (SetProperty(ref _selectedBodyType, value)) ScheduleSearch(); }
    }

    public string? SelectedPosition
    {
        get => _selectedPosition;
        set { if (SetProperty(ref _selectedPosition, value)) ScheduleSearch(); }
    }

    public int? SelectedPlayStyle
    {
        get => _selectedPlayStyle;
        set { if (SetProperty(ref _selectedPlayStyle, value)) ScheduleSearch(); }
    }

    public int? SelectedRank
    {
        get => _selectedRank;
        set { if (SetProperty(ref _selectedRank, value)) ScheduleSearch(); }
    }

    public int? SelectedSpecialRarity
    {
        get => _selectedSpecialRarity;
        set { if (SetProperty(ref _selectedSpecialRarity, value)) ScheduleSearch(); }
    }

    public IReadOnlyList<string> SeriesOptions => Characters
        .Select(character => character.Series)
        .Where(value => !string.IsNullOrWhiteSpace(value) && value != "Unknown")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<int> AcademicYearOptions => DistinctBaseValues(metadata => metadata.AcademicYear);
    private string UnknownAcademicYearLabel => SelectedLanguageCode == "es" ? "Desconocido" : "Unknown";
    public IReadOnlyList<int> GenderOptions => DistinctBaseValues(metadata => metadata.Gender);
    public IReadOnlyList<int> BodyTypeOptions => CharacterBodyTypeCatalog.Values;
    public IReadOnlyList<IdentityChoice> BodyTypeChoices => BodyTypeOptions
        .Concat(ParseNullableInt(ActiveDraft?.Fields.GetValueOrDefault("Identity.BodyType")) is { } active
            ? [active]
            : [])
        .Distinct()
        .Order()
        .Select((value, index) => new IdentityChoice(
            value.ToString(CultureInfo.InvariantCulture),
            index.ToString(CultureInfo.InvariantCulture)))
        .ToArray();
    public IReadOnlyList<string> PositionOptions => Characters
        .Select(character => character.Position)
        .Where(value => !string.IsNullOrWhiteSpace(value) && value != "Unknown")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<int> PlayStyleOptions => DistinctVariantValues(variant => variant.PlayStyle);
    public IReadOnlyList<int> GrowthOptions => DistinctVariantValues(variant => variant.Growth);
    public IReadOnlyList<int> RankOptions => DistinctVariantValues(variant => variant.Rank);
    public IReadOnlyList<int> AbilityBoardOptions => DistinctVariantValues(variant => variant.AbilityBoardId);
    public IReadOnlyList<int> SpecialRarityOptions => DistinctVariantValues(variant => variant.SpecialRarity);
    public IReadOnlyList<LocalizedSkill> SkillChoices => BuildSkillChoices();

    public CharacterSort SelectedCharacterSort
    {
        get => _selectedCharacterSort;
        set
        {
            if (SetProperty(ref _selectedCharacterSort, value)) ScheduleSearch();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsWorkspaceReady
    {
        get => _isWorkspaceReady;
        private set
        {
            if (SetProperty(ref _isWorkspaceReady, value))
            {
                OnPropertyChanged(nameof(IsOnboardingVisible));
                OnPropertyChanged(nameof(ShowActionChoice));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(IsCreationChoiceVisible));
                OnPropertyChanged(nameof(IsActionChoiceVisible));
                OnPropertyChanged(nameof(IsMainMenuVisible));
                OnPropertyChanged(nameof(IsIncorporationVisible));
                OnPropertyChanged(nameof(CanGoBack));
            }
        }
    }

    public bool IsOnboardingVisible => !IsWorkspaceReady;
    public bool IsMainMenuVisible => IsWorkspaceReady && ActiveWorkspace == WorkspaceKind.Project && WizardStep == WizardStep.Setup;
    public bool IsIncorporationVisible => IsWorkspaceReady && ActiveWorkspace == WorkspaceKind.Batch && WizardStep == WizardStep.Setup;
    public bool IsPostSavePromptVisible { get => _isPostSavePromptVisible; private set => SetProperty(ref _isPostSavePromptVisible, value); }
    public bool HasModificationSource => !string.IsNullOrWhiteSpace(_modificationSourcePath);
    public bool ShowActionChoice => false;
    public bool IsCreationChoiceVisible => false;
    public bool IsActionChoiceVisible => false;

    private void BeginCreate()
    {
        BeginCreateFlow();
    }

    public ICommand BeginModifyCommand { get; }
    public ICommand BeginIncorporationCommand { get; }
    public ICommand ReturnToMainMenuCommand { get; }
    public ICommand ConfirmAddSavedPackageCommand { get; }

    public void BeginCreateFlow()
    {
        _modificationSourcePath = null;
        _activeDraftId = null;
        OnPropertyChanged(nameof(HasModificationSource));
        ActiveDraft = null;
        ActiveWorkspace = WorkspaceKind.Characters;
        WizardStep = WizardStep.Character;
    }

    public void BeginIncorporationFlow()
    {
        AcquisitionMode = AcquisitionMode.Delivery;
        ActiveWorkspace = WorkspaceKind.Batch;
        WizardStep = WizardStep.Setup;
    }

    public async Task LoadModificationAsync(string path)
    {
        if (_packageService is null) { StatusMessage = "The .vrchara package service is not configured."; return; }
        try
        {
            var draft = await _packageService.LoadAsync(path, CancellationToken.None);
            _activeDraftId = null;
            ActiveDraft = draft;
            SetTechniqueVariantChoicesFromDraft(ActiveDraft);
            _modificationSourcePath = path;
            OnPropertyChanged(nameof(HasModificationSource));
            ActiveWorkspace = WorkspaceKind.Characters;
            WizardStep = WizardStep.Details;
            StatusMessage = "Character package loaded for modification.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        { StatusMessage = exception.Message; }
    }

    public void ReturnToMainMenu()
    {
        IsPostSavePromptVisible = false;
        _activeDraftId = null;
        ActiveDraft = null;
        _modificationSourcePath = null;
        _lastSavedPackagePath = null;
        OnPropertyChanged(nameof(HasModificationSource));
        ActiveWorkspace = WorkspaceKind.Project;
        WizardStep = WizardStep.Setup;
    }

    public void ConfirmAddSavedPackage() => AddLastSavedPackageToBatch();
    public void AddLastSavedPackageToBatch()
    {
        IsPostSavePromptVisible = false;
        if (!string.IsNullOrWhiteSpace(_lastSavedPackagePath)) { PackageInput = _lastSavedPackagePath; AddPackage(); }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetProperty(ref _isIndexing, value);
    }

    public WorkspaceKind ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (SetProperty(ref _activeWorkspace, value))
            {
                OnPropertyChanged(nameof(IsProjectWorkspace));
                OnPropertyChanged(nameof(IsCharactersWorkspace));
                OnPropertyChanged(nameof(IsBatchWorkspace));
                OnPropertyChanged(nameof(IsExportWorkspace));
                OnPropertyChanged(nameof(IsResearchWorkspace));
                OnPropertyChanged(nameof(IsMainMenuVisible));
                OnPropertyChanged(nameof(IsIncorporationVisible));
            }
        }
    }

    public WizardStep WizardStep
    {
        get => _wizardStep;
        private set
        {
            if (!SetProperty(ref _wizardStep, value)) return;
            OnPropertyChanged(nameof(IsSetupStep));
            OnPropertyChanged(nameof(IsCharacterStep));
            OnPropertyChanged(nameof(IsDetailsStep));
            OnPropertyChanged(nameof(IsAttributesStep));
            OnPropertyChanged(nameof(IsSkillsStep));
            OnPropertyChanged(nameof(IsAppearanceStep));
            OnPropertyChanged(nameof(IsAcquisitionStep));
            OnPropertyChanged(nameof(IsReviewStep));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(ShowActionChoice));
            OnPropertyChanged(nameof(IsCreationChoiceVisible));
            OnPropertyChanged(nameof(IsActionChoiceVisible));
            OnPropertyChanged(nameof(IsMainMenuVisible));
            OnPropertyChanged(nameof(IsIncorporationVisible));
        }
    }

    public bool IsSetupStep => WizardStep == WizardStep.Setup;
    public bool IsCharacterStep => WizardStep == WizardStep.Character;
    public bool IsDetailsStep => WizardStep == WizardStep.Details;
    public bool IsAttributesStep => WizardStep == WizardStep.Attributes;
    public bool IsSkillsStep => WizardStep == WizardStep.Skills;
    public bool IsAppearanceStep => WizardStep == WizardStep.Appearance;
    public bool IsAcquisitionStep => WizardStep == WizardStep.Acquisition;
    public bool IsReviewStep => WizardStep == WizardStep.Review;
    public bool CanGoBack => WizardStep != WizardStep.Setup || (IsWorkspaceReady && ActiveWorkspace == WorkspaceKind.Batch);
    public bool CanGoNext => WizardStep switch
    {
        WizardStep.Setup => IsWorkspaceReady && ActiveWorkspace != WorkspaceKind.Batch,
        WizardStep.Character => HasSelectedCharacter,
        WizardStep.Review => false,
        _ => true,
    };

    private static readonly WizardStep[] WizardSteps =
        [WizardStep.Setup, WizardStep.Character, WizardStep.Details, WizardStep.Attributes, WizardStep.Skills, WizardStep.Appearance, WizardStep.Review];

    private void NextWizardStep()
    {
        if (!CanGoNext) return;
        if (WizardStep == WizardStep.Character && ActiveDraft is null)
        {
            CloneSelectedCharacter();
            if (ActiveDraft is null) return;
        }
        var index = Array.IndexOf(WizardSteps, WizardStep);
        if (index < 0 || index >= WizardSteps.Length - 1) return;
        WizardStep = WizardSteps[index + 1];
    }

    private void PreviousWizardStep()
    {
        if (WizardStep == WizardStep.Setup) { if (IsWorkspaceReady) ReturnToMainMenu(); return; }
        if (!CanGoBack) return;
        if (WizardStep == WizardStep.Character)
        {
            ReturnToMainMenu();
            return;
        }
        var index = Array.IndexOf(WizardSteps, WizardStep);
        if (index <= 0) { ReturnToMainMenu(); return; }
        WizardStep = WizardSteps[index - 1];
    }

    private void GoToWizardStep(object? parameter)
    {
        var step = parameter is WizardStep typed
            ? typed
            : parameter is string text && Enum.TryParse<WizardStep>(text, out var parsed) ? parsed : (WizardStep)(-1);
        if (Array.IndexOf(WizardSteps, step) < 0) return;
        if (step > WizardStep && !IsWorkspaceReady) return;
        if (step == WizardStep.Character)
        {
            OnPropertyChanged(nameof(IsCreationChoiceVisible));
            OnPropertyChanged(nameof(IsActionChoiceVisible));
        }
        WizardStep = step;
    }

    public bool IsProjectWorkspace => ActiveWorkspace == WorkspaceKind.Project;
    public bool IsCharactersWorkspace => ActiveWorkspace == WorkspaceKind.Characters;
    public bool IsBatchWorkspace => ActiveWorkspace == WorkspaceKind.Batch;
    public bool IsExportWorkspace => ActiveWorkspace == WorkspaceKind.Export;
    public bool IsResearchWorkspace => ActiveWorkspace == WorkspaceKind.Research;

    public CharacterCatalogItem? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (SetProperty(ref _selectedCharacter, value))
            {
                var rosterRow = value is null
                    ? null
                    : RosterRows.FirstOrDefault(row => row.Character.Id == value.Id);
                if (!ReferenceEquals(_selectedRosterRow, rosterRow))
                {
                    _selectedRosterRow = rosterRow;
                    OnPropertyChanged(nameof(SelectedRosterRow));
                }
                if (value?.Variants is { Count: > 0 } variants)
                {
                    SelectedVariant = variants[0];
                    SetTechniqueVariantChoices(variants);
                }
                else if (ActiveDraft is null)
                {
                    SelectedVariant = null;
                    SetTechniqueVariantChoices([]);
                }
                SelectProjectDraft(value);
                OnPropertyChanged(nameof(HasSelectedCharacter));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(ShowsOriginalOnly));
                OnPropertyChanged(nameof(ShowsCharacterComparison));
                OnPropertyChanged(nameof(SelectedPortraitSummary));
                OnPropertyChanged(nameof(SelectedVariants));
                OnPropertyChanged(nameof(CanDeleteSelectedCharacter));
                NotifyPlayerCardChanged();
                SchedulePortraitLoad(value);
                SchedulePlayerCardAssetLoad(value, SelectedVariant);
            }
        }
    }

    public CharacterRosterRow? SelectedRosterRow
    {
        get => _selectedRosterRow;
        set
        {
            // Avalonia can briefly report a null selection while a filtered list is rebuilt.
            // Keep the active character until the replacement rows have been installed.
            if (value is null && _isUpdatingRosterRows) return;
            if (SetProperty(ref _selectedRosterRow, value))
                SelectedCharacter = value?.Character;
        }
    }

    public bool HasSelectedCharacter => SelectedCharacter is not null;
    public bool CanDeleteSelectedCharacter =>
        SelectedCharacter?.Origin is CharacterOrigin.Custom or CharacterOrigin.ImportedPackage
        && _activeDraftId is not null;
    public string SelectedPortraitSummary => SelectedCharacter?.PortraitMetadata is { } metadata
        ? $"{metadata.TextureCount} textures · {metadata.PayloadFormat} · {metadata.Width}×{metadata.Height}"
        : "Unresolved";
    public IReadOnlyList<CharacterVariantSummary> SelectedVariants => SelectedCharacter?.Variants ?? [];
    public IReadOnlyList<CharacterVariantChoice> TechniqueVariantChoices => _techniqueVariantChoices;

    public CharacterVariantChoice? SelectedTechniqueVariantChoice
    {
        get => _selectedTechniqueVariantChoice;
        set
        {
            if (!SetProperty(ref _selectedTechniqueVariantChoice, value) || value is null) return;
            var currentVariant = ActiveDraft?.Variants?
                .FirstOrDefault(variant =>
                    variant.SourceParameterId == value.Variant.ParameterId
                    && (variant.Gameplay.SpecialRarity ?? 0) == value.Variant.SpecialRarity);
            var variant = currentVariant is null
                ? value.Variant
                : value.Variant with
                {
                    Affinity = ParseAffinity(currentVariant.Gameplay.Affinity, value.Variant.Affinity),
                    MainPosition = currentVariant.Gameplay.MainPosition ?? value.Variant.MainPosition,
                    SubPosition = currentVariant.Gameplay.SubPosition ?? value.Variant.SubPosition,
                    PlayStyle = currentVariant.Gameplay.PlayStyle ?? value.Variant.PlayStyle,
                    Growth = currentVariant.Gameplay.Growth ?? value.Variant.Growth,
                    Rank = currentVariant.Gameplay.Rank ?? value.Variant.Rank,
                    AbilityBoardId = currentVariant.Gameplay.AbilityBoardId ?? value.Variant.AbilityBoardId,
                    SpecialRarity = currentVariant.Gameplay.SpecialRarity ?? value.Variant.SpecialRarity,
                    SkillSlots = currentVariant.Skills.Slots
                        .Select(slot => new CharacterSkillSlot(slot.SkillId ?? 0, slot.UnlockLevel ?? 0))
                        .ToArray(),
                };
            SelectedVariant = variant;
            ApplyTechniqueVariant(variant);
        }
    }

    private static CharacterAffinity ParseAffinity(string value, CharacterAffinity fallback) =>
        Enum.TryParse<CharacterAffinity>(value, true, out var affinity) ? affinity : fallback;
    public Bitmap? SelectedPortrait => _standardPortraitResult?.Bitmap;
    public Bitmap? PlayerCardBackPortrait => _standardPortraitResult?.Bitmap;
    public Bitmap? PlayerCardFrontPortrait => _uniformPortraitResult?.Bitmap;
    public Bitmap? AppearancePortrait => _appearancePortrait ?? PlayerCardFrontPortrait;
    public bool HasLayeredAppearance => _appearancePortrait is null
        && (PlayerCardBackPortrait is not null || PlayerCardFrontPortrait is not null);
    public bool ShowAppearancePortrait => !HasLayeredAppearance;
    public Bitmap? PlayerCardPositionIcon => _playerCardAssets?.PositionIcon;
    public Bitmap? PlayerCardGenderIcon => _playerCardAssets?.GenderIcon;
    public Bitmap? PlayerCardBodyTypeIcon => _playerCardAssets?.BodyTypeIcon;
    public Bitmap? PlayerCardUniform => _playerCardAssets?.Uniform;
    public bool HasPlayerCardPositionIcon => PlayerCardPositionIcon is not null;
    public bool HasPlayerCardGenderIcon => PlayerCardGenderIcon is not null;
    public bool HasPlayerCardBodyTypeIcon => PlayerCardBodyTypeIcon is not null;
    public bool ShowPlayerCardPositionId => !HasPlayerCardPositionIcon;
    public bool ShowPlayerCardGenderId => !HasPlayerCardGenderIcon;
    public bool ShowPlayerCardBodyTypeId => !HasPlayerCardBodyTypeIcon;
    public IReadOnlyList<string> PlayerCardAssetDiagnosticCodes =>
        _playerCardAssets?.DiagnosticCodes ?? [];

    public string? SelectedPortraitDiagnosticCode
    {
        get => _selectedPortraitDiagnosticCode;
        private set => SetProperty(ref _selectedPortraitDiagnosticCode, value);
    }

    public CharacterVariantSummary? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (SetProperty(ref _selectedVariant, value))
            {
                NotifyPlayerCardChanged();
                SchedulePlayerCardAssetLoad(SelectedCharacter, value);
            }
        }
    }

    public int PlayerCardLevel
    {
        get => _playerCardLevel;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _playerCardLevel, normalized))
            {
                OnPropertyChanged(nameof(PlayerCardLearnedSkills));
                RecalculatePlayerCardStatistics();
            }
        }
    }

    public string PlayerCardDisplayName =>
        GetPlayerCardLocalization()?.FullName
        ?? SelectedCharacter?.DisplayName
        ?? string.Empty;

    public string PlayerCardShortName =>
        GetPlayerCardLocalization()?.ShortName
        ?? PlayerCardDisplayName;

    public string PlayerCardRomanizedName => SelectedCharacter?.RomanizedNames is { Count: > 0 } names
        ? (names.GetValueOrDefault("ja") ?? names.Values.First()).FullName ?? string.Empty
        : string.Empty;

    public string PlayerCardDescription => GetPlayerCardLocalization()?.Description ?? string.Empty;
    public RubyTextDocumentViewModel PlayerCardRomanizedNameDocument => BuildGameTextDocument(PlayerCardRomanizedName);
    public RubyTextDocumentViewModel PlayerCardDescriptionDocument => BuildGameTextDocument(PlayerCardDescription);
    public CharacterAffinity PlayerCardAffinity => SelectedVariant?.Affinity
        ?? SelectedCharacter?.Affinity
        ?? CharacterAffinity.Unknown;
    public string PlayerCardAffinityName => ResolveAffinityName(PlayerCardAffinity);
    public string PlayerCardPosition => SelectedVariant is null
        ? ResolvePositionName(SelectedCharacter?.Position)
        : ResolvePositionName(SelectedVariant.MainPosition);
    public string PlayerCardSeries => SelectedCharacter?.BaseMetadata is { } characterBase
        && SelectedCharacter.Taxonomy is { } taxonomy
            ? taxonomy.ResolveSeries(unchecked((uint)characterBase.SourceSeries), PlayerCardLocale)
            : SelectedCharacter?.Series ?? "Unknown";
    public int? PlayerCardGender => SelectedCharacter?.BaseMetadata?.Gender;
    public int? PlayerCardBodyType => ActiveDraftBodyType ?? SelectedCharacter?.BaseMetadata?.BodyType;
    public string PlayerCardAcademicYear => SelectedCharacter?.BaseMetadata is { AcademicYear: 0 }
        ? UnknownAcademicYearLabel
        : SelectedCharacter?.BaseMetadata is { } characterBase
            ? SelectedCharacter.Taxonomy?.ResolveAcademicYear(
            unchecked((uint)characterBase.AcademicYear), PlayerCardLocale)
            ?? characterBase.AcademicYear.ToString(CultureInfo.InvariantCulture)
            : "Unknown";

    public string ReviewDisplayName => GetReviewLocalization()?.LocalizedName
        ?? ActiveDraft?.Identity?.DisplayName
        ?? ActiveDraft?.DisplayName
        ?? string.Empty;

    public string ReviewSeries => GetActiveDraftField("Identity.SeriesName", "Unknown");

    public string ReviewOriginGame => GetActiveDraftField("Identity.OriginGameName", "Unknown");

    public string ReviewAffinity => ResolveAffinityName(
        ActiveDraft?.Gameplay?.Affinity is { } affinity
            ? ParseAffinity(affinity, CharacterAffinity.Unknown)
            : CharacterAffinity.Unknown);

    public string ReviewMainPosition => ActiveDraft?.Gameplay?.MainPosition is { } position
        ? ResolvePositionName(position)
        : ResolvePositionName(0);

    private CharacterDraftLocalizedText? GetReviewLocalization()
    {
        if (ActiveDraft?.Localization?.Locales is not { Count: > 0 } localizations) return null;
        return localizations.GetValueOrDefault(SelectedLanguageCode)
            ?? localizations.GetValueOrDefault("en")
            ?? localizations.Values.FirstOrDefault();
    }
    public int? PlayerCardRank => SelectedVariant?.Rank;
    public int? PlayerCardSpecialRarity => SelectedVariant?.SpecialRarity;

    public IReadOnlyList<PlayerCardSkillRow> PlayerCardLearnedSkills => SelectedVariant?.EnumerateMainSkillSlots()
        .Select((skill, index) => new PlayerCardSkillRow(
            index + 1,
            skill.SkillId,
            SelectedCharacter?.Taxonomy?.ResolveSkill(unchecked((uint)skill.SkillId), PlayerCardLocale)
                ?? $"Skill {unchecked((uint)skill.SkillId)}",
            skill.UnlockLevel))
        .Where(skill => skill.SkillId != 0 && skill.UnlockLevel <= PlayerCardLevel)
        .ToArray()
        ?? [];

    private string PlayerCardLocale => SelectedLanguageCode;

    private RubyTextDocumentViewModel BuildGameTextDocument(string source)
    {
        var parsed = GameTextParser.Parse(source);
        var resolved = GameTextResolver.Resolve(
            parsed,
            SelectedCharacter?.TextReferences ?? CharacterTextReferenceIndex.Empty,
            PlayerCardLocale);
        return RubyTextDocumentViewModel.From(resolved);
    }

    private IReadOnlyList<LocalizedSkill> BuildSkillChoices()
    {
        if (_gameTaxonomy is null) return [];

        var localized = _gameTaxonomy.GetSkills(SelectedLanguageCode)
            .Select(skill => skill with { Name = ResolveSkillText(skill.Name) })
            .ToArray();
        var decorated = new List<LocalizedSkill>(localized.Length);
        foreach (var group in localized.GroupBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var variants = group.OrderBy(skill => skill.Id).ToArray();
            for (var index = 0; index < variants.Length; index++)
            {
                var skill = variants[index];
                var name = IsArmoredKeshin(skill.Name)
                    ? $"[KESHIN] ({(SelectedLanguageCode == "es" ? "Armadura" : "Armor")}) {skill.Name}"
                    : skill.Name;
                decorated.Add(skill with { Name = name });
            }
        }

        var result = new List<LocalizedSkill>(decorated.Count);
        foreach (var group in decorated.GroupBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var variants = group.OrderBy(skill => skill.Id).ToArray();
            for (var index = 0; index < variants.Length; index++)
            {
                var suffix = variants.Length == 1
                    ? string.Empty
                    : $" ({(SelectedLanguageCode == "es" ? "variante" : "variant")} {index + 1})";
                result.Add(variants[index] with { Name = variants[index].Name + suffix });
            }
        }

        return result
            .OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(skill => skill.Id)
            .ToArray();
    }

    private string ResolveSkillText(string source)
    {
        var parsed = GameTextParser.Parse(source);
        var resolved = GameTextResolver.Resolve(
            parsed,
            _gameTextReferences ?? CharacterTextReferenceIndex.Empty,
            SelectedLanguageCode);
        return string.Concat(resolved.Nodes.Select(node => node switch
        {
            GameTextLiteral literal => literal.Text,
            GameTextRuby ruby => ruby.BaseText,
            GameTextLineBreak => " ",
            GameTextCharacterReference reference => string.Empty,
            _ => string.Empty,
        })).Trim();
    }

    private static bool IsArmoredKeshin(string name) =>
        name.Contains("Keshin Armor", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Keshin Armadura", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Armed", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PlayerCardStatRow> PlayerCardStatistics => _playerCardStatistics;
    public string PlayerCardStatisticsStatus => _playerCardStatisticsStatus;
    public bool ArePlayerCardStatisticsAvailable => _arePlayerCardStatisticsAvailable;

    private CharacterLocalizedText? GetPlayerCardLocalization()
    {
        if (SelectedCharacter?.Localizations is not { Count: > 0 } localizations) return null;
        var locale = PlayerCardLocale;
        return localizations.GetValueOrDefault(locale) ?? localizations.Values.First();
    }

    private void NotifyPlayerCardChanged()
    {
        OnPropertyChanged(nameof(PlayerCardDisplayName));
        OnPropertyChanged(nameof(PlayerCardShortName));
        OnPropertyChanged(nameof(PlayerCardRomanizedName));
        OnPropertyChanged(nameof(PlayerCardDescription));
        OnPropertyChanged(nameof(PlayerCardRomanizedNameDocument));
        OnPropertyChanged(nameof(PlayerCardDescriptionDocument));
        OnPropertyChanged(nameof(PlayerCardAffinity));
        OnPropertyChanged(nameof(PlayerCardAffinityName));
        OnPropertyChanged(nameof(PlayerCardPosition));
        OnPropertyChanged(nameof(PlayerCardSeries));
        OnPropertyChanged(nameof(PlayerCardGender));
        OnPropertyChanged(nameof(PlayerCardBodyType));
        OnPropertyChanged(nameof(PlayerCardAcademicYear));
        OnPropertyChanged(nameof(PlayerCardRank));
        OnPropertyChanged(nameof(PlayerCardSpecialRarity));
        OnPropertyChanged(nameof(PlayerCardLearnedSkills));
        OnPropertyChanged(nameof(AppearancePortrait));
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
        RecalculatePlayerCardStatistics();
    }

    private void RecalculatePlayerCardStatistics()
    {
        var calculation = SelectedVariant is { } variant && _playerCardAssetProfile is { } profile
            ? _characterStatCalculator is IContextualCharacterStatCalculator contextual
                ? contextual.Calculate(
                    profile.Version,
                    variant.MainPosition,
                    variant.SubPosition,
                    variant.PlayStyle,
                    variant.Growth,
                    variant.Rank,
                    new CharacterLevel(PlayerCardLevel))
                : _characterStatCalculator.Calculate(
                    profile.Version,
                    variant.Growth,
                    variant.Rank,
                    new CharacterLevel(PlayerCardLevel))
            : CharacterStatCalculation.Unavailable("statistics.formula_profile_unavailable");

        _arePlayerCardStatisticsAvailable = calculation.IsAvailable;
        _playerCardStatisticsStatus = calculation.IsAvailable
            ? "Verified table value."
            : calculation.DiagnosticCode ?? "statistics.formula_profile_unavailable";
        _playerCardStatistics = calculation.Stats is { } stats
            ?
            [
                new(CharacterStatKind.Kick, "Kick", stats.Kick),
                new(CharacterStatKind.Control, "Control", stats.Control),
                new(CharacterStatKind.Technique, "Technique", stats.Technique),
                new(CharacterStatKind.Pressure, "Pressure", stats.Pressure),
                new(CharacterStatKind.Physical, "Physical", stats.Physical),
                new(CharacterStatKind.Agility, "Agility", stats.Agility),
                new(CharacterStatKind.Intelligence, "Intelligence", stats.Intelligence),
            ]
            : [];
        OnPropertyChanged(nameof(ArePlayerCardStatisticsAvailable));
        OnPropertyChanged(nameof(PlayerCardStatisticsStatus));
        OnPropertyChanged(nameof(PlayerCardStatistics));
    }

    private void SchedulePortraitLoad(CharacterCatalogItem? character)
    {
        _portraitCancellation?.Cancel();
        _portraitCancellation?.Dispose();
        _portraitCancellation = null;
        ReplacePortraitResults(null, null);
        SelectedPortraitDiagnosticCode = null;
        if (character is null || _portraitLoader is null) return;

        var cancellation = new CancellationTokenSource();
        _portraitCancellation = cancellation;
        _ = LoadPortraitsAsync(character, cancellation);
    }

    private async Task LoadPortraitsAsync(
        CharacterCatalogItem character,
        CancellationTokenSource cancellation)
    {
        try
        {
            var loader = _portraitLoader!;
            var standardTask = loader.LoadAsync(
                new CharacterPortraitRequest(character, CharacterPortraitKind.Standard),
                cancellation.Token);
            var uniformTask = loader.LoadAsync(
                new CharacterPortraitRequest(character, CharacterPortraitKind.UniformCompatible),
                cancellation.Token);
            await Task.WhenAll(standardTask, uniformTask);
            var standard = standardTask.Result;
            var uniform = uniformTask.Result;
            if (cancellation.IsCancellationRequested || !ReferenceEquals(SelectedCharacter, character))
            {
                standard.Dispose();
                uniform.Dispose();
                return;
            }
            ReplacePortraitResults(standard, uniform);
            SelectedPortraitDiagnosticCode = standard.DiagnosticCode ?? uniform.DiagnosticCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
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
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
        previousStandard?.Dispose();
        previousUniform?.Dispose();
    }

    private void SchedulePlayerCardAssetLoad(
        CharacterCatalogItem? character,
        CharacterVariantSummary? variant)
    {
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
            var assets = await _playerCardAssetLoader!.LoadAsync(
                _playerCardAssetProfile!,
                new PlayerCardAssetRequest(
                    character,
                    variant,
                    locale,
                    ActiveDraftBodyType,
                    ParseNullableInt(ActiveDraft?.Fields.GetValueOrDefault("Identity.Gender")),
                    ActiveDraft?.Models?.UniformModel,
                    ActiveDraft?.Models?.ChestSize),
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

    public CharacterDraft? ActiveDraft
    {
        get => _activeDraft;
        private set
        {
            var previous = _activeDraft;
            if (SetProperty(ref _activeDraft, value))
            {
                InvalidateExportPlan();
                OnPropertyChanged(nameof(HasActiveDraft));
                OnPropertyChanged(nameof(ShowsOriginalOnly));
                OnPropertyChanged(nameof(ShowsCharacterComparison));
                OnPropertyChanged(nameof(ActiveDraftDisplayName));
                OnPropertyChanged(nameof(ActiveDraftSymbolicId));
                OnPropertyChanged(nameof(ActiveDraftSourceCharacterId));
                OnPropertyChanged(nameof(ActiveDraftFields));
                OnPropertyChanged(nameof(ActiveDraftSeriesChoice));
                OnPropertyChanged(nameof(ActiveDraftOriginGameChoice));
                OnPropertyChanged(nameof(ActiveDraftGenderChoice));
                OnPropertyChanged(nameof(ActiveDraftAcademicYearChoice));
                OnPropertyChanged(nameof(ActiveDraftTeamAssociationChoice));
                if (previous?.SymbolicId != value?.SymbolicId)
                {
                    _localesJsonPath = null;
                    OnPropertyChanged(nameof(LocalesJsonPath));
                    OnPropertyChanged(nameof(AppearanceModelOptions));
                    OnPropertyChanged(nameof(AppearanceUniformOptions));
                    OnPropertyChanged(nameof(AppearanceShoesOptions));
                    OnPropertyChanged(nameof(AppearanceGloveOptions));
                    OnPropertyChanged(nameof(TeamAssociationOptions));
                }
                NotifyActiveDraftEditorProperties();
                NotifyReviewChanged();
                RefreshAppearancePortrait();
                if (!_isUpdatingLocalizationRow) RebuildLocalizationEditorRows();
                if (!_isUpdatingSkillRow) RebuildSkillEditorRows();
            }
        }
    }

    public bool HasActiveDraft => ActiveDraft is not null;
    public bool ShowsOriginalOnly => HasSelectedCharacter && !HasActiveDraft;
    public bool ShowsCharacterComparison => HasSelectedCharacter && HasActiveDraft;

    public bool IsRemoveDraftConfirmationVisible
    {
        get => _isRemoveDraftConfirmationVisible;
        private set => SetProperty(ref _isRemoveDraftConfirmationVisible, value);
    }

    public string ActiveDraftDisplayName
    {
        get => ActiveDraft?.DisplayName ?? string.Empty;
        set
        {
            if (ActiveDraft is null || string.Equals(ActiveDraft.DisplayName, value, StringComparison.Ordinal)) return;
            ActiveDraft = _draftService.Update(ActiveDraft, "Identity.DisplayName", value);
            if (_activeDraftId is { } draftId)
            {
                Project.UpdateDraft(draftId, ActiveDraft);
                UpdateDraftRosterItem(draftId, ActiveDraft);
            }
            QueueRecoverySave();
        }
    }

    public string ActiveDraftSymbolicId => ActiveDraft?.SymbolicId ?? string.Empty;
    public string ActiveDraftSourceCharacterId => ActiveDraft?.SourceCharacterId ?? string.Empty;
    public IReadOnlyDictionary<string, string?> ActiveDraftFields =>
        ActiveDraft?.Fields ?? new Dictionary<string, string?>();
    public IReadOnlyList<IdentityChoice> IdentitySeriesChoices => Characters
        .Where(character => character.BaseMetadata is not null)
        .GroupBy(character => character.BaseMetadata!.SourceSeries)
        .Select(group => new IdentityChoice(
            group.Key.ToString(CultureInfo.InvariantCulture),
            group.Select(character => character.Series).FirstOrDefault(series => !string.IsNullOrWhiteSpace(series))
                ?? $"Series {group.Key}"))
        .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public IReadOnlyList<IdentityChoice> IdentityGenderChoices => Characters
        .Where(character => character.BaseMetadata is not null)
        .Select(character => character.BaseMetadata!.Gender)
        .Concat([1, 2, 5])
        .Distinct()
        .Order()
        .Select(value => new IdentityChoice(
            value.ToString(CultureInfo.InvariantCulture),
            CharacterGenderCatalog.ResolveName(value, SelectedLanguageCode)))
        .ToArray();

    public IReadOnlyList<IdentityChoice> IdentityOriginGameChoices =>
        Enumerable.Range(CharacterOriginGameCatalog.FirstAssociationIndex,
                CharacterOriginGameCatalog.LastAssociationIndex - CharacterOriginGameCatalog.FirstAssociationIndex + 1)
            .Select(index => new IdentityChoice(index.ToString(CultureInfo.InvariantCulture),
                CharacterOriginGameCatalog.ResolveName(index, SelectedLanguageCode)))
            .ToArray();

    public IReadOnlyList<IdentityChoice> IdentityAcademicYearChoices => Characters
        .Where(character => character.BaseMetadata is not null)
        .GroupBy(character => character.BaseMetadata!.AcademicYear)
        .Select(group => new IdentityChoice(
            group.Key.ToString(CultureInfo.InvariantCulture),
            group.First().Taxonomy?.ResolveAcademicYear(unchecked((uint)group.Key), PlayerCardLocale)
                ?? (group.Key == 0 ? UnknownAcademicYearLabel : $"Academic year {group.Key}")))
        .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public IdentityChoice? ActiveDraftSeriesChoice
    {
        get => FindIdentityChoice(IdentitySeriesChoices, "Identity.SourceSeries");
        set => UpdateIdentityChoice(value, "Identity.SourceSeries", "Identity.SeriesName");
    }

    public IdentityChoice? ActiveDraftOriginGameChoice
    {
        get => FindIdentityChoice(IdentityOriginGameChoices, "Identity.OriginGameIndex");
        set => UpdateIdentityChoice(value, "Identity.OriginGameIndex", "Identity.OriginGameName");
    }

    public IdentityChoice? ActiveDraftGenderChoice
    {
        get => FindIdentityChoice(IdentityGenderChoices, "Identity.Gender");
        set => UpdateIdentityChoice(value, "Identity.Gender", "Identity.GenderName");
    }

    public IdentityChoice? ActiveDraftAcademicYearChoice
    {
        get => FindIdentityChoice(IdentityAcademicYearChoices, "Identity.AcademicYear");
        set => UpdateIdentityChoice(value, "Identity.AcademicYear", "Identity.AcademicYearName");
    }

    public IReadOnlyList<LocalizedTeam> TeamAssociationOptions
    {
        get
        {
            if (_teamAssociationOptions.TryGetValue(SelectedLanguageCode, out var options))
                return options;
            options = _gameTaxonomy?.GetTeams(SelectedLanguageCode) ?? [];
            _teamAssociationOptions[SelectedLanguageCode] = options;
            return options;
        }
    }

    public LocalizedTeam? ActiveDraftTeamAssociationChoice
    {
        get
        {
            var value = int.TryParse(
                ActiveDraft?.Fields.GetValueOrDefault("Identity.TeamAssociation1"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : (int?)null;
            return value is { } id
                ? TeamAssociationOptions.FirstOrDefault(team => team.Id == id)
                : null;
        }
        set
        {
            if (value is not null)
                UpdateActiveDraftField("Identity.TeamAssociation1", value.Id.ToString(CultureInfo.InvariantCulture));
        }
    }

    public string ActiveDraftAffinity
    {
        get => ActiveDraft?.Gameplay?.Affinity ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.Affinity", value);
    }

    public IReadOnlyList<CharacterAffinity> AffinityCategories { get; } =
        [CharacterAffinity.Neutral, CharacterAffinity.Wind, CharacterAffinity.Forest,
            CharacterAffinity.Fire, CharacterAffinity.Earth];

    public CharacterAffinity ActiveDraftAffinityCategory
    {
        get => Enum.TryParse<CharacterAffinity>(ActiveDraft?.Gameplay?.Affinity, out var affinity)
            ? affinity : CharacterAffinity.Neutral;
        set => UpdateActiveDraftField("Gameplay.Affinity", value.ToString());
    }

    public IReadOnlyList<IdentityChoice> AffinityChoices => AffinityCategories
        .Select(value => new IdentityChoice(value.ToString(), ResolveAffinityName(value)))
        .ToArray();

    public IdentityChoice? ActiveDraftAffinityChoice
    {
        get
        {
            var affinity = ParseAffinity(ActiveDraft?.Gameplay?.Affinity ?? string.Empty, CharacterAffinity.Neutral);
            return AffinityChoices.FirstOrDefault(choice => choice.Value == affinity.ToString());
        }
        set
        {
            if (value is not null)
                UpdateActiveDraftField("Gameplay.Affinity", value.Value);
        }
    }

    public IReadOnlyList<CharacterRegistrationProfile> RegistrationProfiles { get; } =
        Enum.GetValues<CharacterRegistrationProfile>();

    public CharacterRegistrationProfile ActiveDraftRegistrationProfile
    {
        get => ActiveDraft?.Gameplay?.RegistrationProfile ?? CharacterRegistrationProfile.Standard;
        set => UpdateActiveDraftField("Gameplay.RegistrationProfile", value.ToString());
    }

    public bool UsesFunctionalBankProfile =>
        ActiveDraftRegistrationProfile is CharacterRegistrationProfile.FunctionalBank;

    public string FunctionalBankReferenceDataRoot
    {
        get => Project.FunctionalBankReferenceDataRoot ?? string.Empty;
        set
        {
            Project.SetFunctionalBankReferenceDataRoot(value);
            InvalidateExportPlan();
            OnPropertyChanged();
            QueueRecoverySave();
        }
    }

    public string ActiveDraftPlayStyle
    {
        get => ActiveDraft?.Gameplay?.PlayStyle?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.PlayStyle", value);
    }

    public int? ActiveDraftPlayStyleValue
    {
        get => ActiveDraft?.Gameplay?.PlayStyle;
        set => UpdateActiveDraftField("Gameplay.PlayStyle", value?.ToString(CultureInfo.InvariantCulture));
    }

    public string ActiveDraftGrowth
    {
        get => ActiveDraft?.Gameplay?.Growth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.Growth", value);
    }

    public int? ActiveDraftGrowthValue
    {
        get => ActiveDraft?.Gameplay?.Growth;
        set => UpdateActiveDraftField("Gameplay.Growth", value?.ToString(CultureInfo.InvariantCulture));
    }

    public string ActiveDraftRank
    {
        get => ActiveDraft?.Gameplay?.Rank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.Rank", value);
    }

    public int? ActiveDraftRankValue
    {
        get => ActiveDraft?.Gameplay?.Rank;
        set => UpdateActiveDraftField("Gameplay.Rank", value?.ToString(CultureInfo.InvariantCulture));
    }

    public string ActiveDraftAbilityBoardId
    {
        get => ActiveDraft?.Gameplay?.AbilityBoardId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.AbilityBoardId", value);
    }

    public int? ActiveDraftAbilityBoardIdValue
    {
        get => ActiveDraft?.Gameplay?.AbilityBoardId;
        set => UpdateActiveDraftField("Gameplay.AbilityBoardId", value?.ToString(CultureInfo.InvariantCulture));
    }

    public string ActiveDraftSpecialRarity
    {
        get => ActiveDraft?.Gameplay?.SpecialRarity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.SpecialRarity", value);
    }

    public int? ActiveDraftSpecialRarityValue
    {
        get => ActiveDraft?.Gameplay?.SpecialRarity;
        set => UpdateActiveDraftField("Gameplay.SpecialRarity", value?.ToString(CultureInfo.InvariantCulture));
    }

    public string ActiveDraftMainPosition
    {
        get => ActiveDraft?.Gameplay?.MainPosition?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.MainPosition", value);
    }

    public IReadOnlyList<CharacterPosition> PositionCategories { get; } =
        [CharacterPosition.Goalkeeper, CharacterPosition.Forward,
            CharacterPosition.Midfielder, CharacterPosition.Defender];

    public CharacterPosition ActiveDraftMainPositionCategory
    {
        get => CharacterPositionCatalog.Resolve(ActiveDraft?.Gameplay?.MainPosition ?? 0);
        set => UpdateActiveDraftField("Gameplay.MainPosition", ((int)value).ToString(CultureInfo.InvariantCulture));
    }

    public CharacterPosition ActiveDraftMainPositionValue
    {
        get => ActiveDraftMainPositionCategory;
        set => ActiveDraftMainPositionCategory = value;
    }

    public string ActiveDraftSubPosition
    {
        get => ActiveDraft?.Gameplay?.SubPosition?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set => UpdateActiveDraftField("Gameplay.SubPosition", value);
    }

    public CharacterPosition ActiveDraftSubPositionCategory
    {
        get => CharacterPositionCatalog.Resolve(ActiveDraft?.Gameplay?.SubPosition ?? 0);
        set => UpdateActiveDraftField("Gameplay.SubPosition", ((int)value).ToString(CultureInfo.InvariantCulture));
    }

    public CharacterPosition ActiveDraftSubPositionValue
    {
        get => ActiveDraftSubPositionCategory;
        set => ActiveDraftSubPositionCategory = value;
    }

    public IReadOnlyList<IdentityChoice> PositionChoices => PositionCategories
        .Select(value => new IdentityChoice(
            value.ToString(),
            ResolvePositionName((int)value)))
        .ToArray();

    public IdentityChoice? ActiveDraftMainPositionChoice
    {
        get => FindPositionChoice(ActiveDraft?.Gameplay?.MainPosition);
        set => UpdatePositionChoice(value, "Gameplay.MainPosition");
    }

    public IdentityChoice? ActiveDraftSubPositionChoice
    {
        get => FindPositionChoice(ActiveDraft?.Gameplay?.SubPosition);
        set => UpdatePositionChoice(value, "Gameplay.SubPosition");
    }

    public string? ActiveDraftMainPositionDiagnostic =>
        GetActiveDraftDiagnostic("Gameplay.MainPosition");

    public string? ActiveDraftSubPositionDiagnostic =>
        GetActiveDraftDiagnostic("Gameplay.SubPosition");

    public string ActiveDraftHeadModelPath
    {
        get => ActiveDraft?.Models?.HeadModelPath ?? string.Empty;
        set => UpdateActiveDraftField("Models.HeadModelPath", value);
    }

    public string ActiveDraftHeadModelDisplayPath =>
        FormatPathForDisplay(ActiveDraftHeadModelPath);

    public string ActiveDraftBodyModelPath
    {
        get => ActiveDraft?.Models?.BodyModelPath ?? string.Empty;
        set => UpdateActiveDraftField("Models.BodyModelPath", value);
    }

    public int? ActiveDraftBodyType => ParseNullableInt(
        ActiveDraft?.Fields.GetValueOrDefault("Identity.BodyType"));

    public IdentityChoice? ActiveDraftBodyTypeChoice
    {
        get => ActiveDraftBodyType is { } value
            ? BodyTypeChoices.FirstOrDefault(choice => choice.Value == value.ToString(CultureInfo.InvariantCulture))
            : null;
        set => SetActiveDraftBodyType(value);
    }

    public string? ActiveDraftBodyTypeDiagnostic => GetActiveDraftDiagnostic("Identity.BodyType");

    public void SetActiveDraftBodyType(IdentityChoice? choice)
    {
        if (choice is null
            || _isUpdatingBodyTypeChoice
            || string.Equals(
                ActiveDraftBodyType?.ToString(CultureInfo.InvariantCulture),
                choice.Value,
                StringComparison.Ordinal))
            return;

        _isUpdatingBodyTypeChoice = true;
        try
        {
            UpdateActiveDraftField("Identity.BodyType", choice.Value);
        }
        finally
        {
            _isUpdatingBodyTypeChoice = false;
        }
    }

    public string ActiveDraftSkinColor
    {
        get
        {
            var value = ActiveDraft?.Models?.SkinColorRgba;
            return value is { Length: 8 } ? $"#{value[..6]}" : "#FFE4D0";
        }
        set
        {
            var compact = value.Trim().TrimStart('#');
            if (compact.Length != 6 || !uint.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return;
            var alpha = ActiveDraft?.Models?.SkinColorRgba is { Length: 8 } current ? current[6..] : "FF";
            UpdateActiveDraftField("Models.SkinColorRgba", $"{compact.ToUpperInvariant()}{alpha}");
        }
    }

    public IReadOnlyList<int> AppearanceModelOptions => new[]
    {
        (int?)0,
        ActiveDraft?.Models?.UniformModel,
        ActiveDraft?.Models?.ShoesModel,
        ActiveDraft?.Models?.GloveModel,
    }.Where(value => value is not null).Select(value => value!.Value).Distinct().OrderBy(value => value).ToArray();
    public IReadOnlyList<int> ChestSizeOptions { get; } = [0, 1, 2];
    public IReadOnlyList<LocalizedEquipment> AppearanceUniformOptions => GetAppearanceEquipment(EquipmentCategory.Uniform);
    public IReadOnlyList<LocalizedEquipment> AppearanceShoesOptions => GetAppearanceEquipment(EquipmentCategory.Shoes);
    public IReadOnlyList<LocalizedEquipment> AppearanceGloveOptions => GetAppearanceEquipment(EquipmentCategory.Gloves);
    public LocalizedEquipment? ActiveDraftUniformChoice
    {
        get => FindAppearanceEquipment(EquipmentCategory.Uniform, ActiveDraft?.Models?.UniformModel);
        set { if (value is not null) ActiveDraftUniformModel = value.Id; }
    }
    public LocalizedEquipment? ActiveDraftShoesChoice
    {
        get => FindAppearanceEquipment(EquipmentCategory.Shoes, ActiveDraft?.Models?.ShoesModel);
        set { if (value is not null) ActiveDraftShoesModel = value.Id; }
    }
    public LocalizedEquipment? ActiveDraftGloveChoice
    {
        get => FindAppearanceEquipment(EquipmentCategory.Gloves, ActiveDraft?.Models?.GloveModel);
        set { if (value is not null) ActiveDraftGloveModel = value.Id; }
    }
    public int? ActiveDraftUniformModel { get => ActiveDraft?.Models?.UniformModel; set { if (value != ActiveDraft?.Models?.UniformModel) UpdateActiveDraftField("Models.UniformModel", value?.ToString(CultureInfo.InvariantCulture)); } }
    public int? ActiveDraftShoesModel { get => ActiveDraft?.Models?.ShoesModel; set { if (value != ActiveDraft?.Models?.ShoesModel) UpdateActiveDraftField("Models.ShoesModel", value?.ToString(CultureInfo.InvariantCulture)); } }
    public int? ActiveDraftGloveModel { get => ActiveDraft?.Models?.GloveModel; set { if (value != ActiveDraft?.Models?.GloveModel) UpdateActiveDraftField("Models.GloveModel", value?.ToString(CultureInfo.InvariantCulture)); } }
    public bool ActiveDraftForceKit { get => (ActiveDraft?.Models?.ForceKit ?? 0) != 0; set { if (value != ((ActiveDraft?.Models?.ForceKit ?? 0) != 0)) UpdateActiveDraftField("Models.ForceKit", value ? "1" : "0"); } }
    public bool ActiveDraftUniformCollarOpen
    {
        get => (ActiveDraft?.Models?.UniformCollarOpen ?? 0) != 0;
        set
        {
            if (value != ((ActiveDraft?.Models?.UniformCollarOpen ?? 0) != 0))
                UpdateActiveDraftField("Models.UniformCollarOpen", value ? "1" : "0");
        }
    }
    public int? ActiveDraftChestSize { get => ActiveDraft?.Models?.ChestSize; set { if (value != ActiveDraft?.Models?.ChestSize) UpdateActiveDraftField("Models.ChestSize", value?.ToString(CultureInfo.InvariantCulture)); } }

    private IReadOnlyList<LocalizedEquipment> GetAppearanceEquipment(EquipmentCategory category)
    {
        var key = (category, SelectedLanguageCode);
        if (!_appearanceEquipmentOptions.TryGetValue(key, out var options))
        {
            options = _gameTaxonomy?.GetEquipment(SelectedLanguageCode, category) ?? [];
            _appearanceEquipmentOptions[key] = options;
        }
        var current = category switch
        {
            EquipmentCategory.Uniform => ActiveDraft?.Models?.UniformModel,
            EquipmentCategory.Shoes => ActiveDraft?.Models?.ShoesModel,
            EquipmentCategory.Gloves => ActiveDraft?.Models?.GloveModel,
            _ => null,
        };
        if (current is null || options.Any(option => option.Id == current.Value)) return options;
        return options.Append(new LocalizedEquipment(
                current.Value,
                category,
                BuildEquipmentFallbackName(category, current.Value),
                $"0x{unchecked((uint)current.Value):X8}"))
            .ToArray();
    }

    private LocalizedEquipment? FindAppearanceEquipment(EquipmentCategory category, int? id) =>
        id is { } value
            ? GetAppearanceEquipment(category).FirstOrDefault(option => option.Id == value)
            : null;

    private string BuildEquipmentFallbackName(EquipmentCategory category, int id) => id == 0
        ? (SelectedLanguageCode == "es" ? "Sin equipar" : "Unequipped")
        : $"{category switch
        {
            EquipmentCategory.Uniform => SelectedLanguageCode == "es" ? "Uniforme" : "Uniform",
            EquipmentCategory.Shoes => SelectedLanguageCode == "es" ? "Zapatillas" : "Shoes",
            _ => SelectedLanguageCode == "es" ? "Guantes" : "Gloves",
        }} (0x{unchecked((uint)id):X8})";

    public string ActiveDraftStandardPortraitPath
    {
        get => ActiveDraft?.Assets?.StandardPortraitPath ?? string.Empty;
        set => UpdateActiveDraftField("Assets.StandardPortraitPath", value);
    }

    public string ActiveDraftStandardPortraitDisplayPath =>
        FormatPathForDisplay(ActiveDraftStandardPortraitPath);

    public string ActiveDraftUniformPortraitPath
    {
        get => ActiveDraft?.Assets?.UniformPortraitPath ?? string.Empty;
        set => UpdateActiveDraftField("Assets.UniformPortraitPath", value);
    }

    public string ActiveDraftUniformPortraitDisplayPath =>
        FormatPathForDisplay(ActiveDraftUniformPortraitPath);

    public UniformPortraitFallback ActiveDraftUniformFallback
    {
        get => ActiveDraft?.Assets?.UniformFallback ?? UniformPortraitFallback.Transparent;
        set => UpdateActiveDraftField("Assets.UniformFallback", value.ToString());
    }

    public IReadOnlyList<UniformPortraitFallback> UniformPortraitFallbacks { get; } =
        Enum.GetValues<UniformPortraitFallback>();

    public string ActiveDraftLocalizedName
    {
        get => ActiveDraft?.Localization?.LocalizedName ?? string.Empty;
        set => UpdateActiveDraftField("Localization.LocalizedName", value);
    }

    public string ActiveDraftRomanizedName
    {
        get => ActiveDraft?.Localization?.RomanizedName ?? string.Empty;
        set => UpdateActiveDraftField("Localization.RomanizedName", value);
    }

    public string ActiveDraftAcquisitionMethod
    {
        get => ActiveDraft?.Acquisition?.Method ?? string.Empty;
        set => UpdateActiveDraftField("Acquisition.Method", value);
    }

    public string ActiveDraftAcquisitionSource
    {
        get => ActiveDraft?.Acquisition?.Source ?? string.Empty;
        set => UpdateActiveDraftField("Acquisition.Source", value);
    }

    private void UpdateActiveDraftField(string field, string? value)
    {
        if (ActiveDraft is null) return;
        if (IsHumanReadablePathValue(field, value)) return;
        if (_isUpdatingActiveDraftField) return;
        if (string.Equals(
                ActiveDraft.Fields.GetValueOrDefault(field),
                value,
                StringComparison.Ordinal))
            return;

        _isUpdatingActiveDraftField = true;
        try
        {
            var updated = _draftService.Update(ActiveDraft, field, value);
            updated = SyncGameplayFieldToSelectedVariant(updated, field, value);
            ActiveDraft = updated;
            if (_activeDraftId is { } draftId)
            {
                Project.UpdateDraft(draftId, ActiveDraft);
                UpdateDraftRosterItem(draftId, ActiveDraft);
            }
            if (field == "Identity.BodyType")
            {
                NotifyPlayerCardChanged();
                SchedulePlayerCardAssetLoad(SelectedCharacter, SelectedVariant);
            }
            QueueRecoverySave();
        }
        finally
        {
            _isUpdatingActiveDraftField = false;
        }
    }

    private void PersistActiveDraft()
    {
        if (ActiveDraft is { } draft && _activeDraftId is { } draftId)
            Project.UpdateDraft(draftId, draft);
    }

    private CharacterDraft SyncGameplayFieldToSelectedVariant(
        CharacterDraft draft,
        string field,
        string? value)
    {
        if (draft.Variants is not { Count: > 0 } variants
            || field is "Gameplay.RegistrationProfile")
            return draft;

        var selected = _selectedTechniqueVariantChoice?.Variant;
        var selectedIndex = selected is null
            ? 0
            : variants
                .Select((variant, index) => (variant, index))
                .FirstOrDefault(item => item.variant.SourceParameterId == selected.ParameterId
                    && (item.variant.Gameplay.SpecialRarity ?? 0) == selected.SpecialRarity)
                .index;
        if ((uint)selectedIndex >= (uint)variants.Count)
            selectedIndex = 0;

        var current = variants[selectedIndex];
        var gameplay = field switch
        {
            "Gameplay.Affinity" => current.Gameplay with { Affinity = value ?? string.Empty },
            "Gameplay.MainPosition" => current.Gameplay with { MainPosition = ParseNullableInt(value) },
            "Gameplay.SubPosition" => current.Gameplay with { SubPosition = ParseNullableInt(value) },
            "Gameplay.PlayStyle" => current.Gameplay with { PlayStyle = ParseNullableInt(value) },
            "Gameplay.Growth" => current.Gameplay with { Growth = ParseNullableInt(value) },
            "Gameplay.Rank" => current.Gameplay with { Rank = ParseNullableInt(value) },
            "Gameplay.AbilityBoardId" => current.Gameplay with { AbilityBoardId = ParseNullableInt(value) },
            "Gameplay.SpecialRarity" => current.Gameplay with { SpecialRarity = ParseNullableInt(value) },
            _ => current.Gameplay,
        };
        if (ReferenceEquals(gameplay, current.Gameplay)) return draft;
        var updatedVariants = variants.ToArray();
        updatedVariants[selectedIndex] = current with { Gameplay = gameplay };
        return draft with { Variants = updatedVariants };
    }

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private IdentityChoice? FindIdentityChoice(
        IReadOnlyList<IdentityChoice> choices,
        string field) => choices.FirstOrDefault(choice =>
        string.Equals(choice.Value, ActiveDraft?.Fields.GetValueOrDefault(field), StringComparison.Ordinal));

    private IdentityChoice? FindPositionChoice(int? position) => position is { } value
        ? PositionChoices.FirstOrDefault(choice => choice.Value == CharacterPositionCatalog.Resolve(value).ToString())
        : null;

    private void UpdatePositionChoice(IdentityChoice? choice, string field)
    {
        if (choice is not null
            && Enum.TryParse<CharacterPosition>(choice.Value, out var position))
        {
            UpdateActiveDraftField(field, ((int)position).ToString(CultureInfo.InvariantCulture));
        }
    }

    private string FormatPathForDisplay(string path)
    {
        if (TryGetExtractedPackagePath(path, out var packagePath))
            return $"{WizardText.ExtractedFromPackage} · {packagePath}";
        return path;
    }

    private bool IsHumanReadablePathValue(string field, string? value) =>
        field is
            ("Models.HeadModelPath"
            or "Models.BodyModelPath"
            or "Assets.StandardPortraitPath"
            or "Assets.UniformPortraitPath")
        && value is not null
        && value.StartsWith($"{WizardText.ExtractedFromPackage} · ", StringComparison.Ordinal);

    private static bool IsOperationalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains(" · ", StringComparison.Ordinal);

    private static string? ResolveDraftPortraitPath(CharacterDraft draft)
    {
        var candidates = new[]
        {
            draft.Assets?.StandardPortraitPath,
            draft.Fields.GetValueOrDefault("Assets.StandardPortraitSourcePath"),
            draft.Fields.GetValueOrDefault("Assets.PortraitContainer"),
        };
        return candidates.FirstOrDefault(path => IsOperationalPath(path)
                && Path.IsPathRooted(path)
                && File.Exists(path))
            ?? candidates.FirstOrDefault(path => IsOperationalPath(path) && Path.IsPathRooted(path))
            ?? candidates.FirstOrDefault(IsOperationalPath);
    }

    private static string? ResolveDraftStandardPortraitPath(CharacterDraft draft) =>
        FirstOperationalPath(
            draft.Assets?.StandardPortraitPath,
            draft.Fields.GetValueOrDefault("Assets.StandardPortraitSourcePath"),
            draft.Fields.GetValueOrDefault("Assets.PortraitContainer"));

    private static string? ResolveDraftUniformPortraitPath(CharacterDraft draft) =>
        FirstOperationalPath(
            draft.Assets?.UniformPortraitPath,
            draft.Fields.GetValueOrDefault("Assets.UniformPortraitSourcePath"),
            ResolveDraftStandardPortraitPath(draft));

    private static string? FirstOperationalPath(params string?[] candidates) =>
        candidates.FirstOrDefault(IsOperationalPath);

    private static IReadOnlyList<CharacterVariantSummary>? DraftVariantSummaries(CharacterDraft draft) =>
        draft.Variants?.Select(variant => new CharacterVariantSummary(
            variant.SourceParameterId,
            0,
            ParseAffinity(variant.Gameplay.Affinity, CharacterAffinity.Unknown),
            variant.Gameplay.MainPosition ?? 0,
            variant.Gameplay.SubPosition ?? 0,
            variant.Gameplay.PlayStyle ?? 0,
            variant.Gameplay.Growth ?? 0,
            variant.Gameplay.Rank ?? 0,
            variant.Gameplay.AbilityBoardId ?? 0,
            variant.Skills.Slots
                .Select(slot => new CharacterSkillSlot(slot.SkillId ?? 0, slot.UnlockLevel ?? 0))
                .ToArray(),
            variant.Gameplay.SpecialRarity ?? 0))
        .ToArray();

    private static bool TryGetExtractedPackagePath(string path, out string packagePath)
    {
        packagePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;

        var relative = Path.GetRelativePath(Path.GetTempPath(), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        const string marker = "VictoryTool/vrchara/";
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!relative.StartsWith(marker, comparison)) return false;

        var afterSession = relative[marker.Length..];
        var separator = afterSession.IndexOf('/');
        if (separator <= 0 || separator == afterSession.Length - 1) return false;
        packagePath = afterSession[(separator + 1)..];
        return true;
    }

    private void UpdateIdentityChoice(
        IdentityChoice? choice,
        string valueField,
        string labelField)
    {
        if (choice is null || ActiveDraft is null) return;
        UpdateActiveDraftField(valueField, choice.Value);
        UpdateActiveDraftField(labelField, choice.Label);
    }

    private void UpdateLocalizationRow(string locale, string field, string? value)
    {
        _isUpdatingLocalizationRow = true;
        try
        {
            UpdateActiveDraftField($"Localization.{locale}.{field}", value);
        }
        finally
        {
            _isUpdatingLocalizationRow = false;
        }
    }

    private void RebuildLocalizationEditorRows()
    {
        LocalizationEditorRows.Clear();
        if (ActiveDraft is null) return;
        foreach (var locale in GameLocaleCatalog.SupportedCharacterLocales)
        {
            var localization = ActiveDraft.Localization ?? new CharacterDraftLocalization(
                null, null, GameLocaleCatalog.CreateEmptyLocalizations());
            localization.Locales.TryGetValue(locale, out var text);
            var row = new CharacterLocalizationEditorRow(
                locale,
                text ?? new CharacterDraftLocalizedText(null, null, null, null),
                UpdateLocalizationRow);
            row.SetApplicationLanguage(SelectedLanguageCode);
            LocalizationEditorRows.Add(row);
        }
        SelectedLocalizationEditorRow = LocalizationEditorRows.FirstOrDefault(
            row => string.Equals(row.Locale, SelectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            ?? LocalizationEditorRows.FirstOrDefault();
    }

    private void UpdateSkillRow(int slot, string field, string? value)
    {
        _isUpdatingSkillRow = true;
        try
        {
            UpdateActiveDraftField($"Skills.Slot{slot}.{field}", value);
        }
        finally
        {
            _isUpdatingSkillRow = false;
        }
        SyncSelectedTechniqueVariant();
    }

    private void SetTechniqueVariantChoices(
        IReadOnlyList<CharacterVariantSummary> variants,
        CharacterVariantSummary? selected = null)
    {
        _techniqueVariantChoices = variants
            .Select((variant, index) => new CharacterVariantChoice(variant, BuildTechniqueVariantLabel(variant, index + 1)))
            .ToArray();
        OnPropertyChanged(nameof(TechniqueVariantChoices));
        var selectedVariant = selected ?? variants.FirstOrDefault();
        var choice = _techniqueVariantChoices.FirstOrDefault(item =>
            selectedVariant is not null && SameVariant(item.Variant, selectedVariant));
        SetProperty(ref _selectedTechniqueVariantChoice, choice, nameof(SelectedTechniqueVariantChoice));
    }

    private static bool SameVariant(CharacterVariantSummary left, CharacterVariantSummary right) =>
        left.ParameterId == right.ParameterId
        && left.SpecialRarity == right.SpecialRarity;

    private void SetTechniqueVariantChoicesFromDraft(CharacterDraft? draft)
    {
        if (draft is null)
        {
            SetTechniqueVariantChoices([]);
            return;
        }

        // Legacy packages may not have a Variants array yet. Keep their primary
        // technique row editable instead of presenting an empty selector.
        var storedVariants = draft.Variants is { Count: > 0 } authoredVariants
            ? authoredVariants
            : [new CharacterDraftVariant(
                draft.Fields.TryGetValue("Gameplay.ParameterId", out var parameterId)
                    && int.TryParse(parameterId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedParameterId)
                    ? parsedParameterId
                    : 0,
                draft.Gameplay ?? new CharacterDraftGameplay("Unknown", null, null),
                draft.Skills ?? CharacterDraftSkills.FromLegacyFields(draft.Fields))];
        var variants = CharacterRarityCatalog.OrderForRuntime(storedVariants) ?? storedVariants;

        var summaries = variants.Select(variant => new CharacterVariantSummary(
            variant.SourceParameterId,
            0,
            ParseAffinity(variant.Gameplay.Affinity, CharacterAffinity.Unknown),
            variant.Gameplay.MainPosition ?? 0,
            variant.Gameplay.SubPosition ?? 0,
            variant.Gameplay.PlayStyle ?? 0,
            variant.Gameplay.Growth ?? 0,
            variant.Gameplay.Rank ?? 0,
            variant.Gameplay.AbilityBoardId ?? 0,
            variant.Skills.Slots
                .Select(slot => new CharacterSkillSlot(slot.SkillId ?? 0, slot.UnlockLevel ?? 0))
                .ToArray(),
            variant.Gameplay.SpecialRarity ?? 0)).ToArray();
        SetTechniqueVariantChoices(summaries, summaries.FirstOrDefault());
    }

    private string BuildTechniqueVariantLabel(CharacterVariantSummary variant, int number)
    {
        var rarity = SelectedLanguageCode == "es" ? "Rareza" : "Rarity";
        var position = ResolvePositionName(variant.MainPosition);
        var variantName = SelectedLanguageCode == "es" ? "Variante" : "Variant";
        return $"{variantName} {number} · {ResolveAffinityName(variant.Affinity)} · {position} · {rarity} {variant.SpecialRarity}";
    }

    private string ResolveAffinityName(CharacterAffinity affinity) => affinity switch
    {
        CharacterAffinity.Neutral => SelectedLanguageCode == "es" ? "Neutral" : "Neutral",
        CharacterAffinity.Wind => SelectedLanguageCode == "es" ? "Viento" : "Wind",
        CharacterAffinity.Forest => SelectedLanguageCode == "es" ? "Bosque" : "Forest",
        CharacterAffinity.Fire => SelectedLanguageCode == "es" ? "Fuego" : "Fire",
        CharacterAffinity.Earth => SelectedLanguageCode == "es" ? "Tierra" : "Earth",
        _ => SelectedLanguageCode == "es" ? "Desconocida" : "Unknown",
    };

    private string ResolvePositionName(int position) => CharacterPositionCatalog.Resolve(position) switch
    {
        CharacterPosition.Goalkeeper => SelectedLanguageCode == "es" ? "Portero" : "Goalkeeper",
        CharacterPosition.Forward => SelectedLanguageCode == "es" ? "Delantero" : "Forward",
        CharacterPosition.Midfielder => SelectedLanguageCode == "es" ? "Centrocampista" : "Midfielder",
        CharacterPosition.Defender => SelectedLanguageCode == "es" ? "Defensa" : "Defender",
        _ => SelectedLanguageCode == "es" ? "Desconocida" : "Unknown",
    };

    private string ResolvePositionName(string? position) => position?.ToLowerInvariant() switch
    {
        "goalkeeper" or "portero" => ResolvePositionName((int)CharacterPosition.Goalkeeper),
        "forward" or "delantero" => ResolvePositionName((int)CharacterPosition.Forward),
        "midfielder" or "centrocampista" => ResolvePositionName((int)CharacterPosition.Midfielder),
        "defender" or "defensa" => ResolvePositionName((int)CharacterPosition.Defender),
        _ => ResolvePositionName(0),
    };

    private void ApplyTechniqueVariant(CharacterVariantSummary variant)
    {
        if (ActiveDraft is null) return;
        var fields = new Dictionary<string, string?>(ActiveDraft.Fields, StringComparer.Ordinal)
        {
            ["Gameplay.ParameterId"] = variant.ParameterId.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.BaseId"] = variant.BaseId.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.Affinity"] = variant.Affinity.ToString(),
            ["Gameplay.MainPosition"] = variant.MainPosition.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.SubPosition"] = variant.SubPosition.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.PlayStyle"] = variant.PlayStyle.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.Growth"] = variant.Growth.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.Rank"] = variant.Rank.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.AbilityBoardId"] = variant.AbilityBoardId.ToString(CultureInfo.InvariantCulture),
            ["Gameplay.SpecialRarity"] = variant.SpecialRarity.ToString(CultureInfo.InvariantCulture),
        };
        var skillSlots = CharacterDraftSkills.Empty.Slots.ToArray();
        for (var index = 0; index < skillSlots.Length && index < variant.SkillSlots.Count; index++)
        {
            var skill = variant.SkillSlots[index];
            skillSlots[index] = skillSlots[index] with
            {
                SkillId = skill.SkillId,
                UnlockLevel = skill.UnlockLevel,
            };
            fields[$"Skills.Slot{index + 1}"] =
                $"{skill.SkillId.ToString(CultureInfo.InvariantCulture)}:{skill.UnlockLevel.ToString(CultureInfo.InvariantCulture)}";
            fields[$"Skills.Slot{index + 1}.SkillId"] = skill.SkillId.ToString(CultureInfo.InvariantCulture);
            fields[$"Skills.Slot{index + 1}.UnlockLevel"] = skill.UnlockLevel.ToString(CultureInfo.InvariantCulture);
        }

        var currentGameplay = ActiveDraft.Gameplay ?? new CharacterDraftGameplay("Neutral", null, null);
        var updated = ActiveDraft with
        {
            Fields = fields,
            Gameplay = currentGameplay with
            {
                Affinity = variant.Affinity.ToString(),
                MainPosition = variant.MainPosition,
                SubPosition = variant.SubPosition,
                PlayStyle = variant.PlayStyle,
                Growth = variant.Growth,
                Rank = variant.Rank,
                AbilityBoardId = variant.AbilityBoardId,
                SpecialRarity = variant.SpecialRarity,
            },
            Skills = new CharacterDraftSkills(skillSlots),
            IsDirty = true,
        };
        ActiveDraft = updated with { Diagnostics = _draftService.Validate(updated) };
        if (_activeDraftId is { } draftId)
            Project.UpdateDraft(draftId, ActiveDraft);
        RebuildSkillEditorRows();
        NotifyReviewChanged();
    }

    private void SyncSelectedTechniqueVariant()
    {
        if (ActiveDraft?.Variants is not { Count: > 0 } variants
            || _selectedTechniqueVariantChoice is null)
            return;
        var sourceParameterId = _selectedTechniqueVariantChoice.Variant.ParameterId;
        var specialRarity = _selectedTechniqueVariantChoice.Variant.SpecialRarity;
        var updated = variants
            .Select(variant => variant.SourceParameterId == sourceParameterId
                && (variant.Gameplay.SpecialRarity ?? 0) == specialRarity
                ? variant with { Skills = ActiveDraft.Skills }
                : variant)
            .ToArray();
        ActiveDraft = ActiveDraft with { Variants = updated };
        if (_activeDraftId is { } draftId)
            Project.UpdateDraft(draftId, ActiveDraft);
        RefreshTechniqueVariantChoicesFromDraft();
    }

    private void RefreshTechniqueVariantChoicesFromDraft()
    {
        if (ActiveDraft?.Variants is not { Count: > 0 }) return;
        var currentVariant = _selectedTechniqueVariantChoice?.Variant;
        _techniqueVariantChoices = _techniqueVariantChoices
            .Select(choice =>
            {
                var draftVariant = ActiveDraft.Variants.FirstOrDefault(
                    variant => variant.SourceParameterId == choice.Variant.ParameterId
                        && (variant.Gameplay.SpecialRarity ?? 0) == choice.Variant.SpecialRarity);
                if (draftVariant is null) return choice;
                return choice with
                {
                    Variant = choice.Variant with
                    {
                        Affinity = ParseAffinity(draftVariant.Gameplay.Affinity, choice.Variant.Affinity),
                        MainPosition = draftVariant.Gameplay.MainPosition ?? choice.Variant.MainPosition,
                        SubPosition = draftVariant.Gameplay.SubPosition ?? choice.Variant.SubPosition,
                        PlayStyle = draftVariant.Gameplay.PlayStyle ?? choice.Variant.PlayStyle,
                        Growth = draftVariant.Gameplay.Growth ?? choice.Variant.Growth,
                        Rank = draftVariant.Gameplay.Rank ?? choice.Variant.Rank,
                        AbilityBoardId = draftVariant.Gameplay.AbilityBoardId ?? choice.Variant.AbilityBoardId,
                        SpecialRarity = draftVariant.Gameplay.SpecialRarity ?? choice.Variant.SpecialRarity,
                        SkillSlots = draftVariant.Skills.Slots
                            .Select(slot => new CharacterSkillSlot(slot.SkillId ?? 0, slot.UnlockLevel ?? 0))
                            .ToArray(),
                    },
                };
            })
            .ToArray();
        OnPropertyChanged(nameof(TechniqueVariantChoices));
        var selected = _techniqueVariantChoices.FirstOrDefault(
            choice => currentVariant is not null && SameVariant(choice.Variant, currentVariant));
        selected ??= _techniqueVariantChoices.FirstOrDefault(
            choice => currentVariant is not null && choice.Variant.ParameterId == currentVariant.ParameterId);
        SetProperty(ref _selectedTechniqueVariantChoice, selected, nameof(SelectedTechniqueVariantChoice));
    }

    private void ApplyTechniqueTemplateToAllVariants()
    {
        if (ActiveDraft?.Variants is not { Count: > 0 } variants) return;
        var skills = ActiveDraft.Skills ?? CharacterDraftSkills.FromLegacyFields(ActiveDraft.Fields);
        ActiveDraft = ActiveDraft with
        {
            Skills = skills,
            Variants = variants.Select(variant => variant with { Skills = skills }).ToArray(),
        };
        if (_activeDraftId is { } draftId)
            Project.UpdateDraft(draftId, ActiveDraft);
        RefreshTechniqueVariantChoicesFromDraft();
        RebuildSkillEditorRows();
        NotifyReviewChanged();
        StatusMessage = SelectedLanguageCode == "es"
            ? "La plantilla de técnicas se ha aplicado a todas las variantes."
            : "The technique template was applied to every variant.";
    }

    private void RebuildSkillEditorRows()
    {
        SkillEditorRows.Clear();
        if (ActiveDraft is null)
        {
            OnPropertyChanged(nameof(MainSkillEditorRows));
            OnPropertyChanged(nameof(AlternateSkillEditorRows));
            return;
        }
        var skills = ActiveDraft.Skills ?? CharacterDraftSkills.FromLegacyFields(ActiveDraft.Fields);
        foreach (var slot in skills.Slots)
            SkillEditorRows.Add(new CharacterSkillEditorRow(slot, SkillChoices, WizardText.UnlockLevel, UpdateSkillRow));
        OnPropertyChanged(nameof(MainSkillEditorRows));
        OnPropertyChanged(nameof(AlternateSkillEditorRows));
    }

    private void NotifyActiveDraftEditorProperties()
    {
        OnPropertyChanged(nameof(ActiveDraftAffinity));
        OnPropertyChanged(nameof(ActiveDraftOriginGameChoice));
        OnPropertyChanged(nameof(ActiveDraftAffinityCategory));
        OnPropertyChanged(nameof(ActiveDraftAffinityChoice));
        OnPropertyChanged(nameof(ActiveDraftMainPositionChoice));
        OnPropertyChanged(nameof(ActiveDraftSubPositionChoice));
        OnPropertyChanged(nameof(ActiveDraftRegistrationProfile));
        OnPropertyChanged(nameof(UsesFunctionalBankProfile));
        OnPropertyChanged(nameof(ActiveDraftPlayStyle));
        OnPropertyChanged(nameof(ActiveDraftPlayStyleValue));
        OnPropertyChanged(nameof(ActiveDraftGrowth));
        OnPropertyChanged(nameof(ActiveDraftGrowthValue));
        OnPropertyChanged(nameof(ActiveDraftRank));
        OnPropertyChanged(nameof(ActiveDraftRankValue));
        OnPropertyChanged(nameof(ActiveDraftAbilityBoardId));
        OnPropertyChanged(nameof(ActiveDraftAbilityBoardIdValue));
        OnPropertyChanged(nameof(ActiveDraftSpecialRarity));
        OnPropertyChanged(nameof(ActiveDraftSpecialRarityValue));
        OnPropertyChanged(nameof(ActiveDraftMainPosition));
        OnPropertyChanged(nameof(ActiveDraftMainPositionCategory));
        OnPropertyChanged(nameof(ActiveDraftSubPosition));
        OnPropertyChanged(nameof(ActiveDraftSubPositionCategory));
        OnPropertyChanged(nameof(ActiveDraftMainPositionDiagnostic));
        OnPropertyChanged(nameof(ActiveDraftSubPositionDiagnostic));
        OnPropertyChanged(nameof(ActiveDraftHeadModelPath));
        OnPropertyChanged(nameof(ActiveDraftHeadModelDisplayPath));
        OnPropertyChanged(nameof(ActiveDraftBodyModelPath));
        OnPropertyChanged(nameof(BodyTypeChoices));
        OnPropertyChanged(nameof(ActiveDraftBodyType));
        OnPropertyChanged(nameof(ActiveDraftBodyTypeChoice));
        OnPropertyChanged(nameof(ActiveDraftBodyTypeDiagnostic));
        OnPropertyChanged(nameof(ActiveDraftSkinColor));
        OnPropertyChanged(nameof(ActiveDraftUniformModel));
        OnPropertyChanged(nameof(ActiveDraftShoesModel));
        OnPropertyChanged(nameof(ActiveDraftGloveModel));
        OnPropertyChanged(nameof(ActiveDraftUniformChoice));
        OnPropertyChanged(nameof(ActiveDraftShoesChoice));
        OnPropertyChanged(nameof(ActiveDraftGloveChoice));
        OnPropertyChanged(nameof(ActiveDraftForceKit));
        OnPropertyChanged(nameof(ActiveDraftUniformCollarOpen));
        OnPropertyChanged(nameof(ActiveDraftChestSize));
        OnPropertyChanged(nameof(ActiveDraftStandardPortraitPath));
        OnPropertyChanged(nameof(ActiveDraftStandardPortraitDisplayPath));
        OnPropertyChanged(nameof(ActiveDraftUniformPortraitPath));
        OnPropertyChanged(nameof(ActiveDraftUniformPortraitDisplayPath));
        OnPropertyChanged(nameof(ActiveDraftUniformFallback));
        OnPropertyChanged(nameof(ActiveDraftLocalizedName));
        OnPropertyChanged(nameof(ActiveDraftRomanizedName));
        OnPropertyChanged(nameof(ActiveDraftAcquisitionMethod));
        OnPropertyChanged(nameof(ActiveDraftAcquisitionSource));
    }

    private void NotifyReviewChanged()
    {
        OnPropertyChanged(nameof(ReviewDisplayName));
        OnPropertyChanged(nameof(ReviewSeries));
        OnPropertyChanged(nameof(ReviewOriginGame));
        OnPropertyChanged(nameof(ReviewAffinity));
        OnPropertyChanged(nameof(ReviewMainPosition));
    }

    private void RefreshAppearancePortrait()
    {
        var assets = ActiveDraft?.Assets;
        var path = new[] { assets?.StandardPortraitPath, assets?.UniformPortraitPath }
            .FirstOrDefault(candidate => IsOperationalPath(candidate)
                && candidate!.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate));
        if (!string.Equals(path, _appearancePortraitPath, StringComparison.OrdinalIgnoreCase))
        {
            var previous = _appearancePortrait;
            _appearancePortrait = null;
            _appearancePortraitPath = path;
            if (!string.IsNullOrWhiteSpace(path)
                && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path))
            {
                try
                {
                    _appearancePortrait = new Bitmap(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _appearancePortraitPath = null;
                }
            }
            previous?.Dispose();
        }
        OnPropertyChanged(nameof(AppearancePortrait));
        OnPropertyChanged(nameof(HasLayeredAppearance));
        OnPropertyChanged(nameof(ShowAppearancePortrait));
    }

    private string? GetActiveDraftDiagnostic(string field) => ActiveDraft?.Diagnostics
        .FirstOrDefault(diagnostic => string.Equals(diagnostic.Field, field, StringComparison.Ordinal))
        ?.Message;

    public void SetUserStatusMessage(string message) => StatusMessage = message;

    private string GetActiveDraftField(string field, string fallback) =>
        ActiveDraft?.Fields.TryGetValue(field, out var value) == true && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    public ExportPlatform ExportPlatform
    {
        get => _exportPlatform;
        set
        {
            if (SetProperty(ref _exportPlatform, value))
                InvalidateExportPlan();
        }
    }

    public AcquisitionMode AcquisitionMode
    {
        get => _acquisitionMode;
        set
        {
            if (SetProperty(ref _acquisitionMode, value))
                InvalidateExportPlan();
        }
    }

    public string ExportOutputPath
    {
        get => _exportOutputPath;
        set
        {
            _exportOutputPathGenerated = false;
            if (SetProperty(ref _exportOutputPath, value))
                InvalidateExportPlan();
        }
    }

    public void SetGeneratedExportOutputPath(string path)
    {
        _exportOutputPathGenerated = true;
        if (SetProperty(ref _exportOutputPath, path))
            InvalidateExportPlan();
    }

    public ExportPlan? CurrentExportPlan
    {
        get => _currentExportPlan;
        private set
        {
            if (!SetProperty(ref _currentExportPlan, value)) return;
            OnPropertyChanged(nameof(HasExportPlan));
            OnPropertyChanged(nameof(ExportPlanSummaryLines));
            OnPropertyChanged(nameof(CanExecuteExport));
        }
    }

    private void InvalidateExportPlan()
    {
        if (_currentExportPlan is null) return;
        CurrentExportPlan = null;
        Diagnostics.ReplaceWith(_catalogDiagnostics);
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(DiagnosticReport));
    }

    public bool HasExportPlan => CurrentExportPlan is not null;
    public bool CanExecuteExport => CurrentExportPlan?.CanExport == true;

    public IReadOnlyList<string> ExportPlanSummaryLines => CurrentExportPlan is not { } plan
        ? []
        : plan.AssignedIds
            .Select(assignment => $"ID {assignment.SymbolicKey} [{assignment.Domain}] = {assignment.NumericId}")
            .Concat(plan.AffectedFiles.Select(path => $"CFGBIN {path}"))
            .Concat(plan.ResourceOperations.Select(operation => $"RESOURCE {operation.DestinationPath}"))
            .Concat(plan.GameReferenceOperations.Select(operation =>
                $"REFERENCE {operation.VirtualPath} ({(operation.Exists ? "resolved" : "missing")})"))
            .Concat(plan.PatchOperations.Select(operation =>
                $"PATCH {operation.TablePath} :: {operation.SymbolicKey}"))
            .Concat(plan.LocalizationOperations.Select(operation =>
                $"LOCALIZATION [{operation.Locale}] {operation.NameTablePath}"))
            .Concat(plan.FileOperations.Select(operation =>
                $"COPY {operation.DestinationPath}"))
            .Concat(plan.ModelDependencyOperations.Select(operation =>
                $"MODEL {operation.Kind} {operation.VirtualPath}"))
            .Concat(plan.CharacterCoreOperations.Select(operation =>
                $"CHARACTER CORE {operation.BaseTablePath} + {operation.ParameterTablePath}"))
            .Concat(plan.ShopCharacterOperations.Select(operation =>
                $"SHOP {operation.ShopTablePath} from item {operation.SourceItemId}"))
            .Concat(plan.CharacterDeliveryOperations.Select(operation =>
                $"DELIVERY {operation.DeliveryTablePath}"))
            .ToArray();

    public BatchEntry? SelectedBatchEntry
    {
        get => _selectedBatchEntry;
        set
        {
            if (SetProperty(ref _selectedBatchEntry, value))
            {
                OnPropertyChanged(nameof(HasSelectedBatchEntry));
                OnPropertyChanged(nameof(SelectedBatchShopSourceItemId));
                OnPropertyChanged(nameof(SelectedBatchShopRarity));
                OnPropertyChanged(nameof(SelectedBatchShopSpecialVariant));
            }
        }
    }

    public int? SelectedBatchShopSourceItemId
    {
        get => SelectedBatchEntry?.Acquisition?.ShopSourceItemId;
        set => UpdateSelectedBatchAcquisition(current => current with
        {
            ShopSourceItemId = value,
            IsFree = false,
            ShopSourceParameterId = null,
        });
    }

    public int? SelectedBatchShopRarity
    {
        get => SelectedBatchEntry?.Acquisition?.ShopRarity;
        set => UpdateSelectedBatchAcquisition(current => current with { ShopRarity = value });
    }

    public int? SelectedBatchShopSpecialVariant
    {
        get => SelectedBatchEntry?.Acquisition?.ShopSpecialVariant;
        set => UpdateSelectedBatchAcquisition(current => current with { ShopSpecialVariant = value });
    }

    public bool HasSelectedBatchEntry => SelectedBatchEntry is not null;
    public bool HasBatchEntries => Batch.Count != 0;
    public bool HasDiagnostics => Diagnostics.Count != 0;
    public string DiagnosticReport => Diagnostics.Count == 0
        ? string.Empty
        : string.Join("\n", Diagnostics.Select(diagnostic =>
            $"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}" +
            (string.IsNullOrWhiteSpace(diagnostic.RecoveryAction)
                ? string.Empty
                : $" Acción: {diagnostic.RecoveryAction}")));

    public string DraftPackagePath
    {
        get => _draftPackagePath;
        set => SetProperty(ref _draftPackagePath, value);
    }

    public string? LocalesJsonPath => _localesJsonPath;

    public UiText Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public IReadOnlyList<string> LanguageCodes => GameLocaleCatalog.SupportedCharacterLocales;

    public string SelectedLanguageCode
    {
        get => _selectedLanguageCode;
        set
        {
            var normalized = GameLocaleCatalog.SupportedCharacterLocales.Contains(
                value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant() : "en";
            if (!SetProperty(ref _selectedLanguageCode, normalized)) return;
            Text = UiText.ForLocale(normalized);
            WizardText = WizardText.ForLocale(normalized);
            foreach (var row in LocalizationEditorRows) row.SetApplicationLanguage(normalized);
            OnPropertyChanged(nameof(SkillChoices));
            if (_techniqueVariantChoices.Count > 0)
            {
                var selected = _selectedTechniqueVariantChoice?.Variant;
                SetTechniqueVariantChoices(_techniqueVariantChoices.Select(choice => choice.Variant).ToArray(), selected);
            }
            if (ActiveDraft is not null && !_isUpdatingSkillRow) RebuildSkillEditorRows();
            OnPropertyChanged(nameof(AppearanceUniformOptions));
            OnPropertyChanged(nameof(AppearanceShoesOptions));
            OnPropertyChanged(nameof(AppearanceGloveOptions));
            OnPropertyChanged(nameof(ActiveDraftUniformChoice));
            OnPropertyChanged(nameof(ActiveDraftShoesChoice));
            OnPropertyChanged(nameof(ActiveDraftGloveChoice));
            OnPropertyChanged(nameof(BodyTypeChoices));
            OnPropertyChanged(nameof(ActiveDraftBodyTypeChoice));
            OnPropertyChanged(nameof(IdentityGenderChoices));
            OnPropertyChanged(nameof(IdentityOriginGameChoices));
            OnPropertyChanged(nameof(AffinityChoices));
            OnPropertyChanged(nameof(PositionChoices));
            OnPropertyChanged(nameof(ActiveDraftGenderChoice));
            OnPropertyChanged(nameof(ActiveDraftOriginGameChoice));
            OnPropertyChanged(nameof(ActiveDraftAffinityChoice));
            OnPropertyChanged(nameof(ActiveDraftMainPositionChoice));
            OnPropertyChanged(nameof(ActiveDraftSubPositionChoice));
            OnPropertyChanged(nameof(ActiveDraftHeadModelDisplayPath));
            OnPropertyChanged(nameof(ActiveDraftStandardPortraitDisplayPath));
            OnPropertyChanged(nameof(ActiveDraftUniformPortraitDisplayPath));
            _teamAssociationOptions.Clear();
            OnPropertyChanged(nameof(TeamAssociationOptions));
            OnPropertyChanged(nameof(ActiveDraftTeamAssociationChoice));
            OnPropertyChanged(nameof(LanguageCode));
            NotifyPlayerCardChanged();
            NotifyReviewChanged();
            _ = SaveSettingsAsync();
        }
    }

    public WizardText WizardText
    {
        get => _wizardText;
        private set => SetProperty(ref _wizardText, value);
    }

    public string LanguageCode => SelectedLanguageCode.ToUpperInvariant();

    public LayoutDensity LayoutDensity
    {
        get => _layoutDensity;
        private set
        {
            if (SetProperty(ref _layoutDensity, value))
            {
                OnPropertyChanged(nameof(IsWideLayout));
                OnPropertyChanged(nameof(IsCompactLayout));
                OnPropertyChanged(nameof(IsBatchDockVisible));
                OnPropertyChanged(nameof(PreviewPaneColumnWidth));
            }
        }
    }

    public bool IsWideLayout => LayoutDensity == LayoutDensity.Wide;
    public bool IsCompactLayout => LayoutDensity == LayoutDensity.Compact;
    public bool IsBatchDockVisible => LayoutDensity != LayoutDensity.Compact;

    public double RosterPaneWidth
    {
        get => _rosterPaneWidth;
        set
        {
            if (SetProperty(ref _rosterPaneWidth, NormalizePaneWidth(value, 350)))
                _ = SaveSettingsAsync();
        }
    }

    public double PreviewPaneWidth
    {
        get => _previewPaneWidth;
        set
        {
            if (SetProperty(ref _previewPaneWidth, NormalizePaneWidth(value, 300)))
            {
                OnPropertyChanged(nameof(PreviewPaneColumnWidth));
                _ = SaveSettingsAsync();
            }
        }
    }

    public double PreviewPaneColumnWidth
    {
        get => IsWideLayout ? PreviewPaneWidth : 0;
        set
        {
            if (value > 0) PreviewPaneWidth = value;
        }
    }

    public CharacterEditorSection ActiveEditorSection
    {
        get => _activeEditorSection;
        private set
        {
            if (!SetProperty(ref _activeEditorSection, value)) return;
            OnPropertyChanged(nameof(IsIdentityEditorSection));
            OnPropertyChanged(nameof(IsGameplayEditorSection));
            OnPropertyChanged(nameof(IsStatisticsEditorSection));
            OnPropertyChanged(nameof(IsSkillsEditorSection));
            OnPropertyChanged(nameof(IsModelsEditorSection));
            OnPropertyChanged(nameof(IsAssetsEditorSection));
            OnPropertyChanged(nameof(IsLocalizationEditorSection));
            OnPropertyChanged(nameof(IsAcquisitionEditorSection));
            OnPropertyChanged(nameof(IsAdvancedEditorSection));
        }
    }

    public bool IsIdentityEditorSection => ActiveEditorSection == CharacterEditorSection.Identity;
    public bool IsGameplayEditorSection => ActiveEditorSection == CharacterEditorSection.Gameplay;
    public bool IsStatisticsEditorSection => ActiveEditorSection == CharacterEditorSection.Statistics;
    public bool IsSkillsEditorSection => ActiveEditorSection == CharacterEditorSection.Skills;
    public bool IsModelsEditorSection => ActiveEditorSection == CharacterEditorSection.Models;
    public bool IsAssetsEditorSection => ActiveEditorSection == CharacterEditorSection.Assets;
    public bool IsLocalizationEditorSection => ActiveEditorSection == CharacterEditorSection.Localization;
    public bool IsAcquisitionEditorSection => ActiveEditorSection == CharacterEditorSection.Acquisition;
    public bool IsAdvancedEditorSection => ActiveEditorSection == CharacterEditorSection.Advanced;

    public void NavigateEditorSection(CharacterEditorSection section) => ActiveEditorSection = section;

    public ObservableCollection<string> CfgBinFiles { get; } = [];
    public ObservableCollection<string> PackagePaths { get; } = [];
    public ObservableCollection<CharacterCatalogItem> Characters { get; } = [];
    public ObservableCollection<CharacterCatalogItem> FilteredCharacters { get; } = [];
    public ObservableCollection<CharacterRosterRow> RosterRows { get; } = [];
    public ObservableCollection<BatchEntry> Batch { get; } = [];
    public ObservableCollection<Diagnostic> Diagnostics { get; } = [];
    public ObservableCollection<CharacterLocalizationEditorRow> LocalizationEditorRows { get; } = [];
    public CharacterLocalizationEditorRow? SelectedLocalizationEditorRow
    {
        get => _selectedLocalizationEditorRow;
        set => SetProperty(ref _selectedLocalizationEditorRow, value);
    }
    public ObservableCollection<CharacterSkillEditorRow> SkillEditorRows { get; } = [];
    public IEnumerable<CharacterSkillEditorRow> MainSkillEditorRows =>
        SkillEditorRows.Where(row => row.Path == CharacterSkillPath.Main);
    public IEnumerable<CharacterSkillEditorRow> AlternateSkillEditorRows =>
        SkillEditorRows.Where(row => row.Path == CharacterSkillPath.Alternate);

    public ICommand LoadGameDumpCommand { get; }
    public ICommand AddPackageCommand { get; }
    public ICommand CloneSelectedCommand { get; }
    public ICommand CreateBlankDraftCommand { get; }
    public ICommand BeginCreateCommand { get; }
    public ICommand RemoveActiveDraftCommand { get; }
    public ICommand ConfirmRemoveActiveDraftCommand { get; }
    public ICommand CancelRemoveActiveDraftCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand PreviewExportCommand { get; }
    public ICommand ExecuteExportCommand { get; }
    public ICommand ToggleLanguageCommand { get; }
    public ICommand NextWizardStepCommand { get; }
    public ICommand PreviousWizardStepCommand { get; }
    public ICommand GoToWizardStepCommand { get; }
    public ICommand ApplyTechniqueTemplateCommand { get; }
    public ICommand DuplicateBatchEntryCommand { get; }
    public ICommand ToggleBatchEntryCommand { get; }
    public ICommand RemoveBatchEntryCommand { get; }
    public ICommand MoveBatchEntryUpCommand { get; }
    public ICommand MoveBatchEntryDownCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand OpenLocalesJsonCommand { get; }
    public ICommand CancelIndexingCommand { get; }
    public ICommand ClearCharacterFiltersCommand { get; }
    public ICommand DuplicateSelectedCharacterCommand { get; }
    public ICommand DeleteSelectedCharacterCommand { get; }
    public ICommand NavigateEditorSectionCommand { get; }
    public ICommand SetVerifiedLevelCommand { get; }

    public void LoadGameDump() => LoadGameDumpAsync().GetAwaiter().GetResult();

    public async Task InitializeAsync()
    {
        if (_settingsStore is null) return;
        try
        {
            var settings = await _settingsStore.LoadAsync(CancellationToken.None);
            SelectedLanguageCode = settings.LanguageCode;

            if (settings.RosterPaneWidth is { } rosterWidth)
                SetProperty(ref _rosterPaneWidth, NormalizePaneWidth(rosterWidth, 350), nameof(RosterPaneWidth));
            if (settings.PreviewPaneWidth is { } previewWidth)
                SetProperty(ref _previewPaneWidth, NormalizePaneWidth(previewWidth, 300), nameof(PreviewPaneWidth));

            if (!string.IsNullOrWhiteSpace(settings.GameDumpRoot))
            {
                GameDumpInput = settings.GameDumpRoot;
                await LoadGameDumpAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Application settings could not be restored: {exception.Message}";
        }
    }

    public async Task LoadGameDumpAsync()
    {
        if (IsIndexing) return;
        _indexCancellation?.Dispose();
        _indexCancellation = new CancellationTokenSource();
        IsIndexing = true;
        try
        {
            ReportIndexProgress(new IndexProgress(IndexStage.Validation, 0, 1, "Validating selected game dump."));
            var workspace = GameDumpWorkspace.Open(GameDumpInput);
            var profile = GameDumpProfile.Create(workspace.RootPath);
            var progress = new Progress<IndexProgress>(ReportIndexProgress);
            var catalog = await _catalogService.IndexAsync(profile, progress, _indexCancellation.Token);

            CfgBinFiles.ReplaceWith(workspace.EnumerateCfgBinFiles());
            ClearRosterPortraits();
            Characters.ReplaceWith(catalog.Characters);
            _gameTaxonomy = catalog.Characters.FirstOrDefault()?.Taxonomy;
            _appearanceEquipmentOptions.Clear();
            _teamAssociationOptions.Clear();
            _gameTextReferences = catalog.Characters.FirstOrDefault()?.TextReferences;
            OnPropertyChanged(nameof(SkillChoices));
            OnPropertyChanged(nameof(AppearanceUniformOptions));
            OnPropertyChanged(nameof(AppearanceShoesOptions));
            OnPropertyChanged(nameof(AppearanceGloveOptions));
            OnPropertyChanged(nameof(TeamAssociationOptions));
            OnPropertyChanged(nameof(ActiveDraftTeamAssociationChoice));
            _draftIdsByRosterId.Clear();
            foreach (var draft in Project.Drafts)
                AddDraftToRoster(draft, select: false);
            NotifyFilterOptionsChanged();
            _catalogDiagnostics = catalog.Diagnostics.ToArray();
            Diagnostics.ReplaceWith(_catalogDiagnostics);
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(DiagnosticReport));
            _playerCardAssetProfile = profile;
            if (_loadDumpStatistics)
            {
                try
                {
                    _characterStatCalculator = RdbnpGrowthStatCalculator.Load(profile);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or NotSupportedException)
                {
                    _characterStatCalculator = new CharacterStatCalculator(new DocumentedStatFormulaProvider([]));
                    var diagnostic = new Diagnostic(
                        "statistics.growth_table_unavailable",
                        DiagnosticSeverity.Warning,
                        $"Verified milestone statistics could not be loaded: {exception.Message}",
                        "The player card keeps statistics unavailable instead of estimating values.");
                    _catalogDiagnostics = _catalogDiagnostics.Append(diagnostic).ToArray();
                    Diagnostics.Add(diagnostic);
                    OnPropertyChanged(nameof(HasDiagnostics));
                    OnPropertyChanged(nameof(DiagnosticReport));
                }
            }
            RecalculatePlayerCardStatistics();
            ApplySearchNow();
            InvalidateExportPlan();
            Project.ClearBatch();
            PackagePaths.Clear();
            SelectedBatchEntry = null;
            RefreshBatch();
            ActiveGameDumpPath = workspace.RootPath;
            GameDumpInput = workspace.RootPath;
            IsWorkspaceReady = true;
            ActiveWorkspace = WorkspaceKind.Project;
            WizardStep = WizardStep.Setup;
            StatusMessage = "Game dump loaded.";
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Game dump indexing was cancelled.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or GameDumpValidationException)
        {
            StatusMessage = IsWorkspaceReady
                ? $"The replacement dump could not be loaded. {exception.Message}"
                : exception.Message;
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private void ReportIndexProgress(IndexProgress progress)
    {
        IndexProgressMessage = progress.Message;
        StatusMessage = progress.Message;
    }

    public void CancelIndexing() => _indexCancellation?.Cancel();

    public void ClearCharacterFilters()
    {
        SetProperty(ref _selectedAffinity, null, nameof(SelectedAffinity));
        SetProperty(ref _selectedOrigin, null, nameof(SelectedOrigin));
        SetProperty(ref _selectedSeries, null, nameof(SelectedSeries));
        SetProperty(ref _selectedAcademicYear, null, nameof(SelectedAcademicYear));
        SetProperty(ref _selectedGender, null, nameof(SelectedGender));
        SetProperty(ref _selectedBodyType, null, nameof(SelectedBodyType));
        SetProperty(ref _selectedPosition, null, nameof(SelectedPosition));
        SetProperty(ref _selectedPlayStyle, null, nameof(SelectedPlayStyle));
        SetProperty(ref _selectedRank, null, nameof(SelectedRank));
        SetProperty(ref _selectedSpecialRarity, null, nameof(SelectedSpecialRarity));
        ApplySearchNow();
    }

    public void AddPackage()
    {
        if (!IsWorkspaceReady)
        {
            StatusMessage = "Load a game dump before adding character packages.";
            return;
        }

        try
        {
            AcquisitionMode = AcquisitionMode.Delivery;
            if (Project.ContainsPackagePath(PackageInput))
            {
                StatusMessage = "The character package is already in the batch.";
                ActiveWorkspace = WorkspaceKind.Batch;
                WizardStep = WizardStep.Setup;
                return;
            }

            var entry = Project.AddPackage(PackageInput);
            entry = Project.Batch.Single(item => item.Id == entry.Id);
            PackagePaths.Add(entry.PackagePath);
            Batch.Add(entry);
            InvalidateExportPlan();
            OnPropertyChanged(nameof(HasBatchEntries));
            ActiveWorkspace = WorkspaceKind.Batch;
            WizardStep = WizardStep.Setup;
            StatusMessage = "Character package added to the batch.";
            QueueRecoverySave();
        }
        catch (ArgumentException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    public void CloneSelectedCharacter()
    {
        if (SelectedCharacter is null)
        {
            StatusMessage = "Select a character before cloning.";
            return;
        }

        // A custom roster row is only a presentation projection. Clone the
        // project draft itself so every category survives, including fields
        // that the roster deliberately does not expose.
        if (_draftIdsByRosterId.TryGetValue(SelectedCharacter.Id, out var sourceDraftId))
        {
            var sourceDraft = Project.Drafts.FirstOrDefault(entry => entry.Id == sourceDraftId)?.Draft;
            if (sourceDraft is not null)
            {
                ActiveDraft = _draftService.Duplicate(sourceDraft);
                var createdEntry = Project.AddDraft(ActiveDraft);
                _activeDraftId = createdEntry.Id;
                AddDraftToRoster(createdEntry, select: true);
                SetTechniqueVariantChoicesFromDraft(ActiveDraft);
                ActiveWorkspace = WorkspaceKind.Characters;
                WizardStep = WizardStep.Character;
                StatusMessage = "Character draft created from the complete custom draft.";
                QueueRecoverySave();
                return;
            }
        }

        var sourceVariants = SelectedCharacter.Variants?.ToArray() ?? [];
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(SelectedCharacter.PortraitResourcePath))
            fields["Assets.PortraitContainer"] = SelectedCharacter.PortraitResourcePath;
        if (SelectedCharacter.Portraits is { } portraits)
        {
            if (portraits.TryGetValue(CharacterAssetPlatform.Pc, out var pcPortrait))
                fields["Assets.Pc.PortraitContainer"] = pcPortrait.ResourcePath;
            if (portraits.TryGetValue(CharacterAssetPlatform.Switch, out var switchPortrait))
                fields["Assets.Switch.PortraitContainer"] = switchPortrait.ResourcePath;
        }
        if (SelectedCharacter.PortraitMetadata is { } portrait)
        {
            fields["Assets.StandardPortraitEntry"] = portrait.StandardPortraitEntryName;
            fields["Assets.UniformPortraitEntry"] = portrait.UniformPortraitEntryName;
            fields["Assets.PortraitPayloadFormat"] = portrait.PayloadFormat;
        }
        if (SelectedCharacter.BaseMetadata is { } characterBase)
        {
            fields["Identity.BaseId"] = Invariant(characterBase.BaseId);
            fields["Identity.InternalName"] = characterBase.InternalName;
            fields["Identity.Gender"] = Invariant(characterBase.Gender);
            fields["Identity.BodyType"] = Invariant(characterBase.BodyType);
            fields["Identity.AcademicYear"] = Invariant(characterBase.AcademicYear);
            fields["Identity.SourceSeries"] = Invariant(characterBase.SourceSeries);
            fields["Identity.SeriesName"] = SelectedCharacter.Series;
            fields["Identity.GenderName"] = CharacterGenderCatalog.ResolveName(
                characterBase.Gender, SelectedLanguageCode);
            if (characterBase.OriginGameAssociationIndex is { } originGameIndex)
            {
                fields["Identity.OriginGameIndex"] = Invariant(originGameIndex);
                fields["Identity.OriginGameName"] = CharacterOriginGameCatalog.ResolveName(
                    originGameIndex, SelectedLanguageCode);
            }
            fields["Identity.AcademicYearName"] = PlayerCardAcademicYear;
            fields["Advanced.FullNameTextId"] = Invariant(characterBase.FullNameTextId);
            fields["Advanced.ShortNameTextId"] = Invariant(characterBase.ShortNameTextId);
            fields["Advanced.UpperNameTextId"] = Invariant(characterBase.UpperNameTextId);
            fields["Advanced.DescriptionTextId"] = Invariant(characterBase.DescriptionTextId);
            fields["Models.HeadModelPath"] = characterBase.HeadModelPath;
            fields["Models.BodyModelPath"] = characterBase.BodyModelPath;
            fields["Models.SkinColorRgba"] = $"{characterBase.SkinColorRgba:X8}";
            fields["Advanced.SourceSkinColorRgba"] = $"{characterBase.SkinColorRgba:X8}";
            fields["Advanced.ModelId"] = Invariant(characterBase.ModelId);
            fields["Advanced.BodyModelId"] = Invariant(characterBase.BodyModelId);
            fields["Advanced.BodyGroup"] = Invariant(characterBase.BodyGroup);
            fields["Advanced.BodyPoseType"] = Invariant(characterBase.BodyPoseType);
            fields["Advanced.PhysicalBodyModelKey"] = characterBase.PhysicalBodyModelKey;
            fields["Advanced.UniformPortraitVariant"] = Invariant(characterBase.UniformPortraitVariant);
            fields["Models.UniformModel"] = Invariant(characterBase.UniformModel);
            fields["Models.ShoesModel"] = Invariant(characterBase.ShoesModel);
            fields["Models.GloveModel"] = Invariant(characterBase.GloveModel);
            fields["Models.EquipmentColor"] = Invariant(characterBase.EquipmentColor);
            fields["Models.UniformCollarOpen"] = Invariant(characterBase.UniformCollarOpen);
            fields["Models.EquipmentFlag2"] = Invariant(characterBase.EquipmentFlag2);
            fields["Models.ChestSize"] = Invariant(characterBase.ChestSize);
            fields["Models.ForceKit"] = Invariant(characterBase.ForceKit);
            fields["Identity.TeamAssociation1"] = Invariant(characterBase.TeamAssociation1);
            fields["Identity.TeamAssociation2"] = Invariant(characterBase.TeamAssociation2);
            fields["Identity.TeamAssociation3"] = Invariant(characterBase.TeamAssociation3);
        }
        if (SelectedCharacter.Localizations is { } localizations)
        {
            foreach (var (locale, localization) in localizations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                fields[$"Localization.{locale}.FullName"] = localization.FullName;
                fields[$"Localization.{locale}.FamilyName"] = localization.FamilyName;
                fields[$"Localization.{locale}.GivenName"] = localization.GivenName;
                fields[$"Localization.{locale}.ShortName"] = localization.ShortName;
                fields[$"Localization.{locale}.UpperName"] = localization.UpperName;
                fields[$"Localization.{locale}.Description"] = localization.Description;
            }
        }
        if (SelectedVariant is { } variant)
        {
            fields["Gameplay.ParameterId"] = Invariant(variant.ParameterId);
            fields["Gameplay.BaseId"] = Invariant(variant.BaseId);
            fields["Gameplay.Affinity"] = variant.Affinity.ToString();
            fields["Gameplay.MainPosition"] = Invariant(variant.MainPosition);
            fields["Gameplay.SubPosition"] = Invariant(variant.SubPosition);
            fields["Gameplay.PlayStyle"] = Invariant(variant.PlayStyle);
            fields["Gameplay.Growth"] = Invariant(variant.Growth);
            fields["Gameplay.Rank"] = Invariant(variant.Rank);
            fields["Gameplay.AbilityBoardId"] = Invariant(variant.AbilityBoardId);
            fields["Gameplay.SpecialRarity"] = Invariant(variant.SpecialRarity);
            for (var index = 0; index < variant.SkillSlots.Count; index++)
            {
                var skill = variant.SkillSlots[index];
                fields[$"Skills.Slot{index + 1}"] = $"{Invariant(skill.SkillId)}:{Invariant(skill.UnlockLevel)}";
            }
        }
        var source = new CharacterSnapshot(
            SelectedCharacter.Id,
            SelectedCharacter.DisplayName,
            SelectedCharacter.Confidence,
            fields,
            sourceVariants);
        ActiveDraft = _draftService.Duplicate(source);
        var entry = Project.AddDraft(ActiveDraft);
        _activeDraftId = entry.Id;
        AddDraftToRoster(entry, select: true);
        // The custom roster row intentionally has no dump variants. Keep the source
        // variant choices available while the wizard edits this cloned character.
        if (sourceVariants.Length > 0)
            SetTechniqueVariantChoices(sourceVariants, SelectedVariant);
        ActiveWorkspace = WorkspaceKind.Characters;
        WizardStep = WizardStep.Character;
        StatusMessage = "Character draft created from the immutable dump snapshot.";
    }

    public void CreateBlankDraft()
    {
        ActiveDraft = _draftService.CreateBlank();
        var entry = Project.AddDraft(ActiveDraft);
        _activeDraftId = entry.Id;
        AddDraftToRoster(entry, select: true);
        ActiveWorkspace = WorkspaceKind.Characters;
        WizardStep = WizardStep.Character;
        StatusMessage = "A neutral custom character draft was created.";
        QueueRecoverySave();
    }

    public void RequestRemoveActiveDraft()
    {
        if (ActiveDraft is not null && _activeDraftId is not null)
            IsRemoveDraftConfirmationVisible = true;
    }

    public void RequestDeleteSelectedCharacter()
    {
        if (!CanDeleteSelectedCharacter)
        {
            StatusMessage = "Original Victory Road characters cannot be deleted.";
            return;
        }
        RequestRemoveActiveDraft();
    }

    public void DuplicateSelectedCharacter()
    {
        if (SelectedCharacter is null) return;
        if (SelectedCharacter.Origin == CharacterOrigin.Original)
        {
            CloneSelectedCharacter();
            return;
        }
        if (!_draftIdsByRosterId.TryGetValue(SelectedCharacter.Id, out var draftId)) return;
        var source = Project.Drafts.First(entry => entry.Id == draftId).Draft;
        var duplicate = _draftService.Duplicate(source);
        var entry = Project.AddDraft(duplicate);
        ActiveDraft = duplicate;
        _activeDraftId = entry.Id;
        AddDraftToRoster(entry, select: true);
        StatusMessage = "Custom character duplicated in the project.";
        QueueRecoverySave();
    }

    public void RemoveActiveDraft()
    {
        if (ActiveDraft is null || _activeDraftId is null) return;
        var removedId = _activeDraftId.Value;
        _draftService.RemoveFromProject(Project, removedId);
        var rosterId = _draftIdsByRosterId.FirstOrDefault(pair => pair.Value == removedId).Key;
        if (rosterId is not null)
        {
            _draftIdsByRosterId.Remove(rosterId);
            var rosterItem = Characters.FirstOrDefault(character => character.Id == rosterId);
            if (rosterItem is not null) Characters.Remove(rosterItem);
        }
        SelectedCharacter = null;
        _activeDraftId = null;
        ActiveDraft = null;
        ApplySearchNow();
        IsRemoveDraftConfirmationVisible = false;
        StatusMessage = "Custom character draft unlinked from the project.";
        QueueRecoverySave();
    }

    public void CancelRemoveActiveDraft() => IsRemoveDraftConfirmationVisible = false;

    private void SelectProjectDraft(CharacterCatalogItem? character)
    {
        if (character is not null
            && _draftIdsByRosterId.TryGetValue(character.Id, out var draftId))
        {
            var entry = Project.Drafts.FirstOrDefault(item => item.Id == draftId);
            if (entry is not null)
            {
                _activeDraftId = draftId;
                ActiveDraft = entry.Draft;
                return;
            }
        }
        _activeDraftId = null;
        ActiveDraft = null;
    }

    private void AddDraftToRoster(ProjectDraftEntry entry, bool select)
    {
        var rosterId = $"custom:{entry.Id:N}";
        _draftIdsByRosterId[rosterId] = entry.Id;
        var portraitPath = ResolveDraftPortraitPath(entry.Draft);
        var affinity = Enum.TryParse<CharacterAffinity>(entry.Draft.Gameplay?.Affinity, out var parsedAffinity)
            ? parsedAffinity
            : CharacterAffinity.Neutral;
        var item = new CharacterCatalogItem(
            rosterId,
            entry.Draft.DisplayName,
            CharacterDataConfidence.Confirmed,
            portraitPath,
            affinity,
            Origin: entry.Draft.Origin,
            Variants: DraftVariantSummaries(entry.Draft),
            StandardPortraitResourcePath: ResolveDraftStandardPortraitPath(entry.Draft),
            UniformPortraitResourcePath: ResolveDraftUniformPortraitPath(entry.Draft));
        Characters.Add(item);
        ApplySearchNow();
        NotifyFilterOptionsChanged();
        if (select) SelectedCharacter = item;
    }

    private void UpdateDraftRosterItem(Guid draftId, CharacterDraft draft)
    {
        var rosterId = $"custom:{draftId:N}";
        var row = RosterRows.FirstOrDefault(candidate => candidate.Character.Id == rosterId);
        if (row is null) return;

        // Keep the row object (and therefore the ListBox selection) stable while replacing the
        // immutable catalog value shown by the editor. This lets display-name edits propagate
        // immediately without causing Avalonia to rebuild selection state mid-keystroke.
        var character = row.Character with
        {
            DisplayName = draft.DisplayName,
            PortraitResourcePath = ResolveDraftPortraitPath(draft) ?? row.Character.PortraitResourcePath,
            Origin = draft.Origin,
            Variants = DraftVariantSummaries(draft),
            StandardPortraitResourcePath = ResolveDraftStandardPortraitPath(draft),
            UniformPortraitResourcePath = ResolveDraftUniformPortraitPath(draft),
        };
        row.Character = character;
        if (string.Equals(_selectedCharacter?.Id, rosterId, StringComparison.Ordinal))
        {
            _selectedCharacter = character;
            OnPropertyChanged(nameof(SelectedCharacter));
            OnPropertyChanged(nameof(SelectedPortraitSummary));
            NotifyPlayerCardChanged();
        }
    }

    public async Task SaveDraftAsync()
    {
        if (ActiveDraft is null)
        {
            StatusMessage = "Clone a character before saving a package.";
            return;
        }

        if (_packageService is null)
        {
            StatusMessage = "The .vrchara package service is not configured.";
            return;
        }

        try
        {
            if (!await ApplyEditedLocalesAsync())
                return;

            if (string.IsNullOrWhiteSpace(ActiveDraft.Acquisition?.Method))
            {
                ActiveDraft = ActiveDraft with
                {
                    Acquisition = (ActiveDraft.Acquisition ?? new CharacterDraftAcquisition(null, null)) with
                    {
                        Method = "Delivery",
                    },
                };
                PersistActiveDraft();
            }
            var targetPath = _modificationSourcePath ?? DraftPackagePath;
            await _packageService.SaveAsync(targetPath, ActiveDraft, CancellationToken.None);
            _lastSavedPackagePath = targetPath;
            IsPostSavePromptVisible = true;
            StatusMessage = "Character package saved.";
            QueueRecoverySave();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
    }

    public async Task OpenLocalesJsonAsync()
    {
        if (ActiveDraft is null)
        {
            StatusMessage = SelectedLanguageCode == "es"
                ? "Selecciona o crea un personaje antes de editar los locales."
                : "Select or create a character before editing locales.";
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_localesJsonPath) || !File.Exists(_localesJsonPath))
            {
                var directory = Path.Combine(Path.GetTempPath(), "VictoryTool", "locales");
                Directory.CreateDirectory(directory);
                _localesJsonPath = Path.Combine(directory, $"{SanitizeFileName(ActiveDraft.DisplayName)}-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(
                    _localesJsonPath,
                    CharacterLocalizationJson.Serialize(ActiveDraft.Localization),
                    CancellationToken.None);
                OnPropertyChanged(nameof(LocalesJsonPath));
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _localesJsonPath,
                UseShellExecute = true,
            });
            StatusMessage = SelectedLanguageCode == "es"
                ? "Edita el JSON de locales y pulsa Guardar cuando termines."
                : "Edit the locales JSON and press Save when you are finished.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = exception.Message;
        }
    }

    private async Task<bool> ApplyEditedLocalesAsync()
    {
        if (string.IsNullOrWhiteSpace(_localesJsonPath)) return true;
        if (!File.Exists(_localesJsonPath))
        {
            StatusMessage = SelectedLanguageCode == "es"
                ? "No se encuentra el JSON de locales temporal; vuelve a abrirlo antes de guardar."
                : "The temporary locales JSON cannot be found; open it again before saving.";
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_localesJsonPath, CancellationToken.None);
            var localization = CharacterLocalizationJson.Deserialize(json);
            ActiveDraft = ActiveDraft! with { Localization = localization };
            if (_activeDraftId is { } draftId)
            {
                Project.UpdateDraft(draftId, ActiveDraft);
                UpdateDraftRosterItem(draftId, ActiveDraft);
            }
            RebuildLocalizationEditorRows();
            NotifyReviewChanged();
            QueueRecoverySave();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusMessage = SelectedLanguageCode == "es"
                ? $"El JSON de locales no es válido: {exception.Message}"
                : $"The locales JSON is invalid: {exception.Message}";
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "character" : sanitized;
    }

    private void SetDefaultShopAcquisition(Guid entryId)
    {
        Project.SetAcquisition(
            entryId,
            new BatchAcquisitionConfiguration(
                ShopSourceItemId: -306585094,
                ShopRarity: 6,
                ShopSpecialVariant: 0,
                IsFree: true,
                ShopSourceParameterId: 773487400));
        InvalidateExportPlan();
    }

    public void PreviewExport()
    {
        PreviewExportAsync().GetAwaiter().GetResult();
    }

    public async Task PreviewExportAsync()
    {
        try
        {
            if (_exportOutputPathGenerated
                && (Directory.Exists(ExportOutputPath) || File.Exists(ExportOutputPath)))
                SetGeneratedExportOutputPath(GetNextExportPath(ExportOutputPath));
            if (AcquisitionMode is AcquisitionMode.Shop or AcquisitionMode.Both)
                EnsureShopAcquisitionDefaults();
            CurrentExportPlan = _playerCardAssetProfile is { } profile
                ? await _exportPlanner.CreatePlanAsync(
                    Project,
                    profile,
                    ExportPlatform,
                    AcquisitionMode,
                    ExportOutputPath,
                    CancellationToken.None)
                : _exportPlanner.CreatePlan(
                    Project,
                    ExportPlatform,
                    AcquisitionMode,
                    ExportOutputPath);
            Diagnostics.ReplaceWith(CurrentExportPlan.Diagnostics);
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(DiagnosticReport));
            StatusMessage = CurrentExportPlan.CanExport
                ? "Export plan is ready."
                : BuildDiagnosticStatus(CurrentExportPlan.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private static string BuildDiagnosticStatus(IReadOnlyList<Diagnostic> diagnostics)
    {
        var blocking = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (blocking.Length == 0) return "Export plan contains warnings. Review diagnostics before exporting.";
        var first = blocking[0];
        var suffix = string.IsNullOrWhiteSpace(first.RecoveryAction)
            ? string.Empty
            : $" Acción: {first.RecoveryAction}";
        return $"{blocking.Length} error(es) de exportación. {first.Code}: {first.Message}{suffix}";
    }

    private void EnsureShopAcquisitionDefaults()
    {
        var entriesWithoutAcquisition = Project.Batch
            .Where(entry => entry.Acquisition is null)
            .ToArray();
        foreach (var entry in entriesWithoutAcquisition)
            SetDefaultShopAcquisition(entry.Id);
    }

    public async Task ExecuteExportAsync()
    {
        if (CurrentExportPlan?.CanExport != true)
        {
            StatusMessage = "Preview and resolve every blocking diagnostic before exporting.";
            return;
        }

        try
        {
            var result = await _exportExecutor.ExecuteAsync(CurrentExportPlan, CancellationToken.None);
            StatusMessage = $"Export published to {result.OutputPath}.";
            SetGeneratedExportOutputPath(GetNextExportPath(result.OutputPath));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
    }

    private static string GetNextExportPath(string previousPath)
    {
        var parent = Path.GetDirectoryName(previousPath) ?? Path.GetTempPath();
        var stem = Path.GetFileName(previousPath);
        var candidate = Path.Combine(parent, $"{stem} (2)");
        for (var suffix = 3; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(parent, $"{stem} ({suffix})");
        return candidate;
    }

    public void ToggleLanguage()
    {
        SelectedLanguageCode = string.Equals(
            SelectedLanguageCode, "en", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
    }

    public void UpdateLayoutWidth(double width)
    {
        LayoutDensity = width >= 1440
            ? LayoutDensity.Wide
            : width >= 1100
                ? LayoutDensity.Regular
                : LayoutDensity.Compact;
    }

    public void DuplicateSelectedBatchEntry()
    {
        if (SelectedBatchEntry is null) return;
        SelectedBatchEntry = Project.Duplicate(SelectedBatchEntry.Id);
        InvalidateExportPlan();
        RefreshBatch();
        StatusMessage = "Character package duplicated in the batch.";
        QueueRecoverySave();
    }

    public void ToggleSelectedBatchEntry()
    {
        if (SelectedBatchEntry is null) return;
        var id = SelectedBatchEntry.Id;
        Project.SetEnabled(id, !SelectedBatchEntry.IsEnabled);
        InvalidateExportPlan();
        RefreshBatch(id);
        StatusMessage = "Character package state updated.";
        QueueRecoverySave();
    }

    public void RemoveSelectedBatchEntry()
    {
        if (SelectedBatchEntry is null) return;
        RemoveBatchEntry(SelectedBatchEntry);
    }

    public void RemoveBatchEntry(BatchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Project.Batch.Any(candidate => candidate.Id == entry.Id)) return;
        Project.Remove(entry.Id);
        InvalidateExportPlan();
        if (SelectedBatchEntry?.Id == entry.Id)
            SelectedBatchEntry = null;
        RefreshBatch();
        StatusMessage = "Character package removed from the batch.";
        QueueRecoverySave();
    }

    public void MoveSelectedBatchEntry(int offset)
    {
        if (SelectedBatchEntry is null) return;
        var id = SelectedBatchEntry.Id;
        var currentIndex = Project.Batch.ToList().FindIndex(entry => entry.Id == id);
        if (currentIndex < 0) return;
        Project.Move(id, currentIndex + offset);
        InvalidateExportPlan();
        RefreshBatch(id);
        QueueRecoverySave();
    }

    private void ScheduleSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = ApplySearchAsync(_searchCancellation.Token);
    }

    private async Task ApplySearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            var source = Characters.ToArray();
            var query = CreateCatalogQuery();
            var filtered = await Task.Run(() => query.Apply(source), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                FilteredCharacters.ReplaceWith(filtered);
                UpdateRosterRows(filtered);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplySearchNow()
    {
        var query = CreateCatalogQuery();
        var filtered = query.Apply(Characters);
        FilteredCharacters.ReplaceWith(filtered);
        UpdateRosterRows(filtered);
    }

    private void UpdateRosterRows(IReadOnlyList<CharacterCatalogItem> characters)
    {
        var selectedCharacterId = SelectedCharacter?.Id;
        _rosterPortraitCancellation?.Cancel();
        _rosterPortraitCancellation?.Dispose();
        _rosterPortraitCancellation = null;

        CharacterRosterRow[] rows;
        CharacterRosterRow? selectedRow;
        _isUpdatingRosterRows = true;
        try
        {
            foreach (var row in RosterRows)
                row.Dispose();
            rows = characters.Select(character => new CharacterRosterRow(character)).ToArray();
            RosterRows.ReplaceWith(rows);

            selectedRow = selectedCharacterId is null
                ? null
                : rows.FirstOrDefault(row => row.Character.Id == selectedCharacterId);
            if (!ReferenceEquals(_selectedRosterRow, selectedRow))
            {
                _selectedRosterRow = selectedRow;
                OnPropertyChanged(nameof(SelectedRosterRow));
            }
        }
        finally
        {
            _isUpdatingRosterRows = false;
        }

        // A filter should preserve the active character when it still matches. Only clear
        // it when the character is genuinely outside the filtered result.
        if (selectedCharacterId is not null && selectedRow is null)
            SelectedCharacter = null;

        ScheduleRosterPortraitLoads();
    }

    private void ScheduleRosterPortraitLoads()
    {
        _rosterPortraitCancellation?.Cancel();
        _rosterPortraitCancellation?.Dispose();
        _rosterPortraitCancellation = null;
        if (_portraitLoader is null || RosterRows.Count == 0) return;

        var rows = RosterRows.Where(row => !row.HasUniformPortrait).ToArray();
        if (rows.Length == 0) return;
        foreach (var row in rows) row.IsPortraitLoading = true;
        var cancellation = new CancellationTokenSource();
        _rosterPortraitCancellation = cancellation;
        _ = LoadRosterPortraitsAsync(rows, cancellation);
    }

    private async Task LoadRosterPortraitsAsync(
        IReadOnlyList<CharacterRosterRow> rows,
        CancellationTokenSource cancellation)
    {
        var nextIndex = -1;
        var workers = Enumerable.Range(0, Math.Min(RosterPortraitConcurrency, rows.Count))
            .Select(_ => LoadRosterPortraitWorkerAsync(rows, cancellation, () => Interlocked.Increment(ref nextIndex)))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task LoadRosterPortraitWorkerAsync(
        IReadOnlyList<CharacterRosterRow> rows,
        CancellationTokenSource cancellation,
        Func<int> nextIndex)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var index = nextIndex();
            if ((uint)index >= (uint)rows.Count) return;
            await LoadRosterPortraitAsync(rows[index], cancellation.Token);
        }
    }

    private async Task LoadRosterPortraitAsync(CharacterRosterRow row, CancellationToken cancellationToken)
    {
        CharacterPortraitLoadResult? result = null;
        try
        {
            result = await _portraitLoader!.LoadAsync(
                new CharacterPortraitRequest(row.Character, CharacterPortraitKind.RosterThumbnail),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || !RosterRows.Contains(row))
                return;

            row.UniformPortrait = result.Bitmap;
            result = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A missing or malformed portrait must not stop the rest of the roster.
        }
        finally
        {
            row.IsPortraitLoading = false;
            result?.Dispose();
        }
    }

    private void ClearRosterPortraits()
    {
        _rosterPortraitCancellation?.Cancel();
        _rosterPortraitCancellation?.Dispose();
        _rosterPortraitCancellation = null;
        foreach (var row in RosterRows)
            row.Dispose();
        RosterRows.Clear();
    }

    private CharacterCatalogQuery CreateCatalogQuery() => new(
        SearchText,
        new CharacterFilterSet(
            Affinities: SelectedAffinity is { } affinity ? [affinity] : null,
            Origins: SelectedOrigin is { } origin ? [origin] : null,
            Series: SelectedSeries is { } series ? [series] : null,
            AcademicYears: SelectedAcademicYear is { } academicYear ? [academicYear] : null,
            Genders: SelectedGender is { } gender ? [gender] : null,
            BodyTypes: SelectedBodyType is { } bodyType ? [bodyType] : null,
            Positions: SelectedPosition is { } position ? [position] : null,
            PlayStyles: SelectedPlayStyle is { } playStyle ? [playStyle] : null,
            Ranks: SelectedRank is { } rank ? [rank] : null,
            SpecialRarities: SelectedSpecialRarity is { } rarity ? [rarity] : null),
        SelectedCharacterSort);

    private IReadOnlyList<int> DistinctBaseValues(Func<CharacterBaseMetadata, int> selector) => Characters
        .Where(character => character.BaseMetadata is not null)
        .Select(character => selector(character.BaseMetadata!))
        .Distinct()
        .Order()
        .ToArray();

    private IReadOnlyList<int> DistinctVariantValues(Func<CharacterVariantSummary, int> selector) => Characters
        .SelectMany(character => character.Variants ?? [])
        .Select(selector)
        .Distinct()
        .Order()
        .ToArray();

    private void NotifyFilterOptionsChanged()
    {
        OnPropertyChanged(nameof(SeriesOptions));
        OnPropertyChanged(nameof(AcademicYearOptions));
        OnPropertyChanged(nameof(GenderOptions));
        OnPropertyChanged(nameof(IdentitySeriesChoices));
        OnPropertyChanged(nameof(IdentityOriginGameChoices));
        OnPropertyChanged(nameof(IdentityGenderChoices));
        OnPropertyChanged(nameof(IdentityAcademicYearChoices));
        OnPropertyChanged(nameof(BodyTypeOptions));
        OnPropertyChanged(nameof(BodyTypeChoices));
        OnPropertyChanged(nameof(PositionOptions));
        OnPropertyChanged(nameof(PlayStyleOptions));
        OnPropertyChanged(nameof(RankOptions));
        OnPropertyChanged(nameof(SpecialRarityOptions));
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void RefreshBatch(Guid? selectedId = null)
    {
        Batch.ReplaceWith(Project.Batch);
        OnPropertyChanged(nameof(HasBatchEntries));
        if (selectedId is not null)
            SelectedBatchEntry = Batch.FirstOrDefault(entry => entry.Id == selectedId);
    }

    private void UpdateSelectedBatchAcquisition(
        Func<BatchAcquisitionConfiguration, BatchAcquisitionConfiguration> update)
    {
        if (SelectedBatchEntry is null) return;
        var current = SelectedBatchEntry.Acquisition ?? new BatchAcquisitionConfiguration();
        var updated = update(current);
        var id = SelectedBatchEntry.Id;
        Project.SetAcquisition(id, updated);
        InvalidateExportPlan();
        RefreshBatch(id);
        QueueRecoverySave();
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsStore is null) return;
        try
        {
            await _settingsStore.SaveAsync(
                new ApplicationSettings(
                    IsWorkspaceReady ? GameDumpInput : null,
                    SelectedLanguageCode,
                    RosterPaneWidth,
                    PreviewPaneWidth),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Application settings could not be saved: {exception.Message}";
        }
    }

    private static double NormalizePaneWidth(double value, double fallback) =>
        double.IsFinite(value) ? Math.Max(280, value) : fallback;

    private void QueueRecoverySave()
    {
        if (_projectStore is null || string.IsNullOrWhiteSpace(_recoveryRoot)) return;
        _ = SaveRecoveryAsync();
    }

    private async Task SaveRecoveryAsync()
    {
        try
        {
            var basePath = Path.Combine(_recoveryRoot!, $"{Project.Id:N}.vrproject");
            await _projectStore!.SaveRecoveryAsync(basePath, Project, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Project recovery could not be saved: {exception.Message}";
        }
    }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }
}
