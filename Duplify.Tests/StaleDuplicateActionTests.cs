using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using Duplify.Models;
using Duplify.Services;
using Duplify.ViewModels;

namespace Duplify.Tests;

public sealed class StaleDuplicateActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Duplify_StaleAction_" + Guid.NewGuid().ToString("N"));
    private readonly RecordingActions _actions = new();
    private readonly Dialogs _dialogs = new();
    private readonly Scanner _scanner = new();
    private readonly Journal _journal = new();
    private static readonly byte[] Content = [1, 2, 3, 4, 5];

    public StaleDuplicateActionTests() => Directory.CreateDirectory(_root);

    private (MainViewModel Vm, DuplicateGroup Group) Create(int selectedCount = 1)
    {
        var files = Enumerable.Range(0, selectedCount + 1).Select(i =>
        {
            string path = Path.Combine(_root, $"file-{i}.bin");
            File.WriteAllBytes(path, Content);
            return new DuplicateFileItem(path, Content.Length, File.GetLastWriteTimeUtc(path)) { IsChecked = i < selectedCount };
        }).ToArray();
        var group = new DuplicateGroup(Convert.ToHexString(SHA256.HashData(Content)), Content.Length, files);
        var vm = new MainViewModel(_scanner, _actions, new Settings(), new Thumbnails(), _dialogs, _journal);
        vm.Groups.Add(group);
        vm.HasScanned = true;
        vm.SelectedFolder = _root;
        _dialogs.Folder = Path.Combine(_root, "destination");
        return (vm, group);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSelectedFile_NeverReachesDeleteOrMove(bool move)
    {
        var (vm, group) = Create();
        File.WriteAllBytes(group.Files[0].FilePath, [9, 8, 7, 6, 5]);
        if (move) await vm.MoveSelectedCommand.ExecuteAsync(null);
        else await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Empty(_actions.Calls);
        Assert.Contains("safety_skip", _journal.Operations);
        Assert.Equal(2, group.Files.Count);
        Assert.False(vm.IsResolving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectingEveryCopy_NeverMutatesFiles(bool move)
    {
        var (vm, group) = Create();
        foreach (var file in group.Files) file.IsChecked = true;
        if (move) await vm.MoveSelectedCommand.ExecuteAsync(null);
        else await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Empty(_actions.Calls);
        Assert.All(group.Files, file => Assert.True(File.Exists(file.FilePath)));
        Assert.False(vm.IsResolving);
    }

    [Fact]
    public async Task PermanentDelete_RevalidatesKeeperAfterConfirmation()
    {
        var (vm, group) = Create();
        _actions.RecycleSucceeds = false;
        _dialogs.OnPermanent = () => File.WriteAllBytes(group.Files[1].FilePath, [9, 8, 7, 6, 5]);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["recycle"], _actions.Calls);
        Assert.Contains("safety_skip", _journal.Operations);
        Assert.True(File.Exists(group.Files[0].FilePath));
    }

    [Fact]
    public async Task LaterCandidate_RechecksKeeperAfterEarlierFailedAttempt()
    {
        var (vm, group) = Create(selectedCount: 2);
        string keeper = group.Files[2].FilePath;
        _actions.RecycleSucceeds = false;
        _dialogs.OnPermanent = () => File.Delete(keeper);
        _dialogs.PermanentAnswer = false;
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["recycle"], _actions.Calls);
        Assert.True(File.Exists(group.Files[0].FilePath));
        Assert.True(File.Exists(group.Files[1].FilePath));
    }

    [Fact]
    public async Task ConfirmationCannotReplaceScanOrTurnSelectedFileIntoKeeper()
    {
        var (vm, group) = Create(selectedCount: 2);
        Task? scanAttempt = null;
        _dialogs.OnConfirm = () =>
        {
            scanAttempt = vm.StartScanAsync();
            Assert.True(scanAttempt.IsCompletedSuccessfully);
            group.Files[1].IsChecked = false;
            File.Delete(group.Files[2].FilePath);
        };
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.NotNull(scanAttempt);
        await scanAttempt;
        Assert.Equal(0, _scanner.Calls);
        Assert.Empty(_actions.Calls);
        Assert.Equal(3, group.Files.Count);
    }

    [Fact]
    public async Task EscapeDuringConfirmation_CancelsWithoutFileActions()
    {
        var (vm, _) = Create();
        _dialogs.OnConfirm = vm.CancelScan;
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Empty(_actions.Calls);
        Assert.False(vm.IsResolving);
        Assert.Contains("Пропущено", _dialogs.Summary);
    }

    private sealed class RecordingActions : IFileActionService
    {
        public readonly List<string> Calls = [];
        public bool RecycleSucceeds = true;
        public bool TrySendToRecycleBin(string filePath, out string? error)
        {
            Calls.Add("recycle");
            error = RecycleSucceeds ? null : "simulated recycle failure";
            return RecycleSucceeds;
        }
        public bool DeletePermanently(string filePath, out string? error) { Calls.Add("permanent"); error = null; return true; }
        public bool MoveFile(string sourcePath, string destinationDirectory, out string finalPath, out string? error)
        { Calls.Add("move"); finalPath = sourcePath; error = null; return true; }
        public void OpenInExplorer(string filePath) { }
        public void OpenFile(string filePath) { }
        public bool OpenJournal(string path) => false;
        public bool OpenFolder(string path) => false;
    }

    private sealed class Dialogs : IDialogService
    {
        public string? Folder;
        public Action? OnConfirm;
        public Action? OnPermanent;
        public bool PermanentAnswer = true;
        public string Summary = string.Empty;
        public string? PickFolder(string title, string? initialFolder = null) => Folder;
        public bool Confirm(string message, string title) => true;
        public bool ConfirmDanger(string message, string title) { OnConfirm?.Invoke(); return true; }
        public bool ConfirmLastCopy(string message, string title) => true;
        public bool ConfirmPermanentDelete(string message, string title) { OnPermanent?.Invoke(); return PermanentAnswer; }
        public void ShowInfo(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool ShowActionSummary(string message, string title) { Summary = message; return false; }
        public BulkSelectionPlan? ShowBulkResolve(IReadOnlyList<DuplicateGroup> groups) => null;
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings LoadSettings() => new();
        public void SaveSettings(AppSettings settings) { }
    }
    private sealed class Thumbnails : IImageThumbnailService
    {
        public Task<ImageSource?> LoadThumbnailAsync(string path, int targetPixelWidth = 160, CancellationToken cancellationToken = default)
            => Task.FromResult<ImageSource?>(null);
    }
    private sealed class Scanner : IScannerService
    {
        public int Calls;
        public Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new ScanResult()); }
    }
    private sealed class Journal : IActionJournal
    {
        public readonly List<string> Operations = [];
        public string JournalFilePath => string.Empty;
        public event Action<string>? WriteFailed { add { } remove { } }
        public void Record(string operation, string path, bool success, string message, string? extra = null) => Operations.Add(operation);
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
