using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VictoryTool.Application.Projects;

namespace VictoryTool.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private DispatcherTimer? _screenColorPickTimer;
    private bool _awaitingColorPickRelease;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, args) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                viewModel.UpdateLayoutWidth(args.NewSize.Width);
            }
        };
    }

    private async void SelectDumpFolder(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select extracted Victory Road data",
            AllowMultiple = false,
        });

        if (folders.Count == 1 && DataContext is ViewModels.MainWindowViewModel viewModel)
            viewModel.GameDumpInput = folders[0].Path.LocalPath;
    }

    private async void SelectExportParentFolder(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the parent folder for a new VictoryTool export",
            AllowMultiple = false,
        });

        if (folders.Count != 1 || DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        viewModel.SetGeneratedExportOutputPath(GetAvailableExportPath(folders[0].Path.LocalPath));
    }

    private async void SelectBatchPackage(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a VictoryTool character package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("VictoryTool character package")
                {
                    Patterns = ["*.vrchara"],
                },
            ],
        });
        if (files.Count != 1 || DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        viewModel.PackageInput = files[0].Path.LocalPath;
        viewModel.AddPackage();
    }

    private void RemoveBatchPackage(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel
            || sender is not Button button
            || button.DataContext is not BatchEntry entry)
            return;
        viewModel.RemoveBatchEntry(entry);
    }

    private async void SelectModificationPackage(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a VictoryTool character package to modify",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("VictoryTool character package") { Patterns = ["*.vrchara"] }],
        });
        if (files.Count == 1 && DataContext is ViewModels.MainWindowViewModel viewModel)
            await viewModel.LoadModificationAsync(files[0].Path.LocalPath);
    }

    private async void ExportBatch(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        if (string.IsNullOrWhiteSpace(viewModel.ExportOutputPath))
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the export destination",
                AllowMultiple = false,
            });
            if (folders.Count != 1) return;
            viewModel.SetGeneratedExportOutputPath(GetAvailableExportPath(folders[0].Path.LocalPath));
        }
        await viewModel.PreviewExportAsync();
        if (viewModel.CurrentExportPlan?.CanExport == true)
            await viewModel.ExecuteExportAsync();
    }

    private static string GetAvailableExportPath(string parentPath)
    {
        var stem = $"VictoryTool Export {DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var candidate = Path.Combine(parentPath, stem);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(parentPath, $"{stem} ({suffix})");
        return candidate;
    }

    private void BatchDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = eventArgs.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void BatchDrop(object? sender, DragEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        foreach (var file in eventArgs.DataTransfer.TryGetFiles() ?? [])
        {
            var path = file.Path.LocalPath;
            if (Path.GetExtension(path).Equals(".vrchara", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.PackageInput = path;
                viewModel.AddPackage();
            }
        }
    }

    private async void SaveCharacterPackage(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel { ActiveDraft: { } draft } viewModel)
            return;

        if (viewModel.HasModificationSource)
        {
            await viewModel.SaveDraftAsync();
            return;
        }

        var language = string.Equals(viewModel.SelectedLanguageCode, "es", StringComparison.OrdinalIgnoreCase)
            ? "Guardar personaje"
            : "Save character";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = language,
            SuggestedFileName = GetSuggestedPackageFileName(draft.DisplayName),
            DefaultExtension = "vrchara",
            FileTypeChoices =
            [
                new FilePickerFileType("VictoryTool character package")
                {
                    Patterns = ["*.vrchara"],
                },
            ],
        });

        if (file is null) return;
        viewModel.DraftPackagePath = file.Path.LocalPath;
        await viewModel.SaveDraftAsync();
    }

    private async void SelectStandardPortrait(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync("Select the normal face", "Victory Road portrait", "*.g4tx", "*.png") is { } path)
            viewModel.ActiveDraftStandardPortraitPath = path;
    }

    private async void SelectUniformPortrait(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync("Select the face without rear hair", "Victory Road portrait", "*.g4tx", "*.png") is { } path)
            viewModel.ActiveDraftUniformPortraitPath = path;
    }

    private async void SelectHeadModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync("Select the 3D head model", "Victory Road 3D model", "*.g4md") is { } path)
            viewModel.ActiveDraftHeadModelPath = path;
    }

    private void SkinColorPickerOpened(object? sender, EventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && sender is Flyout { Content: ColorView picker }
            && Color.TryParse(viewModel.ActiveDraftSkinColor, out var color))
            picker.Color = color;
    }

    private void SkinColorChanged(object? sender, ColorChangedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            var color = eventArgs.NewColor;
            viewModel.ActiveDraftSkinColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }

    private void BeginScreenColorPick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        if (!ScreenColorSampler.IsSupported)
        {
            viewModel.SetUserStatusMessage(viewModel.SelectedLanguageCode == "es"
                ? "El cuentagotas de pantalla no está disponible en esta plataforma."
                : "Screen colour picking is not available on this platform.");
            return;
        }

        _screenColorPickTimer?.Stop();
        _awaitingColorPickRelease = true;
        _screenColorPickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _screenColorPickTimer.Tick += SampleScreenColorOnNextClick;
        _screenColorPickTimer.Start();
        viewModel.SetUserStatusMessage(viewModel.SelectedLanguageCode == "es"
            ? "Haz clic sobre cualquier color de la pantalla para capturarlo."
            : "Click any screen colour to capture it.");
    }

    private void SampleScreenColorOnNextClick(object? sender, EventArgs eventArgs)
    {
        if (_screenColorPickTimer is null) return;
        var isDown = ScreenColorSampler.IsPrimaryButtonDown();
        if (_awaitingColorPickRelease)
        {
            _awaitingColorPickRelease = isDown;
            return;
        }
        if (!isDown) return;

        _screenColorPickTimer.Stop();
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        if (ScreenColorSampler.TrySampleCursor(out var color, out var error))
        {
            viewModel.ActiveDraftSkinColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            viewModel.SetUserStatusMessage(viewModel.SelectedLanguageCode == "es"
                ? "Color de piel capturado."
                : "Skin colour captured.");
            return;
        }
        viewModel.SetUserStatusMessage(error ?? (viewModel.SelectedLanguageCode == "es"
            ? "No se pudo capturar el color de pantalla."
            : "The screen colour could not be captured."));
    }

    private async Task<string?> SelectAppearanceFileAsync(string title, string fileTypeName, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CreateAppearanceFileType(fileTypeName, patterns)],
        });
        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }

    private static FilePickerFileType CreateAppearanceFileType(string fileTypeName, IReadOnlyList<string> patterns)
    {
        // Apple pickers use UTIs instead of glob patterns. Keep the patterns
        // for Windows/Linux, but explicitly advertise PNG so portrait icons are
        // selectable on macOS as well. Unknown game resources (G4TX/G4MD) are
        // represented as generic data because they have no system UTI.
        return new FilePickerFileType(fileTypeName)
        {
            Patterns = patterns.ToArray(),
            AppleUniformTypeIdentifiers = patterns
                .Select(pattern => pattern.Equals("*.png", StringComparison.OrdinalIgnoreCase)
                    ? "public.png"
                    : "public.data")
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static string GetSuggestedPackageFileName(string? displayName)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "character" : displayName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return $"{name}.vrchara";
    }
}
