using VictoryTool.Desktop.Localization;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VictoryTool.Application.Projects;

namespace VictoryTool.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private DispatcherTimer? _screenColorPickTimer;
    private bool _awaitingColorPickRelease;
    private bool _closingApproved;
    private ViewModels.MainWindowViewModel? _observedViewModel;

    private string Ui(string english) => WorkflowText.ForLocale(
        (DataContext as VictoryTool.Desktop.ViewModels.MainWindowViewModel)?.SelectedLanguageCode ?? "en", english);

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => ObserveViewModel();
        Opened += (_, _) => RestoreScreenFocus();
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            _screenColorPickTimer?.Stop();
            if (_observedViewModel is not null)
            {
                _observedViewModel.PropertyChanged -= ViewModelPropertyChanged;
                _observedViewModel.Dispose();
            }
        };
        SizeChanged += (_, args) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                viewModel.UpdateLayoutWidth(args.NewSize.Width);
            }
        };
    }

    private void ObserveViewModel()
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= ViewModelPropertyChanged;
        _observedViewModel = DataContext as ViewModels.MainWindowViewModel;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += ViewModelPropertyChanged;
        Dispatcher.UIThread.Post(RestoreScreenFocus);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ViewModels.MainWindowViewModel.WizardStep)
            or nameof(ViewModels.MainWindowViewModel.IsOnboardingVisible)
            or nameof(ViewModels.MainWindowViewModel.IsMainMenuVisible)
            or nameof(ViewModels.MainWindowViewModel.IsIncorporationVisible)
            or nameof(ViewModels.MainWindowViewModel.IsUnsavedChangesPromptVisible)
            or nameof(ViewModels.MainWindowViewModel.IsRecoveryCloseFailureVisible)
            or nameof(ViewModels.MainWindowViewModel.IsPostSavePromptVisible)
            or nameof(ViewModels.MainWindowViewModel.CanInteract)
            or nameof(ViewModels.MainWindowViewModel.IsBusy)
            or nameof(ViewModels.MainWindowViewModel.CanCancelOperation))
            Dispatcher.UIThread.Post(RestoreScreenFocus);
    }

    private void RestoreScreenFocus()
    {
        if (!IsVisible || DataContext is not ViewModels.MainWindowViewModel viewModel)
            return;
        if (viewModel.IsBusy)
        {
            if (viewModel.CanCancelOperation)
                this.FindControl<Button>("CancelExportButton")?.Focus(NavigationMethod.Tab);
            else
                this.FindControl<Border>("BusyOverlay")?.Focus();
            return;
        }
        var screen = viewModel.IsRecoveryCloseFailureVisible ? "RecoveryCloseFailurePrompt"
            : viewModel.IsUnsavedChangesPromptVisible ? "UnsavedChangesPrompt"
            : viewModel.IsPostSavePromptVisible ? "PostSavePrompt"
            : viewModel.IsOnboardingVisible ? "OnboardingScreen"
            : viewModel.IsMainMenuVisible ? "MainMenuScreen"
            : viewModel.IsIncorporationVisible ? "IncorporationScreen"
            : viewModel.IsCharacterStep ? "CharacterScreen"
            : viewModel.IsDetailsStep ? "DetailsScreen"
            : viewModel.IsAttributesStep ? "AttributesScreen"
            : viewModel.IsSkillsStep ? "SkillsScreen"
            : viewModel.IsAppearanceStep ? "AppearanceScreen"
            : viewModel.IsAcquisitionStep ? "AcquisitionScreen"
            : "ReviewScreen";
        var root = this.FindControl<Control>(screen);
        if (root is null) return;
        var focused = FocusManager?.GetFocusedElement();
        if (root.GetVisualDescendants().Any(control => ReferenceEquals(control, focused)))
            return;
        root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible)
            ?.Focus(NavigationMethod.Tab);
    }

    private void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closingApproved || DataContext is not ViewModels.MainWindowViewModel viewModel)
            return;
        args.Cancel = true;
        var handlingClose = true;
        viewModel.RequestClose(() =>
        {
            _closingApproved = true;
            if (handlingClose)
                args.Cancel = false;
            else
                Close();
        });
        handlingClose = false;
    }

    private async void SelectDumpFolder(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Ui("Select extracted Victory Road data"),
            AllowMultiple = false,
        });

        if (folders.Count == 1 && DataContext is ViewModels.MainWindowViewModel viewModel)
            viewModel.GameDumpInput = folders[0].Path.LocalPath;
    }

    private async void SelectExportParentFolder(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Ui("Select the parent folder for a new VictoryTool export"),
            AllowMultiple = false,
        });

        if (folders.Count != 1 || DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        viewModel.SetGeneratedExportOutputPath(GetAvailableExportPath(folders[0].Path.LocalPath));
    }

    private async void SelectBatchPackage(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Ui("Select a VictoryTool character package"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Ui("VictoryTool character package"))
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
            || button.DataContext is not ViewModels.BatchPackageRow row)
            return;
        viewModel.RemoveBatchEntry(row.Entry);
    }

    private async void SelectModificationPackage(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        await viewModel.LoadModificationWithPickerAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Ui("Select a VictoryTool character package to modify"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Ui("VictoryTool character package")) { Patterns = ["*.vrchara"] }],
            });
            return files.Count == 1 ? files[0].Path.LocalPath : null;
        });
    }

    private void AdvanceWorkflow(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        if (viewModel.IsIncorporationVisible) ExportBatch(sender, eventArgs);
        else viewModel.NextWizardStepCommand.Execute(null);
    }

    private async void ExportBatch(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        await viewModel.ExportBatchAsync(async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Ui("Select the export destination"),
                AllowMultiple = false,
            });
            return folders.Count == 1 ? GetAvailableExportPath(folders[0].Path.LocalPath) : null;
        });
    }

    private void CancelExport(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
            viewModel.CancelExport();
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
        if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
        await viewModel.SaveDraftWithPickerAsync(async draft =>
        {
            var language = Ui("Save character");
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = language,
                SuggestedFileName = GetSuggestedPackageFileName(draft.DisplayName),
                DefaultExtension = "vrchara",
                FileTypeChoices =
                [
                    new FilePickerFileType(Ui("VictoryTool character package"))
                    {
                        Patterns = ["*.vrchara"],
                    },
                ],
            });
            return file?.Path.LocalPath;
        });
    }

    private async void SelectStandardPortrait(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync(viewModel.WizardText.NormalFace, viewModel.WizardText.NormalFace, "*.g4tx", "*.png") is { } path)
            viewModel.ActiveDraftStandardPortraitPath = path;
    }

    private async void SelectUniformPortrait(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync(viewModel.WizardText.FaceWithoutRearHair, viewModel.WizardText.FaceWithoutRearHair, "*.g4tx", "*.png") is { } path)
            viewModel.ActiveDraftUniformPortraitPath = path;
    }

    private async void SelectHeadModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync(viewModel.WizardText.Model3D, viewModel.WizardText.Model3D, "*.g4md") is { } path)
            viewModel.ActiveDraftHeadModelPath = path;
    }

    private async void SelectUniformModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync(viewModel.WizardText.Uniform, viewModel.WizardText.Model3D, "*.g4md") is { } path)
        {
            viewModel.ActiveDraftUniformModelPath = path;
        }
    }

    private async void SelectShoesModel(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel
            && await SelectAppearanceFileAsync(viewModel.WizardText.Shoes, viewModel.WizardText.Model3D, "*.g4md") is { } path)
            viewModel.ActiveDraftShoesModelPath = path;
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
            viewModel.SetUserErrorStatusMessage(Ui("Screen colour picking is not available on this platform."));
            return;
        }

        _screenColorPickTimer?.Stop();
        _awaitingColorPickRelease = true;
        _screenColorPickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _screenColorPickTimer.Tick += SampleScreenColorOnNextClick;
        _screenColorPickTimer.Start();
        viewModel.SetUserStatusMessage(Ui("Click any screen colour to capture it."));
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
            viewModel.SetUserStatusMessage(Ui("Skin colour captured."));
            return;
        }
        viewModel.SetUserErrorStatusMessage(error ?? (Ui("The screen colour could not be captured.")));
    }

    private async Task<string?> SelectAppearanceFileAsync(string title, string fileTypeName, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CreateAppearanceFileType(fileTypeName, patterns)],
        });
        if (files.Count != 1) return null;
        var path = files[0].Path.LocalPath;
        if (patterns.Any(pattern => Path.GetExtension(path).Equals(pattern[1..], StringComparison.OrdinalIgnoreCase)))
            return path;
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            var extensions = string.Join(", ", patterns.Select(pattern => pattern[1..]));
            viewModel.SetUserErrorStatusMessage(string.Format(Ui("Select a file of this type: {0}."), extensions));
        }
        return null;
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
