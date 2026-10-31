using System.Globalization;
using System.Windows.Input;
using VictoryTool.Application.Characters;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _rosterSortDescending;
    private bool _refreshingRosterFilters;
    private int? _selectedRosterOriginGame;
    private string _teamFilterSearch = string.Empty;
    private readonly HashSet<int> _selectedRosterTeams = [];
    private ICommand? _toggleRosterTeamCommand;
    private ICommand? _clearRosterFiltersCommand;

    public string BaseCharacterHeading => Workflow("Choose a base character");
    public string RosterSortLabel => Workflow("Sort by");
    public string RosterDirectionLabel => Workflow("Sort direction");
    public string RosterFiltersLabel => Workflow("Filters");
    public string RosterOriginGameLabel => Workflow("Origin game");
    public string RosterGenderLabel => Workflow("Gender");
    public string RosterTeamsLabel => Workflow("Teams");
    public string RosterTeamSearchLabel => Workflow("Search teams");
    public string RosterResetFiltersLabel => Workflow("Reset filters");
    public IReadOnlyList<IdentityChoice> RosterDirectionChoices =>
        [new("ascending", Workflow("Ascending")), new("descending", Workflow("Descending"))];
    public IdentityChoice? SelectedRosterDirectionChoice
    {
        get => RosterDirectionChoices[_rosterSortDescending ? 1 : 0];
        set
        {
            if (value is not null && SetProperty(ref _rosterSortDescending, value.Value == "descending"))
            {
                OnPropertyChanged(nameof(SelectedRosterDirectionChoice));
                ScheduleSearch();
            }
        }
    }
    public IReadOnlyList<IdentityChoice> RosterOriginGameChoices => WithAll(IdentityOriginGameChoices);
    public IReadOnlyList<IdentityChoice> RosterGenderChoices => WithAll(IdentityGenderChoices);
    public IReadOnlyList<IdentityChoice> RosterPositionChoices => WithAll(PositionOptions.Select(position =>
        new IdentityChoice(position, Workflow(position))));
    public IReadOnlyList<IdentityChoice> RosterAffinityChoices => WithAll(CharacterAffinityFilterChoices);
    private IReadOnlyList<IdentityChoice> WithAll(IEnumerable<IdentityChoice> choices) =>
        new[] { new IdentityChoice(string.Empty, Workflow("All")) }.Concat(choices).ToArray();
    public IdentityChoice? SelectedRosterOriginGameChoice
    {
        get => RosterOriginGameChoices.FirstOrDefault(choice => choice.Value == (_selectedRosterOriginGame?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        set { if (!_refreshingRosterFilters && value is not null && SetProperty(ref _selectedRosterOriginGame, ParseNullableInt(value.Value))) ScheduleSearch(); }
    }
    public IdentityChoice? SelectedRosterGenderChoice
    {
        get => RosterGenderChoices.FirstOrDefault(choice => choice.Value == (SelectedGender?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        set { if (!_refreshingRosterFilters && value is not null) SelectedGender = ParseNullableInt(value.Value); }
    }
    public IdentityChoice? SelectedRosterPositionChoice
    {
        get => RosterPositionChoices.FirstOrDefault(choice => choice.Value == (SelectedPosition ?? string.Empty));
        set { if (!_refreshingRosterFilters && value is not null) SelectedPosition = string.IsNullOrEmpty(value.Value) ? null : value.Value; }
    }
    public IdentityChoice? SelectedRosterAffinityChoice
    {
        get => RosterAffinityChoices.FirstOrDefault(choice => choice.Value == (SelectedAffinity?.ToString() ?? string.Empty));
        set { if (!_refreshingRosterFilters && value is not null) SelectedAffinity = Enum.TryParse<CharacterAffinity>(value.Value, out var affinity) ? affinity : null; }
    }
    public string TeamFilterSearch
    {
        get => _teamFilterSearch;
        set { if (SetProperty(ref _teamFilterSearch, value)) OnPropertyChanged(nameof(RosterTeamChoices)); }
    }
    public IReadOnlyList<LocalizedTeam> RosterTeamChoices => TeamAssociationOptions
        .Where(team => team.Id != 0 && !_selectedRosterTeams.Contains(team.Id)
            && team.Name.Contains(TeamFilterSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
    public IReadOnlyList<LocalizedTeam> SelectedRosterTeams => TeamAssociationOptions
        .Where(team => _selectedRosterTeams.Contains(team.Id)).ToArray();
    public ICommand ToggleRosterTeamCommand => _toggleRosterTeamCommand ??= new DelegateCommand(parameter =>
    {
        if (parameter is not LocalizedTeam team) return;
        var added = !_selectedRosterTeams.Remove(team.Id);
        if (added) _selectedRosterTeams.Add(team.Id);
        if (added && RosterTeamChoices.Count == 0) TeamFilterSearch = string.Empty;
        OnPropertyChanged(nameof(SelectedRosterTeams));
        OnPropertyChanged(nameof(RosterTeamChoices));
        ScheduleSearch();
    });
    public ICommand ClearRosterFiltersCommand => _clearRosterFiltersCommand ??= new DelegateCommand(ClearCharacterFilters);

    private void NotifyRosterFilterChoicesChanged()
    {
        _refreshingRosterFilters = true;
        try
        {
            foreach (var property in new[] { nameof(RosterOriginGameChoices), nameof(RosterGenderChoices),
                nameof(RosterPositionChoices), nameof(RosterAffinityChoices), nameof(SelectedRosterTeams),
                nameof(RosterTeamChoices), nameof(BaseCharacterHeading), nameof(RosterSortLabel),
                nameof(RosterDirectionLabel), nameof(RosterFiltersLabel), nameof(RosterOriginGameLabel),
                nameof(RosterGenderLabel), nameof(RosterTeamsLabel), nameof(RosterTeamSearchLabel),
                nameof(RosterResetFiltersLabel), nameof(RosterDirectionChoices), nameof(SelectedRosterDirectionChoice),
                nameof(SelectedRosterOriginGameChoice), nameof(SelectedRosterGenderChoice),
                nameof(SelectedRosterPositionChoice), nameof(SelectedRosterAffinityChoice) }) OnPropertyChanged(property);
        }
        finally { _refreshingRosterFilters = false; }
    }
}
