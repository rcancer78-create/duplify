using System.IO;
using System.Security.Cryptography;

namespace Duplify.Services;

/// <summary>
/// Revalidates the scan evidence, keeping a verified, unselected copy read-locked until the
/// action finishes. This is deliberately not an atomic filesystem transaction: path-based
/// shell/move APIs still allow an external rename/path substitution after the final identity check.
/// </summary>
public sealed class DuplicateActionGuard
{
    public async Task<DuplicateActionLease> AcquireAsync(string candidatePath, long expectedSize,
        string expectedHash, IReadOnlyList<string> retainedPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? candidate = null;
        FileStream? retained = null;
        try
        {
            candidate = await OpenVerifiedAsync(candidatePath, expectedSize, expectedHash,
                FileShare.Read | FileShare.Delete, cancellationToken);
            // This reference is immutable throughout keeper discovery; the nullable local
            // only tracks whether the outer finally still owns disposal.
            FileStream verifiedCandidate = candidate;
            if (!FileIdentityProvider.TryGetIdentity(verifiedCandidate.SafeFileHandle, out var candidateIdentity))
                throw new IOException("Не удалось подтвердить идентичность выбранного файла.");

            foreach (string retainedPath in retainedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(retainedPath),
                        StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // No write/delete sharing: an existing writer makes validation fail; a new
                    // writer or deletion of the keeper is blocked for the lifetime of the lease.
                    retained = await OpenVerifiedAsync(retainedPath, expectedSize, expectedHash,
                        FileShare.Read, cancellationToken);
                    if (!FileIdentityProvider.TryGetIdentity(retained.SafeFileHandle, out var retainedIdentity)
                        || retainedIdentity == candidateIdentity)
                    {
                        retained.Dispose();
                        retained = null;
                        continue;
                    }

                    var lease = new DuplicateActionLease(candidatePath, verifiedCandidate, candidateIdentity,
                        retainedPath, retained, retainedIdentity);
                    // Keep ownership in this scope until confirmation succeeds. A rejected
                    // lease is disposed by the outer finally; only a returned lease owns handles.
                    if (!lease.TryConfirmCurrent(out string? error))
                        return DuplicateActionLease.Rejected(error!);
                    candidate = null;
                    retained = null;
                    return lease;
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    retained?.Dispose();
                    retained = null;
                    // One missing/changed keeper does not hide another valid unselected copy.
                }
            }

            return DuplicateActionLease.Rejected(
                "Нет подтверждённой неизменённой копии вне выбранных файлов. Снимите отметку с одной копии и повторите сканирование.");
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return DuplicateActionLease.Rejected("Повторная проверка файла не пройдена: " + ex.Message);
        }
        finally
        {
            retained?.Dispose();
            candidate?.Dispose();
        }
    }

    private static async Task<FileStream> OpenVerifiedAsync(string path, long size, string hash,
        FileShare sharing, CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, sharing, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != size) throw new IOException("Размер изменился после сканирования.");
            byte[] actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (stream.Length != size || !Convert.ToHexString(actualHash).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Содержимое изменилось после сканирования.");
            cancellationToken.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Путь больше не является обычным файлом.");
    }

    internal static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
}

public sealed class DuplicateActionLease : IDisposable
{
    private readonly string? _candidatePath;
    private readonly FileStream? _candidate;
    private readonly FileIdentity _candidateIdentity;
    private readonly string? _retainedPath;
    private readonly FileStream? _retained;
    private readonly FileIdentity _retainedIdentity;
    private bool _disposed;
    public string? Error { get; }
    public bool IsValid => Error is null && !_disposed;

    internal DuplicateActionLease(string candidatePath, FileStream candidate, FileIdentity candidateIdentity,
        string retainedPath, FileStream retained, FileIdentity retainedIdentity)
    {
        _candidatePath = candidatePath;
        _candidate = candidate;
        _candidateIdentity = candidateIdentity;
        _retainedPath = retainedPath;
        _retained = retained;
        _retainedIdentity = retainedIdentity;
    }

    private DuplicateActionLease(string error) => Error = error;
    internal static DuplicateActionLease Rejected(string error) => new(error);

    /// <summary>Must be called immediately before the path-based action, with the lease still held.</summary>
    public bool TryConfirmCurrent(out string? error)
    {
        error = Error;
        if (!IsValid)
        {
            error ??= "Повторная проверка уже завершена.";
            return false;
        }
        try
        {
            DuplicateActionGuard.RejectReparsePoint(_candidatePath!);
            DuplicateActionGuard.RejectReparsePoint(_retainedPath!);
            if (!FileIdentityProvider.TryGetIdentity(_candidatePath!, out var candidateIdentity)
                || candidateIdentity != _candidateIdentity
                || !FileIdentityProvider.TryGetIdentity(_retainedPath!, out var retainedIdentity)
                || retainedIdentity != _retainedIdentity)
                throw new IOException("Путь изменился после повторной проверки. Повторите сканирование.");
            return true;
        }
        catch (Exception ex) when (DuplicateActionGuard.IsFileFailure(ex))
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retained?.Dispose();
        _candidate?.Dispose();
    }
}
