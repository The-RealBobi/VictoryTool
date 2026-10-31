using VictoryTool.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VictoryTool.Desktop.ViewModels;

namespace VictoryTool.Desktop.Views;

public sealed partial class CharactersWorkspaceView : UserControl
{
    private string Ui(string english) => WorkflowText.ForLocale(
        (DataContext as VictoryTool.Desktop.ViewModels.MainWindowViewModel)?.SelectedLanguageCode ?? "en", english);

    public CharactersWorkspaceView() => AvaloniaXamlLoader.Load(this);

    private void SelectRosterTeam(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is ListBox { SelectedItem: { } team } list && DataContext is MainWindowViewModel viewModel)
        {
            list.SelectedItem = null;
            viewModel.ToggleRosterTeamCommand.Execute(team);
        }
    }

    private void ToggleRosterTeam(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: { } team } && DataContext is MainWindowViewModel viewModel)
            viewModel.ToggleRosterTeamCommand.Execute(team);
    }

    private async void SelectHeadModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (await SelectModelAsync("Select a head model") is { } path
            && DataContext is MainWindowViewModel viewModel)
            viewModel.ActiveDraftHeadModelPath = path;
    }

    private async void SelectBodyModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (await SelectModelAsync("Select a body model") is { } path
            && DataContext is MainWindowViewModel viewModel)
            viewModel.ActiveDraftBodyModelPath = path;
    }

    private async Task<string?> SelectModelAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Ui(title),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Ui("Level-5 character models"))
                {
                    Patterns = ["*.g4md", "*.g4pkm"],
                },
            ],
        });
        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }

    private async void SavePackage(object? sender, RoutedEventArgs eventArgs)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Ui("Save one-character package"),
            DefaultExtension = "vrchara",
            SuggestedFileName = "character.vrchara",
            FileTypeChoices =
            [
                new FilePickerFileType(Ui("VictoryTool character package"))
                {
                    Patterns = ["*.vrchara"],
                },
            ],
        });
        if (file is not null && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DraftPackagePath = file.Path.LocalPath;
            await viewModel.SaveDraftAsync();
        }
    }
}
