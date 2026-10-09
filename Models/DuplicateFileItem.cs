using System;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplify.Utilities;

namespace Duplify.Models;

public partial class DuplicateFileItem : ObservableObject
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    public string FilePath { get; }
    public string FileName { get; }
    public string DirectoryName { get; }
    public long Size { get; }
    public string SizeFormatted { get; }
    public DateTime LastModified { get; }
    public DateTime LastModifiedUtc { get; }
    public string LastModifiedFormatted { get; }
    public bool IsImage { get; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    public DuplicateFileItem(string filePath, long size, DateTime lastModifiedUtc)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        DirectoryName = Path.GetDirectoryName(filePath) ?? string.Empty;
        Size = size;
        SizeFormatted = FormatUtils.FormatBytes(size);
        LastModifiedUtc = lastModifiedUtc;
        LastModified = lastModifiedUtc.ToLocalTime();
        LastModifiedFormatted = LastModified.ToString("dd.MM.yyyy HH:mm:ss");

        string ext = Path.GetExtension(filePath);
        IsImage = ImageExtensions.Contains(ext);
    }
}
