using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Duplify.Services;

namespace Duplify.Tests;

public sealed class DuplicateActionGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Duplify_Guard_" + Guid.NewGuid().ToString("N"));
    private readonly DuplicateActionGuard _guard = new();
    private static readonly byte[] Content = [1, 2, 3, 4, 5];
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Content));

    public DuplicateActionGuardTests() => Directory.CreateDirectory(_root);

    private string Copy(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Content);
        return path;
    }

    [Fact]
    public async Task ValidLease_BlocksWritesAndKeeperDeletion_ButAllowsCandidateMove()
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        using (var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper]))
        {
            Assert.True(lease.TryConfirmCurrent(out _));
            Assert.Throws<IOException>(() => File.WriteAllBytes(candidate, [9, 9, 9, 9, 9]));
            Assert.Throws<IOException>(() => File.WriteAllBytes(keeper, [9, 9, 9, 9, 9]));
            Assert.Throws<IOException>(() => File.Delete(keeper));
            File.Move(candidate, Path.Combine(_root, "moved.bin"));
        }
        Assert.Equal(Content, File.ReadAllBytes(keeper));
        File.WriteAllBytes(keeper, [9]); // All locks were released.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameSizeSameTimestampContentChange_IsRejected(bool changeCandidate)
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        string changed = changeCandidate ? candidate : keeper;
        DateTime originalTime = File.GetLastWriteTimeUtc(changed);
        File.WriteAllBytes(changed, [5, 4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(changed, originalTime);
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper]);
        Assert.False(lease.IsValid);
        Assert.True(File.Exists(candidate));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingCandidateOrKeeper_IsRejected(bool removeCandidate)
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        File.Delete(removeCandidate ? candidate : keeper);
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper]);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public async Task MissingOrChangedKeeper_DoesNotHideAnotherValidCopy()
    {
        string candidate = Copy("selected.bin"), changed = Copy("changed.bin"), keeper = Copy("keeper.bin");
        File.WriteAllBytes(changed, [8, 8, 8, 8, 8]);
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash,
            [Path.Combine(_root, "missing.bin"), changed, keeper]);
        Assert.True(lease.IsValid);
    }

    [Fact]
    public async Task NoUnselectedCopy_AndSamePathAlias_AreRejected()
    {
        string candidate = Copy("selected.bin");
        using var empty = await _guard.AcquireAsync(candidate, Content.Length, Hash, []);
        using var samePath = await _guard.AcquireAsync(candidate, Content.Length, Hash,
            [Path.Combine(_root, ".", "selected.bin")]);
        Assert.False(empty.IsValid);
        Assert.False(samePath.IsValid);
    }

    [Fact]
    public async Task HardLink_IsNotAnIndependentSurvivingCopy()
    {
        string candidate = Copy("selected.bin"), alias = Path.Combine(_root, "alias.bin");
        Assert.True(CreateHardLink(alias, candidate, IntPtr.Zero));
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [alias]);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public async Task ReplacementAfterHash_IsRejectedByFinalIdentityCheck()
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper]);
        Assert.True(lease.IsValid);
        File.Move(candidate, Path.Combine(_root, "old.bin"));
        File.WriteAllBytes(candidate, Content);
        Assert.False(lease.TryConfirmCurrent(out _));
    }

    [Fact]
    public async Task ActiveWriter_IsRejectedEvenWhenContentStillMatches()
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        using var writer = new FileStream(keeper, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var lease = await _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper]);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public async Task CancelledValidation_DoesNotLeaveHandlesOpen()
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _guard.AcquireAsync(candidate, Content.Length, Hash, [keeper], cancellation.Token));
        using var writer = new FileStream(candidate, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task CancellationAfterCandidateHash_ReleasesAcquiredCandidateHandle()
    {
        string candidate = Copy("selected.bin"), keeper = Copy("keeper.bin");
        using var cancellation = new CancellationTokenSource();
        // Enumeration starts only after candidate hashing. Cancel at that deterministic
        // boundary, while its validated handle is owned by AcquireAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _guard.AcquireAsync(candidate,
            Content.Length, Hash, new CancellingPaths(keeper, cancellation), cancellation.Token));
        using var writer = new FileStream(candidate, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    private sealed class CancellingPaths(string path, CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => path;
        public IEnumerator<string> GetEnumerator()
        {
            cancellation.Cancel();
            yield return path;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
