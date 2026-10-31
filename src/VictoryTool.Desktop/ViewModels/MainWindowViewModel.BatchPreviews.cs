using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Threading;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Packages;
using VictoryTool.Desktop.Assets;

namespace VictoryTool.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _batchPreviewSlots = new(2, 2);
    private bool _batchPreviewRefreshQueued;
    private readonly Dictionary<Guid, BatchPackageRow> _batchRowsById = [];

    private ICommand? _refreshBatchPreviewsCommand;
    public ICommand RefreshBatchPreviewsCommand => _refreshBatchPreviewsCommand ??= new DelegateCommand(RefreshBatchPreviews);
    public string RefreshBatchPreviewsLabel => Workflow("Refresh packages");

    private void RefreshBatchPreviews()
    {
        if (_disposed || IsBusy) return;
        foreach (var row in BatchRows) row.Dispose();
        BatchRows.Clear();
        _batchRowsById.Clear();
        RefreshBatchRows();
    }

    public ObservableCollection<BatchPackageRow> BatchRows { get; } = [];
    public BatchPackageRow? SelectedBatchRow
    {
        get => SelectedBatchEntry is { } entry ? _batchRowsById.GetValueOrDefault(entry.Id) : null;
        set => SelectedBatchEntry = value?.Entry;
    }

    private void InitializeBatchPreviews()
    {
        Batch.CollectionChanged += (_, _) =>
        {
            if (_batchPreviewRefreshQueued) return;
            _batchPreviewRefreshQueued = true;
            // ReplaceWith emits a clear followed by additions; reconcile the completed snapshot.
            Dispatcher.UIThread.Post(() =>
            {
                _batchPreviewRefreshQueued = false;
                RefreshBatchRows();
            });
        };
        PropertyChanged += BatchPreviewPropertyChanged;
        RefreshBatchRows();
    }

    private void BatchPreviewPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SelectedBatchEntry))
            OnPropertyChanged(nameof(SelectedBatchRow));
        else if (args.PropertyName == nameof(SelectedLanguageCode))
            foreach (var row in BatchRows) row.SetLanguage(SelectedLanguageCode);
    }

    private void RefreshBatchRows()
    {
        if (_disposed) return;
        var entries = Batch.ToDictionary(entry => entry.Id);
        foreach (var row in BatchRows.ToArray())
        {
            if (entries.TryGetValue(row.Id, out var entry)
                && string.Equals(entry.PackagePath, row.PackagePath, StringComparison.Ordinal)) continue;
            _batchRowsById.Remove(row.Id);
            BatchRows.Remove(row);
            row.Dispose();
        }
        for (var index = 0; index < Batch.Count; index++)
        {
            var entry = Batch[index];
            if (!_batchRowsById.TryGetValue(entry.Id, out var row))
            {
                row = new BatchPackageRow(entry, SelectedLanguageCode);
                _batchRowsById.Add(entry.Id, row);
                BatchRows.Insert(index, row);
                _ = LoadBatchPreviewAsync(row);
            }
            else
            {
                row.UpdateEntry(entry);
                var currentIndex = BatchRows.IndexOf(row);
                if (currentIndex != index) BatchRows.Move(currentIndex, index);
            }
        }
        OnPropertyChanged(nameof(SelectedBatchRow));
    }

    private bool IsCurrentBatchRow(BatchPackageRow row) =>
        !row.IsDisposed
        && _batchRowsById.TryGetValue(row.Id, out var current) && ReferenceEquals(current, row)
        && Batch.Any(entry => entry.Id == row.Id && entry.PackagePath == row.PackagePath);

    private async Task LoadBatchPreviewAsync(BatchPackageRow row)
    {
        var token = row.Cancellation.Token;
        CharacterPortraitLoadResult? portrait = null;
        var acquired = false;
        var hasManifest = false;
        try
        {
            await _batchPreviewSlots.WaitAsync(token);
            acquired = true;
            if (_packageService is null)
            {
                row.Complete(null, "unreadable");
                return;
            }
            // ZIP metadata parsing and resource decompression must not occupy the UI caller.
            var manifest = await Task.Run(() => _packageService.LoadMetadataAsync(row.PackagePath, token), token);
            if (!IsCurrentBatchRow(row)) return;
            hasManifest = true;
            row.SetMetadata(manifest);
            if (_portraitLoader is not null)
                portrait = await LoadBatchThumbnailAsync(row.PackagePath, manifest, token);
            if (!IsCurrentBatchRow(row)) return;
            row.Complete(portrait, portrait?.Bitmap is not null ? "ready"
                : portrait is null ? "missing" : "preview_failed");
            portrait = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            GlobalLog.Warn("batch_preview_failed", exception: exception);
            if (IsCurrentBatchRow(row)) row.Complete(null, hasManifest ? "preview_failed" : "unreadable");
        }
        finally
        {
            portrait?.Dispose();
            if (acquired) _batchPreviewSlots.Release();
        }
    }

    private async Task<CharacterPortraitLoadResult?> LoadBatchThumbnailAsync(
        string packagePath, VrCharaManifest manifest, CancellationToken token)
    {
        const long maximumPortraitBytes = 16 * 1024 * 1024;
        var path = manifest.Character.Assets?.StandardPortraitPath
            ?? manifest.Character.Assets?.UniformPortraitPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        var resource = manifest.Resources.FirstOrDefault(item => item.VirtualPath == path);
        // Previews only read declared embedded resources, never arbitrary manifest filesystem paths.
        if (resource is null || resource.ByteLength > maximumPortraitBytes) return null;
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".g4tx", StringComparison.OrdinalIgnoreCase)) return null;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"victory-portrait-{Guid.NewGuid():N}{extension}");
        CharacterPortraitLoadResult? result = null;
        try
        {
            await Task.Run(async () =>
            {
                using var archive = ZipFile.OpenRead(packagePath);
                var matching = archive.Entries.Where(entry => entry.FullName == $"resources/{path}").Take(2).ToArray();
                if (matching.Length != 1 || matching[0].Length != resource.ByteLength)
                    throw new InvalidDataException("Missing or invalid package portrait entry.");
                await using var source = matching[0].Open();
                using var content = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    if (content.Length + count > maximumPortraitBytes || content.Length + count > resource.ByteLength)
                        throw new InvalidDataException("Package portrait exceeds its declared size.");
                    content.Write(buffer, 0, count);
                }
                if (content.Length != resource.ByteLength
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(content.GetBuffer().AsSpan(0, (int)content.Length))),
                        resource.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package portrait checksum mismatch.");
                await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), token);
            }, token);
            var character = new CharacterCatalogItem(manifest.Character.SymbolicId, manifest.Character.DisplayName,
                CharacterDataConfidence.Parsed, temporaryPath, StandardPortraitResourcePath: temporaryPath);
            result = await _portraitLoader!.LoadAsync(new CharacterPortraitRequest(character, CharacterPortraitKind.RosterThumbnail), token);
            return result;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch
            {
                result?.Dispose();
                throw;
            }
        }
    }
}
