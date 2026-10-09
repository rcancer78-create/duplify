using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Duplify.Services;

public interface IImageThumbnailService
{
    Task<ImageSource?> LoadThumbnailAsync(string filePath, int targetPixelWidth = 160, CancellationToken cancellationToken = default);
}

public class ImageThumbnailService : IImageThumbnailService
{
    private readonly int _maxCacheSize;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lruList = new();

    private sealed record CacheEntry(string Path, ImageSource Source);

    public ImageThumbnailService(int maxCacheSize = 500)
    {
        _maxCacheSize = Math.Max(10, maxCacheSize);
    }

    public async Task<ImageSource?> LoadThumbnailAsync(string filePath, int targetPixelWidth = 160, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(filePath, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Source;
            }
        }

        var source = await Task.Run(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return null;
                if (!File.Exists(filePath)) return null;

                // Open stream directly instead of reading all bytes into a managed byte array
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (cancellationToken.IsCancellationRequested) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = targetPixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // Freeze allows cross-thread UI consumption

                return (ImageSource)bitmap;
            }
            catch
            {
                // Silently return null for corrupt or unsupported images (e.g. webp without WIC codec)
                return null;
            }
        }, cancellationToken);

        if (!cancellationToken.IsCancellationRequested && source != null)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(filePath, out var existingNode))
                {
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                }
                else
                {
                    if (_cache.Count >= _maxCacheSize)
                    {
                        var oldest = _lruList.Last;
                        if (oldest != null)
                        {
                            _cache.Remove(oldest.Value.Path);
                            _lruList.RemoveLast();
                        }
                    }
                    var newNode = new LinkedListNode<CacheEntry>(new CacheEntry(filePath, source));
                    _lruList.AddFirst(newNode);
                    _cache[filePath] = newNode;
                }
            }
        }
        return source;
    }
}
