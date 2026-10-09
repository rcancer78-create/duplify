using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Duplify.Services;

/// <summary>
/// Stable identity of a file on a volume: volume serial number plus the NTFS file index.
/// Two paths that resolve to the same identity are hard links of one physical file.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

public static class FileIdentityProvider
{
    // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    /// <summary>
    /// Reads the (volume serial, file index) identity through an open handle.
    /// Returns false whenever the identity is unavailable (unsupported volume, locked file, ...)
    /// so the caller can fall back to path-only behavior without failing the scan.
    /// </summary>
    public static bool TryGetIdentity(string path, out FileIdentity identity)
    {
        identity = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            return TryGetIdentity(handle, out identity);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads identity from the exact handle whose content the caller has validated.</summary>
    public static bool TryGetIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (handle.IsInvalid || handle.IsClosed || !OperatingSystem.IsWindows()) return false;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo info,
                (uint)Marshal.SizeOf<FileIdInfo>())) return false;
        identity = new FileIdentity(info.VolumeSerialNumber, info.FileId.Low, info.FileId.High);
        return true;
    }
}
