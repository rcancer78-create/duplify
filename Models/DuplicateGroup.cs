using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplify.Utilities;

namespace Duplify.Models;

public partial class DuplicateGroup : ObservableObject
{
    public string Hash { get; }
    public long FileSize { get; }
    public string SizeFormatted { get; }

    public ObservableCollection<DuplicateFileItem> Files { get; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _countFormatted = string.Empty;

    [ObservableProperty]
    private long _wastedSpace;

    [ObservableProperty]
    private string _wastedSpaceFormatted = string.Empty;

    [ObservableProperty]
    private string _sampleFileName = string.Empty;

    public DuplicateGroup(string hash, long fileSize, IEnumerable<DuplicateFileItem> files)
    {
        Hash = hash;
        FileSize = fileSize;
        SizeFormatted = FormatUtils.FormatBytes(fileSize);
        Files = new ObservableCollection<DuplicateFileItem>(files);

        UpdateMetrics();
    }

    public void UpdateMetrics()
    {
        Count = Files.Count;
        CountFormatted = FormatUtils.PluralizeFiles(Count);
        WastedSpace = Count > 1 ? (Count - 1) * FileSize : 0;
        WastedSpaceFormatted = FormatUtils.FormatBytes(WastedSpace);
        SampleFileName = Files.FirstOrDefault()?.FileName ?? string.Empty;
    }
}
