using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VictoryTool.Desktop.ViewModels;

namespace VictoryTool.Desktop.Views;

public sealed partial class CharactersWorkspaceView : UserControl
{
    public CharactersWorkspaceView() => AvaloniaXamlLoader.Load(this);

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
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Level-5 character models")
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
            Title = "Save one-character package",
            DefaultExtension = "vrchara",
            SuggestedFileName = "character.vrchara",
            FileTypeChoices =
            [
                new FilePickerFileType("VictoryTool character package")
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
