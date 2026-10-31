using Avalonia.Media.Imaging;
using VictoryTool.Application.Projects;
using VictoryTool.Application.Packages;
using VictoryTool.Application.Text;
using VictoryTool.Desktop.Localization;
using VictoryTool.Desktop.Assets;

namespace VictoryTool.Desktop.ViewModels;

public sealed class BatchPackageRow : ObservableObject, IDisposable
{
    private CharacterPortraitLoadResult? _portrait;
    private VrCharaManifest? _manifest;
    private string _language;
    private string _status = "loading";

    public BatchPackageRow(BatchEntry entry, string language)
    {
        Entry = entry;
        _language = language;
    }

    public BatchEntry Entry { get; private set; }
    public Guid Id => Entry.Id;
    public string PackagePath => Entry.PackagePath;
    public string DisplayName
    {
        get
        {
            var name = new[]
            {
                _manifest?.Localizations?.GetValueOrDefault(_language)?.LocalizedName,
                _manifest?.Character.Localization?.Locales.GetValueOrDefault(_language)?.LocalizedName,
                _manifest?.Character.DisplayName,
            }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? Path.GetFileNameWithoutExtension(PackagePath);
            return RubyTextDocumentViewModel.From(GameTextParser.Parse(name)).PlainText;
        }
    }
    public Bitmap? Thumbnail => _portrait?.Bitmap;
    public bool HasThumbnail => Thumbnail is not null;
    public bool IsLoading => _status == "loading";
    public string Status => WorkflowText.ForLocale(_language, _status switch
    {
        "loading" => "Loading package…",
        "unreadable" => "Could not read the package. Check the file and add it again.",
        "missing" => "No thumbnail available.",
        "preview_failed" => "Could not load the thumbnail.",
        _ => string.Empty,
    });

    internal CancellationTokenSource Cancellation { get; } = new();
    internal bool IsDisposed { get; private set; }

    internal void UpdateEntry(BatchEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(Entry));
    }

    internal void SetLanguage(string language)
    {
        _language = language;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DisplayName));
    }

    internal void SetMetadata(VrCharaManifest manifest)
    {
        _manifest = manifest;
        OnPropertyChanged(nameof(DisplayName));
    }

    internal void Complete(CharacterPortraitLoadResult? portrait, string status)
    {
        var previous = _portrait;
        _portrait = portrait;
        _status = status;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(Status));
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Cancellation.Cancel();
        Cancellation.Dispose();
        Complete(null, "missing");
    }
}
