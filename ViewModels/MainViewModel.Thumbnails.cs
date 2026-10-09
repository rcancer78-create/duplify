using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplify.Models;
using Duplify.Services;

namespace Duplify.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _thumbnailCts;
    private readonly SemaphoreSlim _thumbnailSemaphore = new(3, 3);
    private const int MaxInitialThumbnailsPerGroup = 25;

    partial void OnSelectedGroupChanged(DuplicateGroup? value)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        if (value != null)
        {
            _thumbnailCts = new CancellationTokenSource();
            _ = LoadInitialThumbnailsForGroup(value, _thumbnailCts.Token);
        }
    }

    private async Task LoadInitialThumbnailsForGroup(DuplicateGroup group, CancellationToken token)
    {
        var imageFiles = group.Files
            .Where(f => f.IsImage && f.Thumbnail == null)
            .Take(MaxInitialThumbnailsPerGroup)
            .ToList();

        foreach (var file in imageFiles)
        {
            if (token.IsCancellationRequested || SelectedGroup != group)
            {
                break;
            }

            try
            {
                await LoadThumbnailForFileAsync(file, group, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("LoadInitialThumbnailsForGroup", ex);
            }
        }
    }

    public void RequestThumbnailLoad(DuplicateFileItem file)
    {
        if (!file.IsImage || file.Thumbnail != null || file.IsLoadingThumbnail)
        {
            return;
        }

        var currentGroup = SelectedGroup;
        if (currentGroup == null || !currentGroup.Files.Contains(file))
        {
            return;
        }

        var cts = _thumbnailCts;
        if (cts == null || cts.IsCancellationRequested)
        {
            return;
        }

        var token = cts.Token;
        _ = LoadThumbnailForFileAsync(file, currentGroup, token);
    }

    private async Task LoadThumbnailForFileAsync(DuplicateFileItem file, DuplicateGroup group, CancellationToken token)
    {
        if (file.Thumbnail != null || !file.IsImage || file.IsLoadingThumbnail)
        {
            return;
        }

        if (token.IsCancellationRequested || SelectedGroup != group)
        {
            return;
        }

        file.IsLoadingThumbnail = true;
        try
        {
            await _thumbnailSemaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested || SelectedGroup != group || file.Thumbnail != null)
                {
                    return;
                }

                var image = await _thumbnailService.LoadThumbnailAsync(file.FilePath, 160, token);
                if (!token.IsCancellationRequested && SelectedGroup == group && image != null)
                {
                    file.Thumbnail = image;
                }
            }
            finally
            {
                _thumbnailSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user quickly changes selection
        }
        catch
        {
            // Silently swallow errors for corrupted images
        }
        finally
        {
            file.IsLoadingThumbnail = false;
        }
    }
}
